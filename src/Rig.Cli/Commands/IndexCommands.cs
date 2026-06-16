using System.CommandLine;
using Rig.Analysis;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Domain.Data;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.EntryPoints.EntryPointContext;

namespace Rig.Cli.Commands;

// The store WRITERS: `index` (analyze a solution/project into a fresh or merged store), `mine` (BFS-index a
// dependency closure in parallel), and `graph` (rebuild the derived call-graph views). These create/mutate
// the .rig store rather than query it.
internal static class IndexCommands
{
    internal static Command BuildIndex(TextWriter output, TextWriter error, string workingDirectory)
    {
        var target = CommonOptions.Pattern("solution", "Solution (.slnx/.sln/.slnf) or project (.csproj) to index.");
        var rules = CommonOptions.Rules();
        var identity = new Option<string?>("--identity") { Description = "Store identity for an append (multi-solution) index." };
        var from = new Option<string?>("--from") { Description = "Index only the entry project's transitive closure (one workspace)." };
        var parallelism = new Option<int?>("--parallelism") { Description = "Max concurrent project analyses." };
        var merge = new Option<bool>("--merge") { Description = "Accumulate into an existing store (multi-solution unified store)." };
        var includeTests = new Option<bool>("--include-tests") { Description = "Keep test projects (excluded by default)." };

        var cmd = new Command("index", "Index a solution/project into a .rig store.")
        {
            target,
            rules,
            identity,
            from,
            parallelism,
            merge,
            includeTests,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunIndexAsync(
                        pr.GetValue(target)!,
                        CommonOptions.RulesOf(pr.GetValue(rules)),
                        pr.GetValue(identity),
                        pr.GetValue(from) is { } f ? Path.GetFullPath(f) : null,
                        pr.GetValue(parallelism),
                        pr.GetValue(merge),
                        pr.GetValue(includeTests),
                        output,
                        error,
                        workingDirectory
                    )
            )
        );
        return cmd;
    }

    internal static async Task<int> RunIndexAsync(
        string target,
        IReadOnlyList<string> extraRules,
        string? identity,
        string? fromProject,
        int? parallelism,
        bool merge,
        bool includeTests,
        TextWriter output,
        TextWriter error,
        string workingDirectory
    )
    {
        // --from <csproj>: index only the transitive ProjectReference closure of the entry project
        // (minus test projects) in ONE cross-project Roslyn workspace — skips every out-of-closure
        // test/tool project before its design-time build runs. The closure is written to
        // relevant-projects.json next to the .rig store.
        IReadOnlySet<string>? scopeProjectPaths = null;
        if (fromProject is not null)
        {
            scopeProjectPaths = await BuildEntryClosureAsync(target, fromProject, workingDirectory, output, error);
            if (scopeProjectPaths is null)
                return 2;
        }

        var totalWatch = System.Diagnostics.Stopwatch.StartNew();
        AnalysisResult result;
        var analyzeWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            output.WriteLine($"Indexing: {Path.GetFullPath(target)}");
            if (extraRules.Count > 0)
                output.WriteLine($"Rules: {string.Join(", ", extraRules)}");
            if (identity is not null)
                output.WriteLine($"Identity: {identity}");
            if (fromProject is not null)
                output.WriteLine($"From (closure): {fromProject}  ->  {scopeProjectPaths!.Count} project(s)");
            if (parallelism is not null)
                output.WriteLine($"Parallelism: {parallelism}");
            result = await SolutionAnalyzer.AnalyzeAsync(
                target,
                progress: message => output.WriteLine($"Progress: {message}"),
                extraRulesPaths: extraRules.Count > 0 ? extraRules : null,
                projectIdentity: identity,
                scopeProjectPaths: scopeProjectPaths,
                parallelism: parallelism,
                // Tests are EXCLUDED by default (they add graph width, not reach); `--include-tests` opts
                // them back in.
                excludeTests: !includeTests
            );
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // IOException/FileNotFoundException: the solution/project path doesn't exist or can't be read
            // (a clean "Failed to load" beats an uncaught stack trace). InvalidOperationException: the
            // workspace couldn't load/bind the target.
            error.WriteLine("Failed to load solution/project for analysis.");
            error.WriteLine(exception.Message);
            error.WriteLine("Ensure the target solution has been restored and builds successfully, then retry.");
            error.WriteLine($"  dotnet restore {target}");
            error.WriteLine($"  dotnet build {target}");
            return 2;
        }
        analyzeWatch.Stop();
        output.WriteLine($"Progress: Analysis phase done in {FormatElapsed(analyzeWatch.Elapsed)}");

        // Publish model. A standalone `index` is a full REPLACE published via write-to-temp +
        // atomic rename, so a crash can't tear the live store (and the previous index survives a
        // failed re-index). Fast/durability-off pragmas are the DEFAULT here — a corrupt temp is
        // never published, so there's nothing to protect with a journal. The sole exception is the
        // APPEND path (`--merge`, or `mine`'s `--identity`): it writes IN PLACE into the live DB from
        // (potentially parallel) writers, so it MUST keep the journal — no fast pragmas, no atomic swap.
        // --merge accumulates this solution into an existing store (multi-solution unified store): append
        // in place, dedup assemblies by content-hash via the registry, NO atomic-replace. See
        // docs/multi-solution-storage.md.
        var appendMode = identity is not null || merge; // mine, or --merge into an existing store
        var fastBulkWrite = !appendMode; // fast pragmas on the standalone atomic-publish path
        var atomicPublish = !appendMode; // replace-via-rename for a standalone index

        var storeDirectory = Path.Combine(workingDirectory, ".rig");
        Directory.CreateDirectory(storeDirectory);
        var finalDbPath = Path.Combine(storeDirectory, "rig.db");
        var dbPath = atomicPublish ? finalDbPath + ".tmp" : finalDbPath;
        if (atomicPublish)
            DeleteDbFiles(dbPath); // clear any leftover temp from a previous aborted run

        if (merge)
        {
            // Required DB state for a merge (declare + require, never migrate): an existing store WITH
            // the assembly registry. A pre-multi-solution store is told to re-mine, not silently altered.
            if (!File.Exists(finalDbPath))
            {
                error.WriteLine("--merge requires an existing store. Run `rig index <base-solution>` first, then merge others.");
                return 2;
            }
            await using var probe = new RigDbContext(finalDbPath, pooling: false, readOnly: true);
            if (!await Writes.HasAssemblyRegistryAsync(probe))
            {
                error.WriteLine(
                    "Store predates multi-solution support (no assembly registry). Re-mine the base solution: rig index <base-solution>"
                );
                return 2;
            }
        }

        output.WriteLine(
            $"Progress: Saving run ({(fastBulkWrite ? "fast" : "durable")}{(atomicPublish ? ", atomic-publish" : ", in-place")})"
        );
        var saveWatch = System.Diagnostics.Stopwatch.StartNew();
        string runId;
        await using (var context = new RigDbContext(dbPath, pooling: !atomicPublish))
        {
            await context.Database.EnsureCreatedAsync();
            runId = await Writes.SaveAsync(
                context,
                result,
                fastBulkWrite: fastBulkWrite,
                progress: message => output.WriteLine($"Progress: {message}")
            );
        }

        if (atomicPublish)
        {
            DeleteDbFiles(finalDbPath); // drop the old published store + any sidecars
            File.Move(dbPath, finalDbPath, overwrite: true);
        }
        saveWatch.Stop();
        totalWatch.Stop();
        output.WriteLine(
            $"Progress: Save phase done in {FormatElapsed(saveWatch.Elapsed)}  (analysis {FormatElapsed(analyzeWatch.Elapsed)}, total {FormatElapsed(totalWatch.Elapsed)})"
        );

        output.WriteLine($"Indexed: {Path.GetFullPath(result.SolutionPath)}");
        output.WriteLine($"Run: {runId}");
        output.WriteLine($"Symbols: {result.Symbols?.Count ?? 0}");
        output.WriteLine($"References: {result.References?.Count ?? 0}");
        output.WriteLine($"DiRegistrations: {result.DiRegistrations.Count}");

        return 0;
    }

    internal static Command BuildMine(TextWriter output, TextWriter error, string workingDirectory)
    {
        var solution = CommonOptions.Pattern("solution", "Solution to mine.");
        var from = new Option<string?>("--from") { Description = "Entry project (.csproj) to BFS from.", Required = true };
        var rules = CommonOptions.Rules();
        var identity = new Option<string?>("--identity") { Description = "Store identity (defaults to a hash of the solution path)." };
        var parallelism = new Option<int?>("--parallelism") { Description = "Max concurrent project analyses." };

        var cmd = new Command("mine", "BFS-index a project dependency closure, level by level, in parallel.")
        {
            solution,
            from,
            rules,
            identity,
            parallelism,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunMineAsync(
                        Path.GetFullPath(pr.GetValue(solution)!),
                        Path.GetFullPath(pr.GetValue(from)!),
                        CommonOptions.RulesOf(pr.GetValue(rules)),
                        pr.GetValue(identity),
                        pr.GetValue(parallelism),
                        output,
                        error,
                        workingDirectory
                    )
            )
        );
        return cmd;
    }

    private static async Task<int> RunMineAsync(
        string solutionPath,
        string fromProject,
        IReadOnlyList<string> extraRules,
        string? identity,
        int? parallelismOverride,
        TextWriter output,
        TextWriter error,
        string workingDirectory
    )
    {
        var parallelism = parallelismOverride ?? Math.Max(1, Environment.ProcessorCount / 2);
        identity ??= ComputeIdentity(solutionPath);
        output.WriteLine($"Mine: {solutionPath}");
        output.WriteLine($"From: {fromProject}");
        output.WriteLine($"Identity: {identity}");
        output.WriteLine($"Parallelism: {parallelism}");

        // Build the dependency graph from the solution (parse ProjectReference elements only — no MSBuild needed)
        var depGraph = await DependencyGraph.BuildAsync(solutionPath, output);

        // BFS from the starting project, indexing each level in parallel.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(fromProject);

        var totalIndexed = 0;
        var totalFailed = 0;

        while (queue.Count > 0)
        {
            // Collect the current BFS level — all projects with no unvisited dependencies
            var batch = new List<string>();
            while (queue.Count > 0)
            {
                var proj = queue.Dequeue();
                if (visited.Add(proj))
                    batch.Add(proj);
            }

            if (batch.Count == 0)
                break;

            output.WriteLine($"\n[mine] Batch: {batch.Count} project(s)");

            // Index this batch in parallel
            var semaphore = new SemaphoreSlim(parallelism);
            var batchTasks = batch.Select(async proj =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var projName = Path.GetFileNameWithoutExtension(proj);
                    output.WriteLine($"  [mine] Indexing: {projName}");

                    var exitCode = await RunIndexAsync(
                        proj,
                        extraRules,
                        identity,
                        fromProject: null,
                        parallelism: null,
                        merge: false,
                        includeTests: false,
                        output,
                        error,
                        workingDirectory
                    );
                    if (exitCode == 0)
                    {
                        Interlocked.Increment(ref totalIndexed);
                        output.WriteLine($"  [mine] Done: {projName}");
                    }
                    else
                    {
                        Interlocked.Increment(ref totalFailed);
                        output.WriteLine($"  [mine] Failed: {projName} (exit {exitCode})");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(batchTasks);

            // Enqueue the next level: direct dependencies of this batch that haven't been visited
            foreach (var proj in batch)
            {
                if (depGraph.TryGetValue(proj, out var deps))
                {
                    foreach (var dep in deps.Where(d => !visited.Contains(d)))
                        queue.Enqueue(dep);
                }
            }
        }

        output.WriteLine($"\n[mine] Complete: {totalIndexed} indexed, {totalFailed} failed, {visited.Count} total projects reached.");

        // Write a reachable-projects index alongside the .rig database so subsequent
        // queries can filter to only runs that belong to this dependency closure.
        var indexPath = Path.Combine(workingDirectory, "reachable-projects.json");
        WriteJsonSidecar(
            indexPath,
            new
            {
                identity,
                solutionPath = Path.GetFullPath(solutionPath),
                entryProject = Path.GetFullPath(fromProject),
                indexedAt = DateTimeOffset.UtcNow.ToString("O"),
                projects = visited.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            }
        );
        output.WriteLine($"[mine] Reachable projects index written: {indexPath}");

        return totalFailed > 0 ? 1 : 0;
    }

    internal static Command BuildGraph(TextWriter output, TextWriter error, string workingDirectory)
    {
        var cmd = new Command(
            "graph",
            "Rebuild the derived call-graph views (call_edges + dispatch_edges) from facts; idempotent, no rescan."
        );
        cmd.SetAction(_ => CommandGuard.RunGuardedAsync(workingDirectory, error, () => RunGraphAsync(output, error, workingDirectory)));
        return cmd;
    }

    // Rebuilds the derived call-graph views (call_edges + dispatch_edges) from facts already in the
    // store. Decoupled from index/mine and idempotent — no Roslyn, no rescan; rerun any time. These
    // views back the SQL recursive-CTE reachability path (reaches/callers/tree/dead).
    private static async Task<int> RunGraphAsync(TextWriter output, TextWriter error, string workingDirectory)
    {
        var dbPath = Path.Combine(workingDirectory, ".rig", "rig.db");
        if (!File.Exists(dbPath))
            return CommandGuard.NoRunError(error);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using var context = new RigDbContext(dbPath);
        // Classification rules flow in here so call_edges is written with Kind="handoff" baked in — the
        // single place classification persists, read back by every SQL query path. Generic-factory rules
        // flow in too so the factory monomorphization is baked into call_edges (so the SQL bounding walk
        // sees the rewritten edges the in-memory traversal does — no effect-path divergence).
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory).ToArray();
        var factoryRules = FactGenericFactoryRuleProvider.LoadForWorkingDirectory(workingDirectory);
        var stats = await GraphMaterializer.BuildAsync(
            context,
            handoffRules,
            message => output.WriteLine($"Progress: {message}"),
            factoryRules: factoryRules
        );
        output.WriteLine(
            $"Graph: {stats.CallEdges} call edge(s), {stats.DispatchEdges} dispatch edge(s) "
                + $"({stats.DispatchEdges - stats.HeuristicDispatchEdges} roslyn-mined, {stats.HeuristicDispatchEdges} heuristic), "
                + $"{stats.Nodes} node(s) in {FormatElapsed(stopwatch.Elapsed)}"
        );
        // Materialize the pattern-independent EP-site set into a table now, so every later query reads it
        // directly instead of re-deriving from the whole-store fact tables. No-op without deployments.json.
        await MaterializeEntryPointSitesAsync(context, workingDirectory);
        return 0;
    }

    // Transitive ProjectReference closure of an entry project, minus test projects — the build scope
    // for `rig index --from`. Parses the dependency graph (XML only, no MSBuild), BFS from the entry,
    // drops test projects by name, and writes the closure to relevant-projects.json next to the .rig
    // store. Returns the normalised full project paths to build, or null on a usage error.
    private static async Task<IReadOnlySet<string>?> BuildEntryClosureAsync(
        string solutionPath,
        string fromProject,
        string workingDirectory,
        TextWriter output,
        TextWriter error
    )
    {
        var solutionFull = Path.GetFullPath(solutionPath);
        if (solutionFull.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("--from is only valid when indexing a solution (.slnx/.sln), not a single project.");
            return null;
        }

        var depGraph = await DependencyGraph.BuildAsync(solutionFull, output);
        var entry = Path.GetFullPath(fromProject);
        if (!depGraph.ContainsKey(entry))
        {
            error.WriteLine($"--from project not found in solution: {entry}");
            return null;
        }

        var visited = DependencyGraph.TransitiveClosure(entry, depGraph);

        // Drop test projects. Production projects don't reference them, so the closure is normally
        // test-free already; this honours --from's "without tests" contract defensively.
        var excludedTests = visited.Where(IsTestProjectPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var t in excludedTests)
            visited.Remove(t);

        var listPath = Path.Combine(workingDirectory, "relevant-projects.json");
        WriteJsonSidecar(
            listPath,
            new
            {
                solutionPath = solutionFull,
                entryProject = entry,
                projectCount = visited.Count,
                excludedTestProjects = excludedTests,
                projects = visited.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            }
        );
        output.WriteLine($"Entry closure: {visited.Count} project(s), {excludedTests.Length} test project(s) excluded -> {listPath}");

        return visited;
    }

    private static bool IsTestProjectPath(string projectPath)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        return name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("UnitTests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("IntegrationTests", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".Tests.", StringComparison.OrdinalIgnoreCase);
    }

    // Delete a SQLite DB file and its WAL/SHM/rollback-journal sidecars, ignoring missing files.
    private static void DeleteDbFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var p = dbPath + suffix;
            if (File.Exists(p))
                File.Delete(p);
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s" : $"{elapsed.TotalSeconds:0.0}s";

    private static string ComputeIdentity(string solutionPath) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(solutionPath)))
        )[..16];

    // The indented-JSON sidecar write shared by `index --from` (relevant-projects.json) and `mine`
    // (reachable-projects.json).
    private static void WriteJsonSidecar(string path, object data) =>
        File.WriteAllText(
            path,
            System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
        );
}

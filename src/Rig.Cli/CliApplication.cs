using System.Reflection;
using System.Text;
using Rig.Analysis;
using Rig.Analysis.Rules;
using Rig.Cli.Deployments;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;

namespace Rig.Cli;

public static class CliApplication
{
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        return RunAsync(args, output, error, Directory.GetCurrentDirectory());
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteCommandSummary(output);
            return 0;
        }

        if (IsVersion(args[0]))
        {
            output.WriteLine($"rig {GetVersion()}");
            return 0;
        }

        return await DispatchAsync(args, output, error, workingDirectory);
    }

    // The command switch.
    private static async Task<int> DispatchAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (HasUnknownFlag(args[0], args, error))
            return 2;
        return args[0] switch
        {
            "index" => await RunIndexAsync(args, output, error, workingDirectory),
            "mine" => await RunMineAsync(args, output, error, workingDirectory),
            "runs" => await RunRunsAsync(output, workingDirectory),
            "di" => await RunDiAsync(output, error, workingDirectory),
            "symbols" => await RunSymbolsAsync(args, output, error, workingDirectory),
            "refs" => await RunRefsAsync(args, output, error, workingDirectory),
            "path" => await RunPathAsync(args, output, error, workingDirectory),
            "tree" => await RunTreeAsync(args, output, error, workingDirectory),
            "callers" => await RunCallersAsync(args, output, error, workingDirectory),
            "reaches" => await RunReachesAsync(args, output, error, workingDirectory),
            "derive" => await RunDeriveAsync(args, output, error, workingDirectory),
            "graph" => await RunGraphAsync(output, error, workingDirectory),
            "dead" => await RunDeadAsync(args, output, error, workingDirectory),
            "files" => await RunFilesAsync(args, output, error, workingDirectory),
            "profile" => await RunProfileAsync(args, output, error, workingDirectory),
            _ => UnknownCommand(args[0], error),
        };
    }

    private static bool IsHelp(string arg)
    {
        return arg is "--help" or "-h" or "help";
    }

    private static bool IsVersion(string arg)
    {
        return arg is "--version" or "-v" or "version";
    }

    private static string GetVersion()
    {
        return typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CliApplication).Assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static void WriteCommandSummary(TextWriter output)
    {
        output.WriteLine("Runtime Intelligence Graph");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine(
            "  rig index <solution|project> [--rules <path>...] [--identity <id>] [--from <entry.csproj>] [--parallelism <n>] [--durable] [--merge] [--include-tests]   (--from = index only the entry project's closure, one workspace; --durable = journaled write, default is fast atomic-publish; --merge = accumulate into an existing store for a multi-solution unified store; test projects are EXCLUDED by default — --include-tests keeps them)"
        );
        output.WriteLine("  rig mine <solution> --from <project.csproj> [--rules <path>...] [--identity <id>] [--parallelism <n>]");
        output.WriteLine("  rig runs");
        output.WriteLine("  rig di   (DI registrations: service -> implementation, lifetime, source)");
        output.WriteLine("  rig symbols <pattern> [--kind <k>] [--limit <n>]");
        output.WriteLine("  rig refs <pattern> [--first-party] [--kind <refkind>] [--limit <n>]");
        output.WriteLine(
            "  rig path <fromPattern> <toPattern> [--async] [--raw] [--rules <path>...] [--depth <n>]   (synchronous by default; --async also walks async handoff edges, tagged; --raw bypasses graph shaping (factory/cut/context rules); --depth defaults unbounded)"
        );
        output.WriteLine(
            "  rig reaches <fromPattern> [--async] [--rules <path>...] [--depth <n>] [--only <p,..>] [--exclude <p,..>] [--format tsv]   (effects reachable from an entry point; synchronous by default (handoffs cut); --async adds scheduled/cross-thread reach via handoffs in a separate ⚡bucket; --exclude throw to drop exceptions)"
        );
        output.WriteLine(
            "  rig tree <fromPattern> [--full|--summary|--effects] [--async] [--only <p,..>] [--exclude <p,..>] [--rules <path>...] [--depth <n>]   (call tree from an entry point; default = synchronous paths that reach an effect, effects inline as {tags}; --full = every reachable method with effects (⚡) AND unresolved library calls (·) as call-site leaf nodes; --async also crosses handoffs (marked ⤳); --effects = only effectful methods, no skeleton; --exclude throw to drop exceptions)"
        );
        output.WriteLine(
            "  rig callers <toPattern> [--orphans|--entrypoints] [--async] [--raw] [--rules <path>...] [--depth <n>]   (reverse reachability: who reaches this method; defaults to SYNCHRONOUS only (handoffs cut), so background callbacks show as their own origins; --async also counts scheduled paths; --orphans = no-predecessor candidates (heuristic); --entrypoints = RULE-DETECTED entry points that reach it (precise); shaped by the same factory/cut/context rules as path/reaches/tree, --raw bypasses)"
        );
        output.WriteLine(
            "  rig derive [--rules <path>...] [--limit <n>] [--only <p,..>] [--exclude <p,..>]   (stage-2 pass over facts: effects + handoffs; --exclude throw to drop exceptions)"
        );
        output.WriteLine(
            "  rig graph   (rebuild the derived call-graph views (call_edges + dispatch_edges) from facts; idempotent, no rescan — speeds up reaches/callers/tree/dead)"
        );
        output.WriteLine(
            "  rig dead [--rules <path>...] [--include-lib] [--include-dispatch] [--all] [--root <pattern>...] [--format tsv]   (unreachable first-party methods; report-only, compiler-confirm before removing)"
        );
        output.WriteLine("  rig files --skipped");
        output.WriteLine("  rig profile validate");
    }

    private static async Task<int> RunIndexAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Missing solution or project path.");
            error.WriteLine(
                "Usage: rig index <solution|project> [--rules <path>...] [--identity <id>] [--from <entry.csproj>] [--parallelism <n>] [--durable] [--merge] [--include-tests]"
            );
            return 2;
        }

        var extraRules = new List<string>();
        string? identity = null;
        string? fromProject = null;
        int? parallelism = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--rules" && i + 1 < args.Length)
            {
                extraRules.Add(Path.GetFullPath(args[i + 1]));
                i++;
            }
            else if (args[i] == "--identity" && i + 1 < args.Length)
            {
                identity = args[i + 1];
                i++;
            }
            else if (args[i] == "--from" && i + 1 < args.Length)
            {
                fromProject = Path.GetFullPath(args[i + 1]);
                i++;
            }
            else if (args[i] == "--parallelism" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                parallelism = p;
                i++;
            }
        }

        // --from <csproj>: index only the transitive ProjectReference closure of the entry project
        // (minus test projects) in ONE cross-project Roslyn workspace — skips every out-of-closure
        // test/tool project before its design-time build runs. The closure is written to
        // relevant-projects.json next to the .rig store.
        IReadOnlySet<string>? scopeProjectPaths = null;
        if (fromProject is not null)
        {
            scopeProjectPaths = await BuildEntryClosureAsync(args[1], fromProject, workingDirectory, output, error);
            if (scopeProjectPaths is null)
                return 2;
        }

        var totalWatch = System.Diagnostics.Stopwatch.StartNew();
        AnalysisResult result;
        var analyzeWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            output.WriteLine($"Indexing: {Path.GetFullPath(args[1])}");
            if (extraRules.Count > 0)
                output.WriteLine($"Rules: {string.Join(", ", extraRules)}");
            if (identity is not null)
                output.WriteLine($"Identity: {identity}");
            if (fromProject is not null)
                output.WriteLine($"From (closure): {fromProject}  ->  {scopeProjectPaths!.Count} project(s)");
            if (parallelism is not null)
                output.WriteLine($"Parallelism: {parallelism}");
            result = await SolutionAnalyzer.AnalyzeAsync(
                args[1],
                progress: message => output.WriteLine($"Progress: {message}"),
                extraRulesPaths: extraRules.Count > 0 ? extraRules : null,
                projectIdentity: identity,
                scopeProjectPaths: scopeProjectPaths,
                parallelism: parallelism,
                // Tests are EXCLUDED by default (they add graph width, not reach). `--include-tests` opts
                // them back in; `--no-tests` is the now-redundant former opt-out, accepted as a no-op alias.
                excludeTests: !args.Contains("--include-tests")
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
            error.WriteLine($"  dotnet restore {args[1]}");
            error.WriteLine($"  dotnet build {args[1]}");
            return 2;
        }
        analyzeWatch.Stop();
        output.WriteLine($"Progress: Analysis phase done in {FormatElapsed(analyzeWatch.Elapsed)}");

        // Publish model. A standalone `index` is a full REPLACE published via write-to-temp +
        // atomic rename, so a crash can't tear the live store (and the previous index survives a
        // failed re-index). Fast/durability-off pragmas are the DEFAULT here — a corrupt temp is
        // never published, so there's nothing to protect with a journal. Two opt-outs to the safe,
        // durable, journaled path:
        //   --durable      — user asks for a consistent in-the-clear write (still atomic-published).
        //   --identity set — `mine` APPENDS many per-project runs into the live DB from PARALLEL
        //                    writers, so it writes in place and MUST keep the journal (no fast pragmas).
        var durable = args.Contains("--durable");
        // --merge accumulates this solution into an existing store (multi-solution unified store): append
        // in place, dedup assemblies by content-hash via the registry, NO atomic-replace. See
        // docs/multi-solution-storage.md.
        var merge = args.Contains("--merge");
        var appendMode = identity is not null || merge; // mine, or --merge into an existing store
        var fastBulkWrite = !durable && !appendMode; // optimisations on by default; opt out above
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
                error.WriteLine("Store predates multi-solution support (no assembly registry). Re-mine the base solution: rig index <base-solution>");
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

        // BFS the dependency graph from the entry project (paths are already normalised full paths).
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(entry);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (!visited.Add(p))
                continue;
            if (depGraph.TryGetValue(p, out var deps))
                foreach (var d in deps)
                    if (!visited.Contains(d))
                        queue.Enqueue(d);
        }

        // Drop test projects. Production projects don't reference them, so the closure is normally
        // test-free already; this honours --from's "without tests" contract defensively.
        var excludedTests = visited.Where(IsTestProjectPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var t in excludedTests)
            visited.Remove(t);

        var listPath = Path.Combine(workingDirectory, "relevant-projects.json");
        var listData = new
        {
            solutionPath = solutionFull,
            entryProject = entry,
            projectCount = visited.Count,
            excludedTestProjects = excludedTests,
            projects = visited.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        File.WriteAllText(
            listPath,
            System.Text.Json.JsonSerializer.Serialize(listData, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
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

    private static async Task<int> RunRunsAsync(TextWriter output, string workingDirectory)
    {
        await using var context = OpenReadContext(workingDirectory);
        var runs = await Reads.ListRunsAsync(context);

        output.WriteLine("Runs");
        foreach (var run in runs)
        {
            output.WriteLine($"  {run.Id}");
            output.WriteLine($"    indexed={run.CreatedAtUtc:u}");
            output.WriteLine($"    solution={run.SolutionPath}");
            output.WriteLine($"    symbols={run.SymbolCount} references={run.ReferenceCount} di={run.DiRegistrationCount}");
        }

        return 0;
    }

    // Rebuilds the derived call-graph views (call_edges + dispatch_edges) from facts already in the
    // store. Decoupled from index/mine and idempotent — no Roslyn, no rescan; rerun any time. These
    // views back the SQL recursive-CTE reachability path (reaches/callers/tree/dead).
    private static async Task<int> RunGraphAsync(TextWriter output, TextWriter error, string workingDirectory)
    {
        var dbPath = Path.Combine(workingDirectory, ".rig", "rig.db");
        if (!File.Exists(dbPath))
            return NoRunError(error);

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

    private static async Task<int> RunDiAsync(TextWriter output, TextWriter error, string workingDirectory)
    {
        await using var context = OpenReadContext(workingDirectory);
        var registrations = await Reads.LoadDiRegistrationsAsync(context);
        if (registrations is null)
        {
            return NoRunError(error);
        }

        DiRenderer.Render(registrations, output);

        return 0;
    }

    private static Task<int> RunProfileAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2 || args[1] != "validate")
        {
            error.WriteLine("Usage: rig profile validate");
            return Task.FromResult(2);
        }

        try
        {
            AnalysisProfileValidator.ValidateForSolution(workingDirectory);
            output.WriteLine("Profile: valid");
            return Task.FromResult(0);
        }
        catch (Exception exception)
        {
            error.WriteLine($"Profile: invalid — {exception.Message}");
            return Task.FromResult(2);
        }
    }

    private static async Task<int> RunFilesAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length != 2 || args[1] != "--skipped")
        {
            error.WriteLine("Usage: rig files --skipped");
            return 2;
        }

        await using var context = OpenReadContext(workingDirectory);
        var sourceFiles = await Reads.LoadSkippedSourceFilesAsync(context);
        if (sourceFiles is null)
        {
            return NoRunError(error);
        }

        SourceFileRenderer.RenderSkipped(sourceFiles, output);

        return 0;
    }

    // Every query command opens the store READ-ONLY (see RigDbContext.readOnly): the engine rejects any
    // write to the main DB, so a read command can never mutate the index. Writers (index/mine/graph) use
    // the default read-write constructor.
    private static RigDbContext OpenReadContext(string workingDirectory) =>
        new(Path.Combine(workingDirectory, ".rig", "rig.db"), readOnly: true);

    // The call graph for a traversal command (reaches/tree/path/callers). When the derived edge views
    // exist (`rig graph` has been run) it returns the BOUNDED subgraph for `pattern` in the given
    // direction — loaded on disk via recursive CTE, sized to the result, not the 1.6GB store. Otherwise
    // it falls back to the full in-memory EF graph (the reference path). The SAME FactPathFinder then
    // runs over whichever graph, so the output is identical — only the load cost differs.
    private static async Task<FactGraphData> LoadTraversalGraphAsync(
        RigDbContext context,
        string pattern,
        SqlReachability.Direction direction,
        IReadOnlyList<FactHandoffRule> handoffRules
    )
    {
        // SQL path: call_edges already carry the persisted handoff classification (from `rig graph`),
        // so the bounded graph is classified by construction. EF fallback: classify the loaded graph
        // with the rules so the in-memory traversal sees the same handoff edges.
        if (await SqlReachability.HasGraphAsync(context))
            return await SqlReachability.LoadBoundedGraphAsync(context, pattern, direction);
        return await Reads.LoadFactGraphAsync(context, handoffRules);
    }

    // The SHAPED traversal graph: LoadTraversalGraphAsync + the single FactPathFinder.ShapeGraph pass
    // (monomorphize generic factories + carry cut/context rules on the graph). EVERY attribution command
    // — forward (path) or reverse (callers) — loads through here so they all walk the identical shaped
    // graph; this is what keeps `callers` consistent with `path`/`reaches`. `dead` deliberately does NOT
    // use this (it needs the sound CHA superset). Pass empty rule sets (the `--raw` path) for no shaping.
    private static async Task<FactGraphData> LoadShapedTraversalGraphAsync(
        RigDbContext context,
        string pattern,
        SqlReachability.Direction direction,
        IReadOnlyList<FactHandoffRule> handoffRules,
        IReadOnlyList<FactGenericFactoryRule> factoryRules,
        IReadOnlyList<FactTraversalCutRule> cutRules,
        IReadOnlyList<FactContextDispatchRule> contextRules
    )
    {
        var graph = await LoadTraversalGraphAsync(context, pattern, direction, handoffRules);
        return FactPathFinder.ShapeGraph(graph, factoryRules, cutRules, contextRules);
    }

    // Like LoadTraversalGraphAsync, but also returns the effect-derivation inputs (invocations / ctor
    // refs / throw refs) bounded to the SAME closure — so reaches/tree don't scan every invocation in
    // the codebase. SQL path: one reach_set drives the graph + bounded inputs. EF fallback: the full
    // reference loads (the original path), so output is identical when no derived views exist.
    private static async Task<SqlReachability.ReachInputs> LoadEffectReachInputsAsync(
        RigDbContext context,
        string pattern,
        SqlReachability.Direction direction,
        IReadOnlyList<FactHandoffRule> handoffRules,
        IReadOnlyList<FactGenericFactoryRule>? factoryRules = null,
        IReadOnlyList<FactTraversalCutRule>? cutRules = null,
        IReadOnlyList<FactContextDispatchRule>? contextRules = null
    )
    {
        var inputs = await SqlReachability.HasGraphAsync(context)
            ? await SqlReachability.LoadReachInputsAsync(context, pattern, direction)
            : await LoadReachInputsFromRowsAsync(context, handoffRules);

        // The single shaping pass (monomorphize generic factories + carry cut/context rules on the graph)
        // so reaches/tree walk the same shaped graph as path/callers. Edges with no concrete construct
        // keep their plumbing (the in-memory generic-dispatch narrowing covers those).
        inputs = inputs with
        {
            Graph = FactPathFinder.ShapeGraph(inputs.Graph, factoryRules ?? [], cutRules ?? [], contextRules ?? []),
        };
        return inputs;
    }

    private static async Task<SqlReachability.ReachInputs> LoadReachInputsFromRowsAsync(
        RigDbContext context,
        IReadOnlyList<FactHandoffRule> handoffRules
    )
    {
        var graph = await Reads.LoadFactGraphAsync(context, handoffRules);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        return new SqlReachability.ReachInputs(graph, invocations, epData.CtorRefs, throwRefs);
    }

    // Base edges in the (TypeId, BaseId) shape FactEffectDeriver.Derive expects, from a graph's edges.
    private static (string, string)[] BaseEdgeTuples(FactGraphData graph) =>
        (graph.BaseEdges ?? []).Select(e => (e.SubType, e.BaseType)).ToArray();

    private static int NoRunError(TextWriter error)
    {
        error.WriteLine("No indexed run found. Run `rig index <solution>` first.");
        return 2;
    }

    private static int UnknownCommand(string command, TextWriter error)
    {
        error.WriteLine($"Unknown command: {command}");
        error.WriteLine("Run `rig --help` to see available commands.");
        return 2;
    }

    // rig mine <solution> --from <project.csproj> [--rules <path>...] [--identity <id>] [--parallelism <n>]
    // Traverses the project dependency graph starting from <project.csproj> in BFS order,
    // indexing each reachable project.  Projects at the same BFS level are indexed in parallel.
    private static async Task<int> RunMineAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Usage: rig mine <solution> --from <project.csproj> [--rules <path>...] [--identity <id>] [--parallelism <n>]");
            return 2;
        }

        var solutionPath = Path.GetFullPath(args[1]);
        string? fromProject = null;
        var extraRules = new List<string>();
        string? identity = null;
        var parallelism = Math.Max(1, Environment.ProcessorCount / 2);

        for (var i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--from")
            {
                fromProject = Path.GetFullPath(args[i + 1]);
                i++;
            }
            else if (args[i] == "--rules")
            {
                extraRules.Add(Path.GetFullPath(args[i + 1]));
                i++;
            }
            else if (args[i] == "--identity")
            {
                identity = args[i + 1];
                i++;
            }
            else if (args[i] == "--parallelism" && int.TryParse(args[i + 1], out var p))
            {
                parallelism = p;
                i++;
            }
        }

        if (fromProject is null)
        {
            error.WriteLine("--from <project.csproj> is required.");
            return 2;
        }

        identity ??= ComputeIdentity(solutionPath);
        output.WriteLine($"Mine: {solutionPath}");
        output.WriteLine($"From: {fromProject}");
        output.WriteLine($"Identity: {identity}");
        output.WriteLine($"Parallelism: {parallelism}");

        // Build the dependency graph from the solution (parse ProjectReference elements only — no MSBuild needed)
        var depGraph = await DependencyGraph.BuildAsync(solutionPath, output);

        // BFS from the starting project
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

                    var rulesArgs = extraRules.SelectMany(r => new[] { "--rules", r }).ToArray();
                    var indexArgs = new[] { "index", proj }.Concat(rulesArgs).Concat(new[] { "--identity", identity! }).ToArray();

                    var exitCode = await RunIndexAsync(indexArgs, output, error, workingDirectory);
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
        var indexData = new
        {
            identity = identity,
            solutionPath = Path.GetFullPath(solutionPath),
            entryProject = Path.GetFullPath(fromProject),
            indexedAt = DateTimeOffset.UtcNow.ToString("O"),
            projects = visited.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        File.WriteAllText(
            indexPath,
            System.Text.Json.JsonSerializer.Serialize(indexData, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
        );
        output.WriteLine($"[mine] Reachable projects index written: {indexPath}");

        return totalFailed > 0 ? 1 : 0;
    }

    private static string ComputeIdentity(string solutionPath) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(solutionPath)))
        )[..16];

    // rig symbols <pattern> [--kind <k>] [--limit <n>]
    private static async Task<int> RunSymbolsAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Usage: rig symbols <pattern> [--kind <k>] [--limit <n>]");
            return 2;
        }

        var pattern = args[1];
        var kind = GetOption(args, "--kind");
        var limit = int.TryParse(GetOption(args, "--limit"), out var l) ? l : 50;

        await using var context = OpenReadContext(workingDirectory);
        var hits = await Reads.SearchSymbolsAsync(context, pattern, kind, limit);

        output.WriteLine($"Symbols matching '{pattern}'{(kind is null ? "" : $" kind={kind}")}");
        foreach (var hit in hits)
            output.WriteLine($"  {hit.Kind, -8} {hit.SymbolId}  {ShortenPath(hit.FilePath)}:{hit.Line}");
        output.WriteLine($"  ({hits.Count} shown)");
        return 0;
    }

    // rig refs <pattern> [--first-party] [--kind <refkind>] [--limit <n>]
    private static async Task<int> RunRefsAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Usage: rig refs <pattern> [--first-party] [--kind <refkind>] [--limit <n>]");
            return 2;
        }

        var pattern = args[1];
        var firstParty = args.Contains("--first-party");
        var refKind = GetOption(args, "--kind");
        var limit = int.TryParse(GetOption(args, "--limit"), out var l) ? l : 200;

        await using var context = OpenReadContext(workingDirectory);
        var hits = await Reads.FindReferencesAsync(context, pattern, firstParty, refKind, limit);

        output.WriteLine($"References to '{pattern}'{(firstParty ? " (first-party)" : "")}{(refKind is null ? "" : $" kind={refKind}")}");
        foreach (var group in hits.GroupBy(h => h.TargetSymbolId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"  {group.Key}");
            foreach (var hit in group)
                output.WriteLine(
                    $"    {hit.RefKind, -11} {hit.EnclosingSymbolId ?? "(top-level)"}  {ShortenPath(hit.FilePath)}:{hit.Line}"
                );
        }
        output.WriteLine($"  ({hits.Count} reference(s) shown)");
        return 0;
    }

    // rig path <fromPattern> <toPattern>  — BFS the fact-derived call graph (cross-project,
    // entry-point-independent, with interface->concrete dispatch) and print the first path found.
    private static async Task<int> RunPathAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 3)
        {
            error.WriteLine("Usage: rig path <fromPattern> <toPattern> [--async] [--raw] [--rules <path>...] [--depth <n>]");
            return 2;
        }

        var fromPattern = args[1];
        var toPattern = args[2];
        var mode = TraversalModeOf(args);
        var extraRules = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--rules" && i + 1 < args.Length)
            {
                extraRules.Add(Path.GetFullPath(args[i + 1]));
                i++;
            }
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        // `path` walks the SAME shaped graph as reaches/tree (monomorphized factories + cut/context
        // rules) so the path it reports is consistent with what those report — a reflection seam that
        // `tree` cuts is not silently traversed here either. `--raw` bypasses all shaping.
        var raw = args.Contains("--raw");
        var factoryRules = raw
            ? Array.Empty<FactGenericFactoryRule>()
            : FactGenericFactoryRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var cutRules = raw
            ? Array.Empty<FactTraversalCutRule>()
            : FactTraversalCutRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var contextRules = raw
            ? Array.Empty<FactContextDispatchRule>()
            : FactContextDispatchRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);

        await using var context = OpenReadContext(workingDirectory);
        // Any path from a `from` node lies entirely within that node's forward closure, so the BOUNDED
        // forward subgraph (loaded on disk via the derived edge views, sized to the result) finds the
        // same first path as the full graph — without the 1.4M-row in-memory load. Falls back to the
        // full EF graph when `rig graph` hasn't been run. (Same pattern as reaches/tree/callers.)
        var graph = await LoadShapedTraversalGraphAsync(
            context,
            fromPattern,
            SqlReachability.Direction.Forward,
            handoffRules,
            factoryRules,
            cutRules,
            contextRules
        );
        output.WriteLine(
            $"Fact graph: {graph.CallEdges.Count} call edges, {graph.ImplementsEdges.Count} implements edges, {graph.Methods.Count} methods"
        );

        var path = FactPathFinder.Find(graph, fromPattern, toPattern, maxDepth: MaxDepthOf(args), mode: mode);
        if (path is null)
        {
            output.WriteLine($"No path from '{fromPattern}' to '{toPattern}'.");
            return 1;
        }

        // Deployment/EP chip on the from-node (path[0]): which service(s) host this entry point.
        // Opt-in via deployments.json; no-op otherwise.
        var pathDeployments = await DeploymentMap.LoadAsync(
            workingDirectory,
            (await Reads.ListRunsAsync(context)).FirstOrDefault()?.SolutionPath
        );
        var pathEpContext = await BuildEpContextAsync(
            context,
            graph,
            workingDirectory,
            extraRules,
            handoffRules,
            pathDeployments,
            !args.Contains("--no-cache")
        );

        output.WriteLine($"Path '{fromPattern}' -> '{toPattern}' ({path.Count} nodes):");
        for (var i = 0; i < path.Count; i++)
        {
            var step = path[i];
            var loop = step.LoopKind is null ? "" : $" | loop {step.LoopKind}: {ShortLoop(step.LoopDetail)}";
            var kindBase = step.HandoffVia is not null ? $"⤳ handoff via {ShortName(step.HandoffVia)}" : step.Kind;
            if (step.DispatchBasis == "heuristic")
                kindBase += " (heuristic)";
            var kind = step.Fanout > 1 ? $"{kindBase} ×{step.Fanout} fan-out" : kindBase;
            var via =
                i == 0
                    ? HeaderSuffix(pathEpContext, step.SymbolId)
                    : $"  [{kind}{loop}{(step.FilePath is null ? "" : $" @ {ShortenPath(step.FilePath)}:{step.Line}")}]";
            output.WriteLine($"  {new string(' ', i * 2)}{step.SymbolId}{via}");
        }
        return 0;
    }

    // rig reaches <fromPattern> [--rules <path>...] [--maxdepth|--depth <n>] [--format tsv]
    // Reachability over the SAME fact call graph that powers `rig path` (incl. interface->impl
    // dispatch), intersected with the derived effects: "from this entry point, which captured
    // effects are reachable, and at what depth". Validates effect capture along real call paths.
    private static async Task<int> RunReachesAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Usage: rig reaches <fromPattern> [--async] [--rules <path>...] [--depth <n>] [--format tsv]");
            return 2;
        }
        var fromPattern = args[1];
        var maxDepth = MaxDepthOf(args);
        var tsv = string.Equals(GetOption(args, "--format"), "tsv", StringComparison.OrdinalIgnoreCase);
        var mode = TraversalModeOf(args);
        var extraRules = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--rules" && i + 1 < args.Length)
            {
                extraRules.Add(Path.GetFullPath(args[i + 1]));
                i++;
            }
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var factoryRules = FactGenericFactoryRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var raw = args.Contains("--raw");
        var cutRules = raw
            ? Array.Empty<FactTraversalCutRule>()
            : FactTraversalCutRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var contextRules = raw
            ? Array.Empty<FactContextDispatchRule>()
            : FactContextDispatchRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);

        await using var context = OpenReadContext(workingDirectory);

        var inputs = await LoadEffectReachInputsAsync(
            context,
            fromPattern,
            SqlReachability.Direction.Forward,
            handoffRules,
            factoryRules,
            cutRules,
            contextRules
        );
        var graph = inputs.Graph;
        if (!raw)
            graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
        var reachable = FactPathFinder.ReachesWithFanout(graph, fromPattern, maxDepth, mode: mode);

        var effectRules = FactEffectRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var observationRules = FactObservationRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var effects = FactEffectDeriver.Derive(
            inputs.Invocations,
            effectRules,
            providerFilter: null,
            baseEdges: BaseEdgeTuples(graph),
            ctorRefs: inputs.CtorRefs,
            observationRules: observationRules,
            throwRefs: inputs.ThrowRefs
        );
        effects = ApplyEffectFilters(effects, args); // --only / --exclude (e.g. --exclude throw)

        // Effects whose enclosing method is reachable from the entry point. Fanout = looped call
        // edges on the path to the enclosing method (ReachInfo.LoopNesting) + 1 if the effect's OWN
        // call site is inside a loop (the looped_effect observation). >0 => the effect fires N-deep
        // inside loops along this path; the loop detail shown is the innermost wrapping loop.
        var hits = effects
            .Where(e => e.EnclosingSymbolId is not null && reachable.ContainsKey(e.EnclosingSymbolId))
            .Select(e =>
            {
                var ri = reachable[e.EnclosingSymbolId!];
                var ownLoop = (e.Observations ?? []).Any(o => o.Type == "looped_effect");
                var ownDetail = (e.Observations ?? []).Where(o => o.Type == "looped_effect").Select(o => o.Detail).FirstOrDefault();
                var fanout = ri.LoopNesting + (ownLoop ? 1 : 0);
                var loopDetail = ownLoop ? (string.IsNullOrEmpty(ownDetail) ? ri.NearestLoopDetail : ownDetail) : ri.NearestLoopDetail;
                return (
                    ri.Depth,
                    Fanout: fanout,
                    Loop: loopDetail,
                    Via: ri.DispatchVia,
                    ViaDegree: ri.DispatchDegree,
                    HandoffVia: ri.HandoffVia,
                    Basis: ri.DispatchBasis,
                    Effect: e
                );
            })
            .OrderBy(h => h.Depth)
            .ToList();

        if (tsv)
        {
            // dispatchVia/dispatchDegree flag effects whose ONLY reach is a base-virtual/interface
            // dispatch fan-out (not a real call, D3/D7); handoffVia flags effects reachable ONLY
            // across an async handoff boundary (cross-thread; only under --async); dispatchBasis
            // (last col) = "heuristic" when a name/arity-guessed dispatch hop is on the path
            // ("roslyn" when all dispatch hops are exact mined facts; empty when no dispatch hop).
            foreach (var h in hits)
                output.WriteLine(
                    $"{h.Depth}\t{h.Effect.Provider}\t{h.Effect.Operation}\t{h.Effect.ResourceType}\t{h.Effect.EnclosingSymbolId}\t{ShortenPath(h.Effect.FilePath)}:{h.Effect.Line}\t{h.Fanout}\t{ShortLoop(h.Loop)}\t{h.Via}\t{(h.Via is null ? 0 : h.ViaDegree)}\t{h.HandoffVia}\t{h.Basis}"
                );
            return 0;
        }

        // Three buckets: an effect reached across an async handoff (HandoffVia set) is SCHEDULED
        // (cross-thread), not on a synchronous path — split out first. Of the rest, a DispatchVia tag
        // means the only reach is base-virtual/interface dispatch fan-out (A1), rolled up by source.
        // What remains is genuine direct reach.
        var scheduled = hits.Where(h => h.HandoffVia is not null).ToList();
        var direct = hits.Where(h => h.HandoffVia is null && h.Via is null).ToList();
        var fanned = hits.Where(h => h.HandoffVia is null && h.Via is not null).ToList();

        // Deployment/EP chip on the From line: which service(s) host this entry point (opt-in via
        // deployments.json; no-op otherwise). The from-root is the depth-0 reachable symbol.
        var reachDeployments = await DeploymentMap.LoadAsync(
            workingDirectory,
            (await Reads.ListRunsAsync(context)).FirstOrDefault()?.SolutionPath
        );
        var reachEpContext = await BuildEpContextAsync(
            context,
            graph,
            workingDirectory,
            extraRules,
            handoffRules,
            reachDeployments,
            !args.Contains("--no-cache")
        );
        var reachFromRoot = reachable.Where(kv => kv.Value.Depth == 0).Select(kv => kv.Key).FirstOrDefault();
        output.WriteLine(
            $"From: {fromPattern}{(mode == FactPathFinder.TraversalMode.AsyncInclude ? "  (--async: handoffs included)" : "")}"
                + (reachFromRoot is null ? "" : HeaderSuffix(reachEpContext, reachFromRoot))
        );
        output.WriteLine($"Reachable methods (<= depth {maxDepth}): {reachable.Count}");
        output.WriteLine($"Direct effects (real call paths): {direct.Count}  (fanned out under a loop: {direct.Count(h => h.Fanout > 0)})");
        foreach (var g in direct.GroupBy(h => (h.Effect.Provider, h.Effect.Operation)).OrderByDescending(g => g.Count()))
            output.WriteLine($"  {g.Count(), 4}  {g.Key.Provider} {g.Key.Operation}");
        output.WriteLine("--- nearest direct effects (depth  provider op  resource  <- method  [loop]) ---");
        foreach (var h in direct.Take(40))
        {
            var fan = h.Fanout > 0 ? $"  🔁x{h.Fanout} [loop: {ShortLoop(h.Loop)}]" : "";
            var heuristic = h.Basis == "heuristic" ? "  ~heuristic" : "";
            output.WriteLine(
                $"  d{h.Depth}  {h.Effect.Provider} {h.Effect.Operation}  {ShortName(h.Effect.ResourceType)}  <- {ShortName(h.Effect.EnclosingSymbolId)}{fan}{SpanTag(h.Effect)}{heuristic}"
            );
        }

        if (scheduled.Count > 0)
        {
            output.WriteLine(
                $"--- async (scheduled) effects ({scheduled.Count}; reached across a handoff boundary — ⚡cross_thread, NOT synchronous) ---"
            );
            foreach (
                var g in scheduled.GroupBy(h => (h.HandoffVia!, h.Effect.Provider, h.Effect.Operation)).OrderByDescending(g => g.Count())
            )
                output.WriteLine($"  ⚡x{g.Count(), -4} {g.Key.Provider} {g.Key.Operation}  ⤳ via {ShortName(g.Key.Item1)} [cross_thread]");
        }

        if (fanned.Count > 0)
        {
            output.WriteLine(
                $"--- dispatch fan-out ({fanned.Count} effects; reach is base-virtual/interface dispatch, NOT a real call — see A1) ---"
            );
            foreach (var g in fanned.GroupBy(h => (h.Via!, h.Effect.Provider, h.Effect.Operation)).OrderByDescending(g => g.Count()))
            {
                var degree = g.Max(h => h.ViaDegree);
                var heuristic = g.Any(h => h.Basis == "heuristic") ? "  ~heuristic" : "";
                output.WriteLine(
                    $"  x{g.Count(), -5} {g.Key.Provider} {g.Key.Operation}  via {ShortName(g.Key.Item1)} dispatch [fan-out of {degree}]{heuristic}"
                );
            }
        }
        return 0;
    }

    // rig tree <fromPattern> [--full|--summary] [--rules <path>...] [--maxdepth|--depth <n>]
    // The full first-party call TREE from an entry point over the fact graph (entrypoint-independent,
    // same edges as reaches/path — incl. interface->impl + base->override dispatch + loop context).
    // Modes mirror the legacy `callgraph`: default prunes to call paths that REACH an effect; --full
    // prints every reachable method AND promotes effects to call-site leaf nodes (provider:op + resource +
    // file:line) instead of the inline tag; --summary prints just the effect-count rollup. In the compact
    // modes effects are annotated inline ({provider:op resource}); looped edges get 🔁; cycle/shared-callee re-entry
    // is shown as "↺seen" (that method's subtree is printed under its first occurrence).
    private static async Task<int> RunTreeAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine(
                "Usage: rig tree <fromPattern> [--full|--summary|--effects] [--async] [--raw] [--files] [--signatures] [--rules <path>...] [--depth <n>] [--no-cache] [--time]"
            );
            return 2;
        }
        var fromPattern = args[1];
        var maxDepth = MaxDepthOf(args);
        var full = args.Contains("--full");
        var summary = args.Contains("--summary");
        var effectsOnly = args.Contains("--effects");
        if (HasConflictingModes(args, "tree", error, "--full", "--summary", "--effects"))
            return 2;
        var mode = TraversalModeOf(args);
        var extraRules = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--rules" && i + 1 < args.Length)
            {
                extraRules.Add(Path.GetFullPath(args[i + 1]));
                i++;
            }
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        // Codebase-specific render rules (collapse fan-out seams / opaque infra types). Presentation
        // only — never affects reach. `--raw` bypasses them to print the exact unfiltered tree.
        // `--raw` also bypasses the generic-factory edge rewrite, so the exact plumbing chain
        // (Entity.New``3 -> … -> Construct`2.New -> ×N) is visible for inspection.
        var raw = args.Contains("--raw");
        var renderRules = raw ? FactRenderRules.Empty : FactRenderRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var factoryRules = raw
            ? Array.Empty<FactGenericFactoryRule>()
            : FactGenericFactoryRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        // Traversal-cut rules: stop drilling into infra seams (reflection service-locators etc.)
        // during TRAVERSAL so they can't steal shallow direct-call expansions. `--raw` bypasses.
        var cutRules = raw
            ? Array.Empty<FactTraversalCutRule>()
            : FactTraversalCutRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        // Context-bound dispatch rules: narrow a context-interface's fan-out to the enclosing context's
        // family (e.g. IWorkflowState dispatch -> only the controller's own states). `--raw` bypasses.
        var contextRules = raw
            ? Array.Empty<FactContextDispatchRule>()
            : FactContextDispatchRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);

        await using var context = OpenReadContext(workingDirectory);
        var timer = new PhaseTimer(args.Contains("--time"), error);

        // Query cache (best-effort, opt-out via --no-cache). A `rig tree` query recomputes the call-tree
        // forest (BuildTree) AND its effects (FactEffectDeriver.Derive — the ~3.8s dominant cost); both
        // are a pure function of the store + effective rules + traversal params. Cache the pair in a
        // separate writable `.rig/cache.db` (rig.db itself is opened read-only), so a repeat query skips
        // both and only re-loads the cheaper graph to render. Auto-invalidates on reindex: the key embeds
        // a store identity (rig.db size+mtime) that `rig index`/`rig graph` change. See QueryCache.
        var rigDir = Path.Combine(workingDirectory, ".rig");
        var storeKey = StoreKey(Path.Combine(rigDir, "rig.db"));
        using var cache = args.Contains("--no-cache") ? null : QueryCache.Open(rigDir, storeKey);
        var cacheKey = cache is null
            ? null
            : TreeCacheKey(storeKey, RulesFingerprint.Compute(workingDirectory, extraRules), fromPattern, maxDepth, mode, raw);
        var cached = cacheKey is not null && cache!.Get(cacheKey) is { } blob ? TreeCacheCodec.Decode(blob) : null;
        // Render sidecar: everything render needs from the graph (seam effects + locations), keyed by the
        // forest key PLUS the effect filters. Seam effects are derived from the FILTERED effects, and
        // filters are deliberately absent from the forest key (effects are cached unfiltered and re-filtered
        // per query), so the sidecar must key on them — else a differently-filtered warm query would render
        // stale seam summaries. Locations are filter-free but ride along (negligible). No filters → a stable
        // shared key. A sidecar hit lets the warm path skip the graph load entirely.
        var sidecarKey = cacheKey is null ? null : cacheKey + ":sidecar:" + EffectFilterSignature(args);
        var sidecar =
            cached is not null && sidecarKey is not null && cache!.Get(sidecarKey) is { } scBlob ? RenderSidecarCodec.Decode(scBlob) : null;
        timer.Lap($"cache lookup (forest={cached is not null}, sidecar={sidecar is not null})");

        FactGraphData? graph = null; // stays null on a full hit (forest + sidecar) — the graph is never loaded
        IReadOnlyList<TraceNode> roots;
        IReadOnlyList<DerivedEffect> effects;
        if (cached is not null && sidecar is not null)
        {
            // FULL HIT: forest + effects + render sidecar all cached → render without touching the graph.
            roots = cached.Forest;
            effects = cached.Effects;
            timer.Lap("forest + sidecar hit (no graph load)");
        }
        else if (cached is not null)
        {
            // Forest hit but no sidecar (a pre-sidecar entry, or first run under this filter): load the
            // shaped graph to render — the sidecar is written below so the NEXT query is a full hit.
            roots = cached.Forest;
            effects = cached.Effects;
            graph = await LoadShapedTraversalGraphAsync(
                context,
                fromPattern,
                SqlReachability.Direction.Forward,
                handoffRules,
                factoryRules,
                cutRules,
                contextRules
            );
            if (!raw)
                graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
            timer.Lap("graph load + event marking (cache hit)");
        }
        else
        {
            var inputs = await LoadEffectReachInputsAsync(
                context,
                fromPattern,
                SqlReachability.Direction.Forward,
                handoffRules,
                factoryRules,
                cutRules,
                contextRules
            );
            graph = inputs.Graph;
            timer.Lap("graph + invocations load");
            // Event subscriptions (`someEvent += Handler`) are deferred handlers, not synchronous calls —
            // mark them as handoffs so the sync tree doesn't expand the handler as if RegisterEvents ran it.
            if (!raw)
                graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));

            roots = FactPathFinder.BuildTree(graph, fromPattern, maxDepth, mode: mode);
            timer.Lap("event marking + BuildTree");
            if (roots.Count == 0)
            {
                effects = [];
            }
            else
            {
                // Effects per enclosing method — same derivation as `reaches` (incl. throws).
                var effectRules = FactEffectRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
                var observationRules = FactObservationRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
                effects = FactEffectDeriver.Derive(
                    inputs.Invocations,
                    effectRules,
                    providerFilter: null,
                    baseEdges: BaseEdgeTuples(graph),
                    ctorRefs: inputs.CtorRefs,
                    observationRules: observationRules,
                    throwRefs: inputs.ThrowRefs
                );
                // Cache UNFILTERED effects; --only/--exclude are applied below so they don't fragment the
                // key. Best-effort: encoding a pathologically deep forest (or any IO hiccup) must never
                // fail the query — on error we simply don't cache and the next run recomputes.
                if (cacheKey is not null)
                {
                    try
                    {
                        cache!.Put(cacheKey, TreeCacheCodec.Encode(new TreeCachePayload(roots, effects)));
                    }
                    catch (Exception ex)
                        when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or IOException)
                    {
                        // skip caching this result
                    }
                }
                timer.Lap("derive effects + cache store");
            }
        }

        if (roots.Count == 0)
        {
            output.WriteLine($"No symbol matches '{fromPattern}'.");
            return 1;
        }

        // Deployment attribution (opt-in via deployments.json) + EP-site lookup, so tree nodes that are
        // themselves entry points get the ▶ kind + service chip. Null when unconfigured (default tree).
        // Locations (method DocID -> file:line): from the sidecar on a full hit, else from the graph.
        // One map serves both the EP-chip site lookup and `--files` links.
        IReadOnlyDictionary<string, (string? File, int Line)> locations =
            sidecar?.Locations
            ?? graph!
                .Methods.GroupBy(m => m.SymbolId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => ((string?)g.First().FilePath, g.First().Line), StringComparer.Ordinal);

        var deployments = await DeploymentMap.LoadAsync(
            workingDirectory,
            (await Reads.ListRunsAsync(context)).FirstOrDefault()?.SolutionPath
        );
        // EP context is built from `locations` (not the graph), so it works on the no-graph full-hit path.
        // The expensive, pattern-independent site->kind map is its own cache (LoadOrDeriveEpSiteKind).
        var epContext = deployments.IsEmpty
            ? null
            : new EpRenderContext(
                deployments,
                locations,
                await LoadOrDeriveEpSiteKindAsync(context, workingDirectory, extraRules, handoffRules, !args.Contains("--no-cache"))
            );
        timer.Lap("deployment map + entry-point derivation");

        effects = ApplyEffectFilters(effects, args); // --only / --exclude (e.g. --exclude throw)

        var emoji = FactEffectEmojiProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var effectsByMethod = effects
            .Where(e => e.EnclosingSymbolId is not null)
            .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => FormatEffectGroup(g, emoji), StringComparer.Ordinal);

        // `--full` renders effects AND unresolved library calls as leaf nodes (call site + line), source-
        // ordered per method, rather than the compact inline tag. Only built in --full; other modes never
        // read it, so the extra library-call query never touches the default/compact path.
        IReadOnlyDictionary<string, List<string>>? effectLeavesByMethod = null;
        if (full)
        {
            var leafRows = new List<(string Method, int Line, string Body)>();
            foreach (var e in effects.Where(e => e.EnclosingSymbolId is not null))
                leafRows.Add((e.EnclosingSymbolId!, e.Line, FormatEffectLeaf(e, emoji)));

            // Unresolved library calls: invocations to a referenced-assembly target that produced no effect
            // (no rule matched). Bounded to the rendered tree's methods; subtract the effect call-sites so a
            // call already shown as an effect leaf isn't doubled.
            var treeMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
                CollectTreeMethods(root, treeMethods);
            var effectSites = effects
                .Where(e => e.EnclosingSymbolId is not null)
                .Select(e => (e.EnclosingSymbolId!, e.Line))
                .ToHashSet();
            var libCalls = await Reads.LoadLibraryCallSitesAsync(context, treeMethods);
            foreach (
                var c in libCalls
                    .Where(c => c.Enclosing is not null && !effectSites.Contains((c.Enclosing!, c.Line)))
                    .DistinctBy(c => (c.Enclosing, c.Target, c.Line))
            )
                leafRows.Add((c.Enclosing!, c.Line, FormatUnresolvedLeaf(c.Target, c.FilePath, c.Line)));

            effectLeavesByMethod = leafRows
                .GroupBy(r => r.Method, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Line).Select(r => r.Body).ToList(), StringComparer.Ordinal);
        }

        if (summary)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
                CollectTreeMethods(root, seen);
            var hits = effects.Where(e => e.EnclosingSymbolId is not null && seen.Contains(e.EnclosingSymbolId)).ToList();
            output.WriteLine($"From: {fromPattern}");
            output.WriteLine($"Reachable methods: {seen.Count}");
            output.WriteLine($"Effects on reachable methods: {hits.Count}");
            foreach (var g in hits.GroupBy(h => (h.Provider, h.Operation)).OrderByDescending(g => g.Count()))
                output.WriteLine($"  {g.Count(), 4}  {g.Key.Provider} {g.Key.Operation}");
            timer.Total();
            return 0;
        }

        // --effects: the compact view — ONLY the methods that carry an effect, listed in source/DFS order
        // (deduped), each with its effect glyphs. Drops the entire call skeleton, so a 10-screen tree
        // collapses to one line per effectful method — "what does this entry point actually DO".
        if (effectsOnly)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
                CollectEffectful(root, effectsByMethod, ordered, seen);
            output.WriteLine($"From: {fromPattern}  ({ordered.Count} effectful method(s), source order)");
            foreach (var sym in ordered)
                output.WriteLine($"  {ShortName(sym)}\n      {string.Join("  ", effectsByMethod[sym])}");
            timer.Total();
            return 0;
        }

        // Seam effects: from the sidecar on a full hit, else computed from the (filtered) effects + graph.
        IReadOnlyDictionary<string, List<string>> seamEffects;
        if (sidecar is not null)
            seamEffects = sidecar.Value.SeamEffects;
        else
        {
            var structuredByMethod = effects
                .Where(e => e.EnclosingSymbolId is not null)
                .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            seamEffects = ComputeSeamEffects(
                roots,
                renderRules,
                graph!,
                maxDepth,
                mode,
                structuredByMethod,
                (p, o) => FactEffectEmojiProvider.For(emoji, p, o)
            );
        }

        // `--files`: per-node definition location (relpath:line) from the loaded methods, for source links.
        // `--signatures` (alias --sig): per-node compact param signature, to tell same-named overloads apart.
        var files = args.Contains("--files");
        var signatures = args.Contains("--signatures") || args.Contains("--sig");
        var locById = files ? locations : null;

        // Populate the render sidecar (best-effort) so the next warm query renders with NO graph load.
        // Only when a graph was actually loaded (cold or sidecar-miss) and caching is on.
        if (graph is not null && sidecarKey is not null)
        {
            try
            {
                cache!.Put(sidecarKey, RenderSidecarCodec.Encode(seamEffects, locations));
            }
            catch (Exception ex)
                when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or IOException)
            {
                // skip caching the sidecar
            }
        }

        foreach (var root in roots)
        {
            if (!full && !SubtreeHasEffect(root, effectsByMethod))
                continue;
            // Fold single-impl interface/base hops (IFoo.M -> Foo.M when there's exactly one target)
            // into the impl, with a «via IFoo» marker — exact, no info loss. --raw shows the raw hops.
            RenderTreeNode(
                raw ? root : FoldSingleImplHops(root, effectsByMethod),
                prefix: "",
                isLast: true,
                isRoot: true,
                effectsByMethod,
                prune: !full,
                renderRules,
                seamEffects,
                output,
                files,
                locById,
                signatures,
                cutRules,
                epContext,
                full,
                effectLeavesByMethod
            );
        }
        timer.Lap("seam effects + render");
        timer.Total();
        return 0;
    }

    // Builds the EP-render context for a tree: the SymbolId->site map (from the loaded graph) and the
    // site->kind map (from the SAME derived entry-point set `derive` emits, incl. promoted handoff
    // origins). Returns null when deployments are unconfigured, so the default tree pays no cost.
    private static async Task<EpRenderContext?> BuildEpContextAsync(
        RigDbContext context,
        FactGraphData graph,
        string workingDirectory,
        IReadOnlyList<string> extraRules,
        IReadOnlyList<FactHandoffRule> handoffRules,
        DeploymentMap deployments,
        bool useCache = true
    )
    {
        if (deployments.IsEmpty)
            return null;

        // The site->kind map is the expensive, PATTERN-INDEPENDENT half — derive-or-cache it once per
        // (store + rules). The symbol->site map below is cheap and rebuilt fresh from THIS query's graph.
        var epSiteKind = await LoadOrDeriveEpSiteKindAsync(context, workingDirectory, extraRules, handoffRules, useCache);

        var siteById = graph
            .Methods.GroupBy(m => m.SymbolId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => ((string?)g.First().FilePath, g.First().Line), StringComparer.Ordinal);

        return new EpRenderContext(deployments, siteById, epSiteKind);
    }

    // Load the whole-store entry-point site map: (file,line) -> (kind, capability requirements), covering
    // both rule-detected EPs and promoted handoff origins. A pure function of the store + effective rules
    // (NO traversal pattern). Three tiers, fastest first:
    //   1. The entry_point_sites table `rig graph` materialized — INDEX data, read via raw ADO (no EF, no
    //      whole-store load, no derive). Used whenever the effective rules match what graph was built with,
    //      regardless of --no-cache (it's index data, like call_edges), so it serves the common path.
    //   2. The .rig/cache.db query cache — for --rules queries (rule-hash mismatch on the table) when
    //      caching is on; derives once then memoizes.
    //   3. A live derive — --no-cache with a rule mismatch, or no materialized table yet.
    private static async Task<
        IReadOnlyDictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>
    > LoadOrDeriveEpSiteKindAsync(
        RigDbContext context,
        string workingDirectory,
        IReadOnlyList<string> extraRules,
        IReadOnlyList<FactHandoffRule> handoffRules,
        bool useCache
    )
    {
        var rulesHash = RulesFingerprint.Compute(workingDirectory, extraRules);

        // Tier 1: the materialized index table (built at `rig graph` under the default rules).
        if (await EntryPointSiteStore.LoadAsync(context, rulesHash) is { } materialized)
            return materialized;

        if (!useCache)
            return await DeriveEpSiteKindAsync(context, workingDirectory, extraRules, handoffRules);

        // Tier 2: query cache (handles --rules, which the table doesn't cover).
        var rigDir = Path.Combine(workingDirectory, ".rig");
        var storeKey = StoreKey(Path.Combine(rigDir, "rig.db"));
        using var cache = QueryCache.Open(rigDir, storeKey);
        var key = cache is null ? null : EpCacheKey(storeKey, rulesHash);
        if (key is not null && cache!.Get(key) is { } blob && EpSiteCacheCodec.Decode(blob) is { } hit)
            return hit;

        var derived = await DeriveEpSiteKindAsync(context, workingDirectory, extraRules, handoffRules);
        if (key is not null)
        {
            try
            {
                cache!.Put(key, EpSiteCacheCodec.Encode(derived));
            }
            catch (Exception ex)
                when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or IOException)
            {
                // skip caching this result
            }
        }
        return derived;
    }

    // The actual whole-store EP derivation (uncached): rule EPs + class-inheritance EPs + promoted handoff
    // origins, flattened to a (file,line)->(kind,requires) map. Pulled out of BuildEpContextAsync so both
    // the lazy query path and the eager `rig graph` warm-up share one definition.
    private static async Task<Dictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>> DeriveEpSiteKindAsync(
        RigDbContext context,
        string workingDirectory,
        IReadOnlyList<string> extraRules,
        IReadOnlyList<FactHandoffRule> handoffRules
    )
    {
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var classRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(workingDirectory, extraRules);
        var derivedEps = FactEntryPointDeriver.Derive(epData, epRules, classRules);
        var classifiedHandoffs = (await Reads.DeriveHandoffEntryPointsAsync(context, int.MaxValue, handoffRules))
            .Where(h => h.Dispatcher is not null)
            .ToList();

        var epSiteKind = new Dictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>();
        foreach (var e in derivedEps.Concat(PromoteHandoffOrigins(classifiedHandoffs, derivedEps)))
            epSiteKind[(e.FilePath, e.Line)] = (e.Kind, e.Requires);
        return epSiteKind;
    }

    // Materialize the pattern-independent EP-site set as a first-class table right after `rig graph`
    // rebuilds the store, so every later query reads it via raw ADO (no EF, no whole-store load, no derive)
    // instead of paying the ~2.1s derivation. Gated on deployments.json — projects without deployment
    // attribution never use the EP set, so they pay nothing. Built with the DEFAULT rules and stamped with
    // their hash; a --rules query sees the mismatch and derives live under its own rules.
    private static async Task MaterializeEntryPointSitesAsync(RigDbContext context, string workingDirectory)
    {
        if (!File.Exists(Path.Combine(workingDirectory, "deployments.json")))
            return;
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory).ToArray();
        var sites = await DeriveEpSiteKindAsync(context, workingDirectory, [], handoffRules);
        await EntryPointSiteStore.PersistAsync(context, sites, RulesFingerprint.Compute(workingDirectory, []));
    }

    // Cache key for the pattern-INDEPENDENT EP-site map: store identity + rule fingerprint only (no
    // pattern, no traversal params), so a single derivation serves every query against the store.
    private static string EpCacheKey(string storeKey, string rulesHash)
    {
        var material = $"ep|v1|{storeKey}|{rulesHash}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    // Identity of the current store for cache keying + invalidation: rig.db size + last-write time.
    // `rig index` publishes a fresh db (atomic rename → new mtime/size) and `rig graph` rewrites the
    // derived edge tables in place (mtime changes), so any reindex shifts this — old cache entries no
    // longer match and are purged. Missing db → a constant sentinel (cache simply never hits).
    private static string StoreKey(string dbPath)
    {
        try
        {
            var info = new FileInfo(dbPath);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "absent";
        }
        catch (IOException)
        {
            return "absent";
        }
    }

    // The cache key for a `rig tree` forest+effects artifact: everything the artifact is a function of —
    // the store identity, the effective rule fingerprint, and the traversal parameters. `v1` is the
    // payload-schema version (bump to ignore older blobs). Render-only flags (--files/--summary/--effects
    // and --only/--exclude) are deliberately absent: they don't change the forest or the unfiltered
    // effects, only how they're presented, so they must not fragment the cache.
    private static string TreeCacheKey(
        string storeKey,
        string rulesHash,
        string fromPattern,
        int maxDepth,
        FactPathFinder.TraversalMode mode,
        bool raw
    )
    {
        var material = $"tree|v1|{storeKey}|{rulesHash}|{fromPattern}|{maxDepth}|{mode}|{raw}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    // "  ▶ kind  ⟦svc⟧" suffix for a from/root symbol (reaches/path/callers roots), or "" when there is
    // no deployment context or the symbol has no known declaration site.
    private static string HeaderSuffix(EpRenderContext? epContext, string symbolId)
    {
        var tag = epContext?.HeaderTag(symbolId);
        return string.IsNullOrEmpty(tag) ? "" : $"  {tag}";
    }

    // Collapses single-target dispatch hops: when a node has exactly one child reached by impl-/override-
    // dispatch with no fan-out (Fanout <= 1) and the node itself carries no effect, the lone interface/base
    // hop is folded into its impl — the impl is promoted into the node's slot with a FoldedVia marker
    // naming the interface. Exact (a 1-target dispatch is determined, not an approximation); recurses
    // bottom-up so a chain of single-impl hops collapses. The node's own effect, a fan-out (>1), a
    // truncated/cut leaf, or extra children all block the fold (then children are folded in place).
    private static TraceNode FoldSingleImplHops(TraceNode node, IReadOnlyDictionary<string, List<string>> effectsByMethod)
    {
        var kids = node.Children.Select(c => FoldSingleImplHops(c, effectsByMethod)).ToList();
        if (
            kids.Count == 1
            && kids[0].EdgeKind is "impl-dispatch" or "override-dispatch"
            && kids[0].Fanout <= 1
            && !kids[0].Truncated
            && !effectsByMethod.ContainsKey(node.SymbolId)
            && !node.Truncated
        )
            return kids[0] with { FoldedVia = FoldedViaTypeName(node.SymbolId) };
        return node with { Children = kids };
    }

    // The folded-away interface/base TYPE's short name (e.g. "M:Ns.IFoo.Bar``1(..)" -> "IFoo"), for the
    // «via IFoo» marker — the impl's own name already carries the method, so only the type is informative.
    private static string FoldedViaTypeName(string methodSymbolId)
    {
        var paren = methodSymbolId.IndexOf('(');
        var head = paren >= 0 ? methodSymbolId.Substring(0, paren) : methodSymbolId;
        var lastDot = head.LastIndexOf('.');
        return ShortName(lastDot >= 0 ? head.Substring(0, lastDot) : head);
    }

    // Collects effect-bearing methods in DFS (source) order, deduped — the backing of `tree --effects`.
    private static void CollectEffectful(
        TraceNode node,
        IReadOnlyDictionary<string, List<string>> effectsByMethod,
        List<string> ordered,
        HashSet<string> seen
    )
    {
        if (effectsByMethod.ContainsKey(node.SymbolId) && seen.Add(node.SymbolId))
            ordered.Add(node.SymbolId);
        foreach (var c in node.Children)
            CollectEffectful(c, effectsByMethod, ordered, seen);
    }

    private static void CollectTreeMethods(TraceNode node, HashSet<string> seen)
    {
        seen.Add(node.SymbolId);
        foreach (var c in node.Children)
            CollectTreeMethods(c, seen);
    }

    // True when this node directly has an effect or any descendant does. A "↺seen" (Truncated) node
    // has no children here, so only its own effect counts — that's sound: the effects under the
    // method's real subtree are printed under its first (expanded) occurrence, so nothing is lost.
    private static bool SubtreeHasEffect(TraceNode node, IReadOnlyDictionary<string, List<string>> effectsByMethod)
    {
        if (effectsByMethod.ContainsKey(node.SymbolId))
            return true;
        foreach (var c in node.Children)
            if (SubtreeHasEffect(c, effectsByMethod))
                return true;
        return false;
    }

    // Renders the call tree with box-drawing connectors (├─ └─ │). When pruning, a node's VISIBLE
    // children are filtered first so the last visible child gets └─ correctly. The root prints flush-left.
    internal static void RenderTreeNode(
        TraceNode node,
        string prefix,
        bool isLast,
        bool isRoot,
        IReadOnlyDictionary<string, List<string>> effectsByMethod,
        bool prune,
        FactRenderRules renderRules,
        // Precomputed REALISTIC effect union per collapse-seam hub (keyed by hub DocID): the de-duped
        // effects over the hub's full reach closure, NOT the truncated rendered subtree. Empty falls
        // back to a subtree walk (the closure was unavailable, e.g. unit tests).
        IReadOnlyDictionary<string, List<string>> seamEffects,
        TextWriter output,
        // `--files`: append each node's DEFINITION location (relpath:line) so the tree links to source.
        // locById maps SymbolId -> (file, line) from the loaded methods; null/false leaves nodes bare.
        bool files = false,
        IReadOnlyDictionary<string, (string? File, int Line)>? locById = null,
        // `--signatures`: show each method's compact parameter signature so same-named overloads differ.
        bool signatures = false,
        // Traversal-cut rules for the «cut» marker: a node matching a cut rule gets a visible marker
        // indicating that its subtree was cut during traversal (not just render). Null = no markers.
        IReadOnlyList<FactTraversalCutRule>? cutRules = null,
        // Deployment/EP context: when supplied, a node that is itself a rule-detected entry point is
        // marked with the ▶ kind + service chip. Null = no EP marking (default tree).
        EpRenderContext? epContext = null,
        // `--full`: render effects as provenance leaf nodes (call site + line) BELOW each method instead of
        // the inline {…} tag. effectLeavesByMethod carries the precomputed leaf bodies. false/null = the
        // compact inline-tag rendering used by default/--effects/--summary.
        bool full = false,
        IReadOnlyDictionary<string, List<string>>? effectLeavesByMethod = null
    )
    {
        // Compute visible children first — the fan-out label must reflect how many branches are
        // actually rendered (pruning may drop effectless children, making ×2 fan-out misleading
        // when only 1 child survives).
        var children = prune ? node.Children.Where(c => SubtreeHasEffect(c, effectsByMethod)).ToList() : node.Children.ToList();
        var childPrefix = isRoot ? "" : prefix + (isLast ? "   " : "│  ");

        var dispatchTag = node.DispatchBasis == "heuristic" ? $"{node.EdgeKind} ~heuristic" : node.EdgeKind;
        // A folded single-impl hop shows «via IFoo» (the collapsed interface) in place of the dispatch tag.
        var dispatch =
            node.FoldedVia is not null ? $" «via {node.FoldedVia}»"
            : node.EdgeKind is "impl-dispatch" or "override-dispatch"
                ? (children.Count > 1 ? $" «{dispatchTag} ×{children.Count} fan-out»" : $" «{dispatchTag}»")
            : "";
        // An async handoff hop (only present under --async): mark the cross-thread boundary.
        var handoff = node.EdgeKind == EdgeKinds.Handoff ? $" ⤳handoff via {ShortName(node.HandoffVia)} [cross_thread]" : "";
        var loop = node.LoopKind is null ? "" : $" 🔁[{ShortLoop(node.LoopDetail)}]";
        // Identical sibling edges collapsed under one parent (e.g. a generic method called once per
        // type-arg): show the call-site count rather than N repeated "↺seen" lines.
        var calls = node.CallSites > 1 ? $" ×{node.CallSites} calls" : "";
        var seen = node.Truncated ? " ↺seen" : "";
        // Opaque-type render rule: a matching non-root node is drawn as a leaf — its own effects still
        // print, but its subtree is suppressed (the type's internals aren't worth expanding).
        var opaque = isRoot ? null : renderRules.MatchOpaque(node.SymbolId);
        var opaqueTag = opaque is not null ? $" «opaque: {opaque.Label}»" : "";
        // Traversal-cut marker: a node whose successors were cut during traversal (empty children,
        // not because it has none, but because a cut rule stopped the walk). We detect this by
        // matching the cut rules against the node and checking it has no children (was a traversal leaf).
        var cutTag = "";
        if (cutRules is { Count: > 0 } && node.Children.Count == 0 && !node.Truncated)
        {
            FactTraversalCutRule? matchedCut = null;
            foreach (var rule in cutRules)
                if (rule.IsMatch(node.SymbolId))
                {
                    matchedCut = rule;
                    break;
                }
            if (matchedCut is not null)
                cutTag = $" «cut: {matchedCut.Label}»";
        }
        // --full hoists effects out to leaf nodes (below), so the inline {…} tag is suppressed in that mode.
        var fx = !full && effectsByMethod.TryGetValue(node.SymbolId, out var list) && list.Count > 0 ? "  {" + string.Join(", ", list) + "}" : "";
        var loc =
            files && locById is not null && locById.TryGetValue(node.SymbolId, out var l) && l.File is not null
                ? $"  📄 {ShortenPath(l.File)}:{l.Line}"
                : "";
        var name = PrettyGenericName(ShortName(node.SymbolId)) + (signatures ? ShortSignature(node.SymbolId) : "");
        // EP marker: when this node is itself a rule-detected entry point, wrap its name with "▶ kind"
        // and a trailing service chip — the same custom rendering used by derive/callers.
        var (epPrefix, epSuffix) = epContext?.ChipFor(node.SymbolId) ?? ("", "");
        var label = $"{epPrefix}{name}{dispatch}{handoff}{loop}{calls}{seen}{opaqueTag}{cutTag}{fx}{loc}{epSuffix}";
        output.WriteLine(isRoot ? label : $"{prefix}{(isLast ? "└─ " : "├─ ")}{label}");

        // Collapse-seam render rule: this node is a fan-out hub (e.g. a reflection service-locator or
        // an ORM entity-constructor factory). Fold its candidate children into ONE summary leaf —
        // the union of effects reachable through them + how many lines were hidden — instead of N
        // near-identical polymorphic subtrees that drown out the real call story. Computed here (ahead of
        // the effect-leaf pass) so leaf connectors know whether a trailing seam summary line follows.
        var seam = renderRules.MatchCollapseSeam(node.SymbolId);

        // --full: emit this method's effects as provenance leaf nodes (call site + line) ahead of the call
        // children, so the effect-producing calls (e.g. ExecuteAsync) are visible rather than folded into a
        // tag. The last leaf gets └─ only when nothing trails it — an opaque node renders no children, a
        // collapsed seam renders exactly one summary line, otherwise the visible call children follow.
        if (full && effectLeavesByMethod is not null && effectLeavesByMethod.TryGetValue(node.SymbolId, out var fxLeaves) && fxLeaves.Count > 0)
        {
            var trailing = opaque is not null ? 0 : (seam is not null && children.Count > 0 ? 1 : children.Count);
            for (var i = 0; i < fxLeaves.Count; i++)
            {
                var lastLeaf = trailing == 0 && i == fxLeaves.Count - 1;
                output.WriteLine($"{childPrefix}{(lastLeaf ? "└─ " : "├─ ")}{fxLeaves[i]}");
            }
        }

        if (opaque is not null)
            return;

        if (seam is not null && children.Count > 0)
        {
            // Lines-hidden is the rendered subtree size; the effect union is the REALISTIC reach-closure
            // set (precomputed), falling back to the subtree walk when no closure was supplied.
            var (subtreeEffects, hidden) = SummarizeSubtrees(children, prune, effectsByMethod);
            var effects = seamEffects.TryGetValue(node.SymbolId, out var realistic) ? realistic : subtreeEffects;
            // Aggregated provider:operation tallies are few; the cap mainly bounds the raw-subtree fallback.
            const int cap = 30;
            var shown = effects.Take(cap).Select(e => "{" + e + "}");
            var overflow = effects.Count > cap ? $" …+{effects.Count - cap} more" : "";
            var fxUnion = effects.Count == 0 ? "" : "  " + string.Join(" ", shown) + overflow;
            output.WriteLine(
                $"{childPrefix}└─ ⋯ {children.Count} dispatch targets collapsed [seam: {seam.Label}]{fxUnion}  (+{hidden} lines hidden — `tree --raw` to expand)"
            );
            return;
        }

        for (var i = 0; i < children.Count; i++)
            RenderTreeNode(
                children[i],
                childPrefix,
                i == children.Count - 1,
                isRoot: false,
                effectsByMethod,
                prune,
                renderRules,
                seamEffects,
                output,
                files,
                locById,
                signatures,
                cutRules,
                epContext,
                full,
                effectLeavesByMethod
            );
    }

    // Finds every collapse-seam hub in the tree(s) and precomputes its REALISTIC effect summary: the
    // effects over the hub's full forward reach closure (NOT the dedup/depth-truncated rendered subtree),
    // AGGREGATED by provider:operation with a distinct-resource count (e.g. `📥 llblgen:fetch ×42`). A
    // folded seam reaches hundreds of distinct resource-typed effects; listing them is noise, so we
    // collapse them after retrieval into a compact per-operation tally — what the folded region does, at
    // a readable altitude. Bounded by the tree's depth + the reach node budget.
    private static Dictionary<string, List<string>> ComputeSeamEffects(
        IReadOnlyList<TraceNode> roots,
        FactRenderRules renderRules,
        FactGraphData graph,
        int maxDepth,
        FactPathFinder.TraversalMode mode,
        IReadOnlyDictionary<string, List<DerivedEffect>> structuredByMethod,
        Func<string, string, string> emojiFor
    )
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (renderRules.CollapseSeams.Count == 0)
            return result;

        var hubs = new HashSet<string>(StringComparer.Ordinal);
        void FindHubs(TraceNode node)
        {
            if (renderRules.MatchCollapseSeam(node.SymbolId) is not null)
                hubs.Add(node.SymbolId);
            foreach (var child in node.Children)
                FindHubs(child);
        }
        foreach (var root in roots)
            FindHubs(root);

        foreach (var hub in hubs)
        {
            var reach = FactPathFinder.ReachesWithFanout(graph, hub, maxDepth, mode: mode);
            // Distinct resource types per (provider, operation) over the whole reach closure.
            var perOp = new Dictionary<(string Provider, string Operation), HashSet<string>>();
            foreach (var sym in reach.Keys)
                if (structuredByMethod.TryGetValue(sym, out var list))
                    foreach (var effect in list)
                    {
                        if (!perOp.TryGetValue((effect.Provider, effect.Operation), out var resources))
                            perOp[(effect.Provider, effect.Operation)] = resources = new HashSet<string>(StringComparer.Ordinal);
                        resources.Add(effect.ResourceType);
                    }
            result[hub] = perOp
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key.Provider, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.Operation, StringComparer.Ordinal)
                .Select(kv => $"{emojiFor(kv.Key.Provider, kv.Key.Operation)} {kv.Key.Provider}:{kv.Key.Operation} ×{kv.Value.Count}")
                .ToList();
        }
        return result;
    }

    // Walks a set of subtrees (respecting the same prune filter the renderer uses) and returns the
    // de-duplicated union of effect glyphs found in them, in first-seen order, plus the total rendered
    // node count. Backs the collapse-seam summary line so a folded fan-out still reports what it touches.
    private static (List<string> Effects, int Nodes) SummarizeSubtrees(
        IReadOnlyList<TraceNode> nodes,
        bool prune,
        IReadOnlyDictionary<string, List<string>> effectsByMethod
    )
    {
        var effects = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        void Walk(TraceNode node)
        {
            count++;
            if (effectsByMethod.TryGetValue(node.SymbolId, out var list))
                foreach (var effect in list)
                    if (seen.Add(effect))
                        effects.Add(effect);
            var kids = prune ? node.Children.Where(c => SubtreeHasEffect(c, effectsByMethod)) : node.Children;
            foreach (var child in kids)
                Walk(child);
        }

        foreach (var node in nodes)
            Walk(node);
        return (effects, count);
    }

    // rig callers <toPattern> [--roots|--entrypoints] [--async] [--rules <path>...] [--maxdepth|--depth <n>]
    // Reverse reachability over the fact graph: every method that can reach toPattern (transitive
    // callers, incl. reverse interface/override dispatch). DEFAULTS TO SYNCHRONOUS (handoffs cut) — the
    // right lens for "who touches X" attribution; `--async` also walks handoffs (the "could eventually
    // involve X on some thread" superset). --roots filters to entry-point candidates (reachable methods
    // with no predecessor — framework/DI/reflection-invoked tops, a heuristic that also surfaces unbound
    // interface members). --entrypoints filters to the RULE-DETECTED entry points (the `rig derive` set)
    // that reach the target — the precise "which of my real entry points touch this code". Coverage is
    // bounded by what's indexed.
    private static async Task<int> RunCallersAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine(
                "Usage: rig callers <toPattern> [--orphans|--entrypoints] [--async] [--rules <path>...] [--depth <n>]"
            );
            return 2;
        }
        var toPattern = args[1];
        var maxDepth = MaxDepthOf(args);
        var rootsOnly = args.Contains("--orphans") || args.Contains("--roots"); // --roots: deprecated alias
        var entrypointsOnly = args.Contains("--entrypoints");
        if (rootsOnly && entrypointsOnly)
        {
            error.WriteLine("Options --orphans and --entrypoints can't be combined for 'rig callers'.");
            return 2;
        }
        var mode = TraversalModeOf(args);
        var raw = args.Contains("--raw");
        var extraRules = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--rules" && i + 1 < args.Length)
            {
                extraRules.Add(Path.GetFullPath(args[i + 1]));
                i++;
            }
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        // callers walks the SAME shaped graph as path/reaches/tree — monomorphized generic factories +
        // cut + context rules, carried on the graph and honoured SYMMETRICALLY by the reverse traversal
        // (a cut node yields no successors forward, so it is never a predecessor in reverse). Before this,
        // callers walked the raw call_edges and fanned reflection/service-locator seams (e.g.
        // ProvideService<T>, ~1k callers) out to thousands of unrelated entry points — disagreeing with
        // what `path`/`reaches` reported for the same target. `--raw` bypasses shaping (the unfiltered
        // reverse closure, for inspecting the exact plumbing).
        var factoryRules = raw
            ? Array.Empty<FactGenericFactoryRule>()
            : FactGenericFactoryRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var cutRules = raw
            ? Array.Empty<FactTraversalCutRule>()
            : FactTraversalCutRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var contextRules = raw
            ? Array.Empty<FactContextDispatchRule>()
            : FactContextDispatchRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);

        await using var context = OpenReadContext(workingDirectory);

        // One shaped reverse subgraph (bounded when `rig graph` has run, else the full EF graph) drives
        // all three callers modes — the set, the no-predecessor roots, and the rule-detected entrypoints.
        var graph = await LoadShapedTraversalGraphAsync(
            context,
            toPattern,
            SqlReachability.Direction.Reverse,
            handoffRules,
            factoryRules,
            cutRules,
            contextRules
        );

        if (entrypointsOnly)
            return await RunCallersEntryPointsAsync(
                context,
                graph,
                toPattern,
                maxDepth,
                mode,
                handoffRules,
                extraRules,
                workingDirectory,
                output
            );

        // Deployment/EP context for the from-symbol annotations (opt-in via deployments.json). Null/no-op
        // when unconfigured. Built once here for the roots + reachable listings.
        var deployments = await DeploymentMap.LoadAsync(
            workingDirectory,
            (await Reads.ListRunsAsync(context)).FirstOrDefault()?.SolutionPath
        );
        var epContext = await BuildEpContextAsync(
            context,
            graph,
            workingDirectory,
            extraRules,
            handoffRules,
            deployments,
            !args.Contains("--no-cache")
        );

        if (rootsOnly)
        {
            var roots = FactPathFinder.EntryRootsReaching(graph, toPattern, maxDepth, mode: mode);
            if (roots.Count == 0)
            {
                output.WriteLine($"No entry-point candidates reach '{toPattern}' (or no symbol matches).");
                return 1;
            }
            output.WriteLine($"Entry-point candidates reaching '{toPattern}': {roots.Count}");
            foreach (var r in roots)
                output.WriteLine($"  {r}{HeaderSuffix(epContext, r)}");
            return 0;
        }

        var reachable = FactPathFinder.ReachedBy(graph, toPattern, maxDepth, mode: mode);
        if (reachable.Count == 0)
        {
            output.WriteLine($"No symbol matches '{toPattern}'.");
            return 1;
        }
        output.WriteLine($"Methods that reach '{toPattern}': {reachable.Count}");
        foreach (var kv in reachable.OrderBy(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(300))
            output.WriteLine($"  d{kv.Value}  {ShortName(kv.Key)}");
        return 0;
    }

    // `rig callers <toPattern> --entrypoints` — the RULE-DETECTED entry points (same set `rig derive`
    // emits: page/action/class-inheritance handlers + promoted async-handoff origins) whose body is in
    // the REVERSE closure of toPattern, i.e. the real entry points that actually reach the target code.
    // Unlike `--roots` (a no-predecessor heuristic that surfaces unbound interface methods / framework
    // shims as "candidates"), this is the deterministic rule set intersected with reachability. The join
    // key is the declaration site (FilePath, Line) — a derived EP carries no DocID, but its handler
    // method's symbol fact shares the same site, so an EP is "touching" when some reverse-reachable
    // method is declared at the EP's site. Default is synchronous-only; --async also counts EPs that
    // reach the target across a handoff (scheduled). Honors --maxdepth.
    private static async Task<int> RunCallersEntryPointsAsync(
        RigDbContext context,
        FactGraphData graph,
        string toPattern,
        int maxDepth,
        FactPathFinder.TraversalMode mode,
        IReadOnlyList<FactHandoffRule> handoffRules,
        IReadOnlyList<string> extraRules,
        string workingDirectory,
        TextWriter output
    )
    {
        // Reverse closure of the target (every method that reaches it) over the SAME shaped graph the
        // caller loaded — so the rule-detected entry points are intersected with the cut-shaped reach,
        // not the raw fan-out.
        var reachable = FactPathFinder.ReachedBy(graph, toPattern, maxDepth, mode: mode).Keys.ToHashSet(StringComparer.Ordinal);
        if (reachable.Count == 0)
        {
            output.WriteLine($"No symbol matches '{toPattern}'.");
            return 1;
        }

        var deployments = await DeploymentMap.LoadAsync(
            workingDirectory,
            (await Reads.ListRunsAsync(context)).FirstOrDefault()?.SolutionPath
        );

        // (FilePath, Line) of every reverse-reachable method — the join key against derived EP sites.
        var methods = await Reads.LoadDeadCodeMethodsAsync(context);
        var reachableSites = methods.Where(m => reachable.Contains(m.SymbolId)).Select(m => (m.FilePath, m.Line)).ToHashSet();

        // The rule-detected entry points (identical derivation to `rig derive`) + promoted handoff origins.
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var classRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(workingDirectory, extraRules);
        var derivedEps = FactEntryPointDeriver.Derive(epData, epRules, classRules);
        var classifiedHandoffs = (await Reads.DeriveHandoffEntryPointsAsync(context, int.MaxValue, handoffRules))
            .Where(h => h.Dispatcher is not null)
            .ToList();
        var allEps = derivedEps.Concat(PromoteHandoffOrigins(classifiedHandoffs, derivedEps));

        var touching = allEps
            .Where(e => reachableSites.Contains((e.FilePath, e.Line)))
            .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
            .Select(g => (g.Key.Kind, g.Key.Route, g.Key.FilePath, g.Key.Line, g.First().Requires))
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Route, StringComparer.Ordinal)
            .ToList();

        if (touching.Count == 0)
        {
            output.WriteLine($"No rule-detected entry points reach '{toPattern}'.");
            return 1;
        }
        output.WriteLine(
            $"Rule-detected entry points reaching '{toPattern}': {touching.Count}"
                + (mode == FactPathFinder.TraversalMode.AsyncInclude ? "  (--async: incl. scheduled paths)" : "")
        );
        foreach (var kindGroup in touching.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  {kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup)
                WriteEntryPointLine(output, deployments, e.Route, e.FilePath, e.Line, e.Requires);
        }
        if (!deployments.IsEmpty)
            WriteServiceSummary(touching.Select(t => (t.Kind, (string?)t.FilePath, t.Requires)), deployments, output);
        return 0;
    }

    // rig derive [--rules <path>...] [--limit <n>] — the stage-2 pass over facts (no Roslyn):
    // re-derives effects, page/action entry points, and delegate/method-group handoff entry
    // points from the reference index in a single command, one DB open, one rule load.
    // Effects and entry points are matched against the same AnalysisRuleSet JSON the Roslyn
    // pass uses (detectors are data, not code).
    private static async Task<int> RunDeriveAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        var limit = int.TryParse(GetOption(args, "--limit"), out var l) ? l : 40;
        var extraRules = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--rules" && i + 1 < args.Length)
            {
                extraRules.Add(Path.GetFullPath(args[i + 1]));
                i++;
            }
        }
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        await using var context = OpenReadContext(workingDirectory);

        // Deployment attribution (opt-in: only when deployments.json sits next to .rig). Maps each EP's
        // source file to the deployed service(s) whose process loads it, via the indexed solution's
        // ProjectReference graph. Empty (no-op) when the config is absent.
        var deployments = await DeploymentMap.LoadAsync(
            workingDirectory,
            (await Reads.ListRunsAsync(context)).FirstOrDefault()?.SolutionPath,
            error
        );

        // Classified handoffs (background/timer/actor/event) shared by the listing, the origin-EP
        // promotion, and the TSV output — derived once from the classified call graph.
        var handoffs = await Reads.DeriveHandoffEntryPointsAsync(context, int.MaxValue, handoffRules);
        var classifiedHandoffs = handoffs.Where(h => h.Dispatcher is not null).ToList();
        var unclassifiedHandoffCount = handoffs.Count - classifiedHandoffs.Count;

        // Entry-point fact data is loaded up front: its base edges also feed the effect deriver's
        // base-type gates (e.g. clientpage_proxy = declaring type derives MedDBase.Pages.ProxyBase).
        var epData = await Reads.LoadFactEntryPointDataAsync(context);

        // --- Effects (data-driven over facts) ---
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var effectRules = FactEffectRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var observationRules = FactObservationRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var effects = FactEffectDeriver.Derive(
            invocations,
            effectRules,
            providerFilter: null,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs,
            observationRules: observationRules,
            throwRefs: throwRefs
        );
        effects = ApplyEffectFilters(effects, args); // --only / --exclude (e.g. --exclude throw)

        // Machine-readable mode: emit full-fidelity rows (full DocIDs/paths) for tooling that joins
        // effects/entry points against the call graph. `rig derive --format tsv`.
        if (string.Equals(GetOption(args, "--format"), "tsv", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var e in effects)
                output.WriteLine(
                    $"effect\t{e.Provider}\t{e.Operation}\t{e.ResourceType}\t{e.EnclosingSymbolId}\t{e.FilePath}\t{e.Line}\t{string.Join(",", (e.Observations ?? []).Select(o => o.Type))}"
                );
            var tsvEpRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
            var tsvClassRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(workingDirectory, extraRules);
            var tsvEps = FactEntryPointDeriver.Derive(epData, tsvEpRules, tsvClassRules);
            // Trailing columns (comma-joined, empty when no deployments.json): `service` = the hosts
            // that LOAD the EP (link its code); `activeService` = the subset it is ACTIVE-IN after the
            // capability gate (== service when the EP is ungated). `service` is kept unchanged for
            // back-compat; tooling that wants runs-here filters on the new `activeService` column.
            foreach (var ep in tsvEps.Concat(PromoteHandoffOrigins(classifiedHandoffs, tsvEps)))
            {
                var loaded = deployments.ServicesForFile(ep.FilePath);
                var active = deployments.ActiveServices(loaded, ep.Requires);
                output.WriteLine(
                    $"entrypoint\t{ep.Kind}\t{ep.Method}\t{ep.Route}\t{ep.FilePath}\t{ep.Line}\t{string.Join(",", loaded)}\t{string.Join(",", active)}"
                );
            }
            return 0;
        }

        output.WriteLine($"Effects re-derived from facts: {effects.Count}");
        foreach (var group in effects.GroupBy(e => (e.Provider, e.Operation)).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  {group.Key.Provider} {group.Key.Operation}: {group.Count()}");
            foreach (var e in group.Take(limit / 8 + 1))
                output.WriteLine(
                    $"      {ShortName(e.ResourceType)}  <- {ShortName(e.EnclosingSymbolId)}  {ShortenPath(e.FilePath)}:{e.Line}"
                );
        }

        // --- Observations attached to effects (looped_effect / parallel_fanout / …, P2b) ---
        var observationGroups = effects
            .SelectMany(e => e.Observations ?? [])
            .GroupBy(o => o.Type)
            .OrderByDescending(g => g.Count())
            .ToArray();
        if (observationGroups.Length > 0)
        {
            output.WriteLine();
            output.WriteLine($"Observations on effects: {observationGroups.Sum(g => g.Count())}");
            foreach (var group in observationGroups)
                output.WriteLine($"  {group.Key}: {group.Count()}");
        }

        // --- Page + action entry points (fact-based BFS + attribute-ref detection) ---
        // epData was loaded above (shared with the effect deriver's base-type gates).
        var epRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var classRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(workingDirectory, extraRules);
        var derivedEps = FactEntryPointDeriver.Derive(epData, epRules, classRules);

        output.WriteLine();
        output.WriteLine($"Entry points re-derived from facts: {derivedEps.Count}");
        foreach (var kindGroup in derivedEps.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  {kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup.Take(limit / 4 + 1))
                WriteEntryPointLine(output, deployments, e.Route, e.FilePath, e.Line, e.Requires);
        }

        // --- Classified handoff entry points (Phase 1/3): dispatcher-consumed delegates, promoted to
        //     execution origins by kind (background/timer/actor/event), with the dispatcher + the
        //     registration site. The unclassified-methodGroup residual is collapsed to a count (it was
        //     a 4,503-entry firehose). Each emits an `async_handoff` observation at its registration.
        var origins = PromoteHandoffOrigins(classifiedHandoffs, derivedEps);
        output.WriteLine();
        output.WriteLine(
            $"Handoff entry points (classified): {classifiedHandoffs.Count}  "
                + $"(promoted origins after dedup: {origins.Count}; unclassified methodGroup residual: {unclassifiedHandoffCount})"
        );
        foreach (var kindGroup in classifiedHandoffs.GroupBy(h => h.Kind).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  {kindGroup.Key}: {kindGroup.Count()}");
            foreach (var h in kindGroup.Take(limit / 4 + 1))
            {
                var tag = deployments.IsEmpty ? "" : $"  {EntryPointRenderer.DeployTag(deployments, h.FilePath, h.Requires)}";
                output.WriteLine(
                    $"      {ShortName(h.Target)}  ⤳ via {h.Dispatcher}{tag}\n          registered in {ShortName(h.RegisteredIn)}  {ShortenPath(h.FilePath)}:{h.Line}  [async_handoff]"
                );
            }
        }

        // The headline: entry points per deployed service (the summary table). An EP counts in every
        // service whose process loads it (shared libraries fan out to many hosts — see the chip counts).
        if (!deployments.IsEmpty)
            WriteServiceSummary(derivedEps.Concat(origins).Select(e => (e.Kind, (string?)e.FilePath, e.Requires)), deployments, output);
        return 0;
    }

    // The two-line "custom" EP listing line (Format A) when deployment data exists; the plain
    // route + location otherwise. The kind is supplied by the caller's group header, so it's not
    // repeated on the line — only the ▶ marker, route, deployment chip, then the indented location.
    private static void WriteEntryPointLine(
        TextWriter output,
        DeploymentMap deployments,
        string route,
        string filePath,
        int line,
        IReadOnlyList<string>? requires = null
    )
    {
        if (deployments.IsEmpty)
        {
            output.WriteLine($"      {route}  {ShortenPath(filePath)}:{line}");
            return;
        }
        output.WriteLine($"      {EntryPointRenderer.Marker} {route}  {EntryPointRenderer.DeployTag(deployments, filePath, requires)}");
        output.WriteLine($"          {ShortenPath(filePath)}:{line}");
    }

    // Per-service rollup of entry points: total + per-kind breakdown, in deployments.json order.
    // An EP counts in every service it is ACTIVE-IN (loaded AND capability-gated in) — so a gated
    // actor counts only in the host(s) that `provides` its required token, not in every host that
    // merely links it. A service that LOADS an EP but is gated out of it is still listed, with a
    // `· N linked-inactive` tail (and a 0 active count when it activates none) — so the "loaded here,
    // doesn't run here" signal is visible in the rollup, not just on each EP line. EPs whose owning
    // project is in no service closure (tests/tools) fall into "(unattributed)".
    private static void WriteServiceSummary(
        IEnumerable<(string Kind, string? FilePath, IReadOnlyList<string>? Requires)> eps,
        DeploymentMap deployments,
        TextWriter output
    )
    {
        var byService = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var inactive = new Dictionary<string, int>(StringComparer.Ordinal); // loaded but gated out
        var unattributed = 0;
        foreach (var (kind, filePath, requires) in eps)
        {
            var loaded = deployments.ServicesForFile(filePath);
            if (loaded.Count == 0)
            {
                unattributed++;
                continue;
            }
            var active = deployments.ActiveServices(loaded, requires);
            foreach (var s in active)
            {
                if (!byService.TryGetValue(s, out var kinds))
                    byService[s] = kinds = new Dictionary<string, int>(StringComparer.Ordinal);
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                totals[s] = totals.GetValueOrDefault(s) + 1;
            }
            // Services that link the EP's code but are gated out of activating it.
            foreach (var s in loaded)
                if (!active.Contains(s))
                    inactive[s] = inactive.GetValueOrDefault(s) + 1;
        }

        output.WriteLine();
        output.WriteLine("Entry points per deployed service (active-in; `· N linked-inactive` = loaded but gated out of that host):");
        foreach (var svc in deployments.Services)
        {
            var total = totals.GetValueOrDefault(svc.Name);
            var inactiveCount = inactive.GetValueOrDefault(svc.Name);
            if (total == 0 && inactiveCount == 0)
                continue;
            var breakdown =
                total == 0
                    ? ""
                    : string.Join(
                        " ",
                        byService[svc.Name]
                            .OrderByDescending(k => k.Value)
                            .ThenBy(k => k.Key, StringComparer.Ordinal)
                            .Select(k => $"{k.Key}={k.Value}")
                    );
            var inactiveTail = inactiveCount > 0 ? $"   · {inactiveCount} linked-inactive" : "";
            var label = svc.Kind is null ? svc.Name : $"{svc.Name} ({svc.Kind})";
            output.WriteLine($"  {label, -46} {total, 6}   {breakdown}{inactiveTail}");
        }
        if (unattributed > 0)
            output.WriteLine($"  {"(unattributed — tests/tools/no service)", -46} {unattributed, 6}");
    }

    // Phase-3 origin promotion: a CLASSIFIED handoff target becomes a first-class DerivedEntryPoint —
    // kind from the matching dispatcher (background|timer|actor|event), route = the target's FQN
    // (same shape as the L1 class-inheritance route), registration site as file/line. Deduped against
    // the L1-rule EPs by route, so a `Process()` override that is BOTH an L1 EP and a handoff target
    // is not double-counted. Deduped among handoffs by route too (one origin per callback).
    private static IReadOnlyList<DerivedEntryPoint> PromoteHandoffOrigins(
        IReadOnlyList<HandoffEntryPoint> classifiedHandoffs,
        IReadOnlyList<DerivedEntryPoint> existingEntryPoints
    )
    {
        var existingRoutes = new HashSet<string>(existingEntryPoints.Select(e => e.Route), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DerivedEntryPoint>();
        foreach (var h in classifiedHandoffs)
        {
            var route = HandoffTargetRoute(h.Target);
            if (route is null || existingRoutes.Contains(route) || !seen.Add(route))
                continue;
            var kind = h.Kind ?? "background";
            var method = kind.ToUpperInvariant();
            result.Add(new DerivedEntryPoint(kind, method, route, $"{kind} {method} {route}", h.FilePath, h.Line, h.Requires));
        }
        return result;
    }

    // "M:Ns.Type.Method(args)" -> "Ns.Type.Method" (strip M:, params, generic arity) — the same route
    // shape FactEntryPointDeriver builds for class-inheritance EPs, so dedup-by-route lines up.
    private static string? HandoffTargetRoute(string targetDocId)
    {
        if (!targetDocId.StartsWith("M:", StringComparison.Ordinal))
            return null;
        var body = targetDocId.Substring(2);
        var paren = body.IndexOf('(');
        if (paren >= 0)
            body = body.Substring(0, paren);
        var sb = new System.Text.StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '`')
            {
                i++;
                while (i < body.Length && char.IsDigit(body[i]))
                    i++;
                i--;
                continue;
            }
            sb.Append(body[i]);
        }
        return sb.ToString();
    }

    // rig dead [--rules <path>...] [--lib] [--include-dispatch] [--all] [--limit <n>] [--format tsv]
    // Unreachable-symbol / dead-code finder over the fact graph (closes the deferred Task #7). Roots =
    // the derived entry points (pages/actions/background/wcf) + delegate/method-group handoffs + every
    // Main + every test method ([Fact]/[Theory]/[Test]). A first-party method NOT reachable from any
    // root (forward, incl. dispatch) is a candidate. REPORT ONLY — confirm against the C# compiler
    // (IDE0051/CS0169) or a human before removing; static facts miss reflection/DI/serialization.
    //   --lib              treat public/protected members as roots (library API surface); default off
    //                      (application mode) so unused public methods ARE flagged.
    //   --include-dispatch also flag unreached override/virtual members (dispatch targets); default off.
    //   --all              include Low-confidence (public/protected) candidates; default = High+Medium.
    //   --root <pattern>   add every method whose SymbolId contains <pattern> to the root set
    //                      (repeatable). For entry points facts can't see — e.g. a top-level-statement
    //                      Program.Main (synthesized, no DocID) or a host that invokes by reflection.
    private static async Task<int> RunDeadAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        var limit = int.TryParse(GetOption(args, "--limit"), out var l) ? l : 80;
        var libMode = args.Contains("--include-lib") || args.Contains("--lib"); // --lib: deprecated alias
        var includeDispatch = args.Contains("--include-dispatch");
        var showAll = args.Contains("--all");
        var tsv = string.Equals(GetOption(args, "--format"), "tsv", StringComparison.OrdinalIgnoreCase);
        var extraRules = new List<string>();
        var rootPatterns = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--rules" && i + 1 < args.Length)
            {
                extraRules.Add(Path.GetFullPath(args[i + 1]));
                i++;
            }
            else if (args[i] == "--root" && i + 1 < args.Length)
            {
                rootPatterns.Add(args[i + 1]);
                i++;
            }
        }

        await using var context = OpenReadContext(workingDirectory);

        // TODO(perf): `dead` still loads the full ~1.4M-row call graph into memory (LoadFactGraphAsync)
        // and runs ReachableFromAll(roots) in process. This is the last read command doing a full-graph
        // load. It maps directly onto the SQL primitive: reachable = SqlReachability.ReachableSetAsync(
        // roots, Forward); dead = methods − reachable − roots. Left as-is intentionally — `dead` is a
        // cold/occasional audit path, not a hot query, so the in-memory load is acceptable for now.
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var graph = await Reads.LoadFactGraphAsync(context, handoffRules);
        var methods = await Reads.LoadDeadCodeMethodsAsync(context);
        if (methods.Count == 0)
        {
            output.WriteLine("No method symbols in the index — run `rig index`/`rig mine` first.");
            return 1;
        }

        // --- Roots: derived entry points + handoffs + Main + test methods ---
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var classRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(workingDirectory, extraRules);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ep in FactEntryPointDeriver.Derive(epData, epRules, classRules))
            roots.Add(ep.Method);
        // RECALL RAIL: every delegate/method-group target stays a root REGARDLESS of classification —
        // both the surviving unclassified methodGroup edges AND the reclassified handoff edges. The
        // sync-cut prunes the registrar->callback edge from reach, so the callback must be a root or it
        // would be falsely flagged dead. (Constraint #1 in the handoff.)
        foreach (var edge in graph.CallEdges)
            if (edge.Kind is EdgeKinds.MethodGroup or EdgeKinds.Handoff)
                roots.Add(edge.Callee);
        // Process entry points: any method named Main.
        foreach (var m in methods)
            if (m.Name == "Main")
                roots.Add(m.SymbolId);
        // Test methods are framework-invoked roots: a ctor ref to a test attribute marks its enclosing
        // method ([Fact]/[Theory]/[Test]). Built in so `rig dead` works with no rules file.
        foreach (var cr in epData.CtorRefs)
            if (cr.Enclosing is not null && IsTestAttribute(cr.Target))
                roots.Add(cr.Enclosing);
        // User-supplied roots (--root <pattern>): every method whose SymbolId contains the pattern.
        if (rootPatterns.Count > 0)
            foreach (var m in methods)
                if (rootPatterns.Any(p => m.SymbolId.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                    roots.Add(m.SymbolId);

        var candidates = DeadCodeFinder.Find(graph, roots, methods, libMode, includeDispatch);
        var shown = candidates.Where(c => showAll || c.Tier != DeadCodeFinder.Tier.Low).ToList();

        if (tsv)
        {
            foreach (var c in shown)
                output.WriteLine($"{c.Tier}\t{c.Reason}\t{c.DirectCallers}\t{c.SymbolId}\t{c.FilePath}:{c.Line}");
            return 0;
        }

        output.WriteLine($"Roots (entry points + handoffs + Main + tests): {roots.Count}");
        output.WriteLine($"First-party methods examined: {methods.Count}");
        output.WriteLine(
            $"Dead-code candidates: {candidates.Count}  (High {candidates.Count(c => c.Tier == DeadCodeFinder.Tier.High)}, "
                + $"Medium {candidates.Count(c => c.Tier == DeadCodeFinder.Tier.Medium)}, Low {candidates.Count(c => c.Tier == DeadCodeFinder.Tier.Low)})"
        );
        output.WriteLine(libMode ? "Mode: library (public/protected = roots)" : "Mode: application (public methods are flaggable)");
        output.WriteLine("REPORT ONLY — confirm each against the C# compiler (IDE0051/CS0169) before removing.");
        if (!showAll && candidates.Any(c => c.Tier == DeadCodeFinder.Tier.Low))
            output.WriteLine("(Low-confidence public/protected candidates hidden; pass --all to include them.)");
        output.WriteLine();
        foreach (var tierGroup in shown.GroupBy(c => c.Tier).OrderBy(g => g.Key))
        {
            output.WriteLine($"=== {tierGroup.Key} confidence ({tierGroup.Count()}) ===");
            foreach (var c in tierGroup.Take(limit))
            {
                var note = c.DirectCallers == 0 ? "" : $"  [reached only by {c.DirectCallers} dead caller(s)]";
                output.WriteLine($"  {ShortName(c.SymbolId)}  {ShortenPath(c.FilePath)}:{c.Line}{note}");
            }
            if (tierGroup.Count() > limit)
                output.WriteLine($"  … and {tierGroup.Count() - limit} more (raise --limit)");
        }
        return 0;
    }

    private static bool IsTestAttribute(string targetSymbolId) =>
        targetSymbolId.IndexOf("FactAttribute", StringComparison.Ordinal) >= 0
        || targetSymbolId.IndexOf("TheoryAttribute", StringComparison.Ordinal) >= 0
        || targetSymbolId.IndexOf("TestAttribute", StringComparison.Ordinal) >= 0;

    // Formats the raw effect group for one method into display strings, applying three transforms:
    // (1) lock:acquire+release pairs on the same resource → single "🔒 lock [resource]" entry
    //     (the pair is always emitted together and adds no information individually);
    //     if the sole resource is Threading.Monitor the resource name is omitted (always the same).
    // (2) identical rendered strings → deduplicated with a "×N" suffix.
    // (3) all effects are returned as individual strings; the caller joins them inside one {…} block.
    private static List<string> FormatEffectGroup(
        IEnumerable<Rig.Domain.Data.DerivedEffect> effects,
        IReadOnlyDictionary<string, string> emoji
    )
    {
        var list = effects.ToList();

        // Collapse lock acquire+release pairs per resource.
        var acquiresByResource = list.Where(e => e.Provider == "lock" && e.Operation == "acquire")
            .GroupBy(e => e.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key ?? "", g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var releasesByResource = list.Where(e => e.Provider == "lock" && e.Operation == "release")
            .GroupBy(e => e.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key ?? "", g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var pairedResources = acquiresByResource
            .Keys.Intersect(releasesByResource.Keys, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<string>();

        // Emit one collapsed "lock" entry per paired resource.
        foreach (var resource in pairedResources.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            var lockEmoji = FactEffectEmojiProvider.For(emoji, "lock", "held");
            // Omit resource name when it is always Threading.Monitor — adds no information.
            var resourceLabel = resource.Contains("Threading.Monitor", StringComparison.OrdinalIgnoreCase) ? "" : $" {ShortName(resource)}";
            result.Add($"{lockEmoji} lock{resourceLabel}");
        }

        // Emit non-lock effects and any unpaired lock effects normally.
        foreach (var e in list)
        {
            var isPaired =
                pairedResources.Contains(e.ResourceType ?? "")
                && (e.Provider == "lock" && (e.Operation == "acquire" || e.Operation == "release"));
            if (isPaired)
                continue;
            result.Add(
                $"{FactEffectEmojiProvider.For(emoji, e.Provider, e.Operation)} {e.Provider}:{e.Operation} {ShortName(e.ResourceType)}"
            );
        }

        // Dedup: collapse identical strings to "label ×N".
        return result.GroupBy(s => s, StringComparer.Ordinal).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToList();
    }

    // `tree --full`: one effect rendered as its OWN provenance leaf body — glyph + provider:op + resource +
    // the producing call site (file:line) — instead of the compact inline {…} tag the other modes hoist
    // onto the enclosing method. The caller orders a method's leaves by source line.
    private static string FormatEffectLeaf(Rig.Domain.Data.DerivedEffect e, IReadOnlyDictionary<string, string> emoji)
    {
        var loc = string.IsNullOrEmpty(e.FilePath) ? "" : $"  {ShortenPath(e.FilePath)}:{e.Line}";
        return $"{FactEffectEmojiProvider.For(emoji, e.Provider, e.Operation)} {e.Provider}:{e.Operation} {ShortName(e.ResourceType)}{loc}";
    }

    // `tree --full`: a library call that produced NO effect (resolved to a referenced-assembly target, but
    // no rule matched it). Rendered as a dim leaf (· marker) so the call is visible without implying an
    // effect — distinct from the glyph-prefixed effect leaves above.
    private static string FormatUnresolvedLeaf(string target, string? filePath, int line)
    {
        var loc = string.IsNullOrEmpty(filePath) ? "" : $"  {ShortenPath(filePath)}:{line}";
        var name = ShortName(target);
        // ShortName keeps a leading DocID kind prefix ("M:"/"T:"/…) for a namespace-less symbol; strip it.
        if (name.Length > 2 && name[1] == ':')
            name = name[2..];
        return $"· {name}{loc}";
    }

    private static string ShortName(string? symbolId)
    {
        if (string.IsNullOrEmpty(symbolId))
            return "(top-level)";
        var s = symbolId!;
        var paren = s.IndexOf('(');
        if (paren >= 0)
            s = s.Substring(0, paren);
        var lastDot = s.LastIndexOf('.');
        var prevDot = lastDot > 0 ? s.LastIndexOf('.', lastDot - 1) : -1;
        return prevDot >= 0 ? s.Substring(prevDot + 1) : s;
    }

    // Compact parameter signature for `rig tree --signatures`, so same-named OVERLOADS (e.g. the four
    // SiteCache.New / three Site.New) are distinguishable: "(Int32)", "(SiteId, ITransaction)", "()".
    // Each parameter type is reduced to its simple name (namespace stripped); generic/array suffixes are
    // left as-is. Empty string when the DocID carries no parameter list (so a bare member stays bare).
    private static string ShortSignature(string? symbolId)
    {
        if (string.IsNullOrEmpty(symbolId))
            return "";
        var s = symbolId!;
        var open = s.IndexOf('(');
        if (open < 0)
            return "";
        var close = s.LastIndexOf(')');
        if (close <= open)
            return "";
        var inner = s.Substring(open + 1, close - open - 1);
        if (inner.Length == 0)
            return "()";

        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= inner.Length; i++)
        {
            if (i < inner.Length)
            {
                var c = inner[i];
                if (c is '{' or '[' or '(' or '<')
                    depth++;
                else if (c is '}' or ']' or ')' or '>')
                    depth--;
                if (!(c == ',' && depth == 0))
                    continue;
            }
            parts.Add(SimplifyParamType(inner.Substring(start, i - start)));
            start = i + 1;
        }
        return "(" + string.Join(", ", parts) + ")";
    }

    // Strips the namespace from EVERY type token in a parameter type (not just the outer one) and shows
    // generics in C# angle-bracket form, so the rendering is consistent at any nesting depth:
    // "System.Int32" -> "Int32", "SD.…ORMSupportClasses.ITransaction" -> "ITransaction",
    // "System.Collections.Generic.Dictionary{System.String,System.Object}" -> "Dictionary<String, Object>",
    // "System.Nullable{MedDBase.SiteId}[]" -> "Nullable<SiteId>[]". Tokenizes on dotted-identifier runs;
    // every other char (braces->angles, brackets, commas, ref/out '@', '*') is structure and preserved.
    private static string SimplifyParamType(string param)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < param.Length)
        {
            var c = param[i];
            if (c == '`')
            {
                // A type-parameter REFERENCE: ``N (method type param) or `N (containing-type param).
                // Render the positional placeholder T/U/V… (the real name isn't in the doc id).
                i++;
                if (i < param.Length && param[i] == '`')
                    i++;
                var ds = i;
                while (i < param.Length && char.IsDigit(param[i]))
                    i++;
                sb.Append(int.TryParse(param.Substring(ds, i - ds), out var idx) ? TypeParamName(idx) : "T");
                continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '.')
            {
                var start = i;
                while (i < param.Length && (char.IsLetterOrDigit(param[i]) || param[i] is '_' or '.'))
                    i++;
                var token = param.Substring(start, i - start);
                var dot = token.LastIndexOf('.');
                sb.Append(dot >= 0 ? token.Substring(dot + 1) : token);
            }
            else
            {
                // Doc-id generic args use {}; render as C# <> with ", " between args for readability.
                sb.Append(
                    c == '{' ? '<'
                    : c == '}' ? '>'
                    : c
                );
                if (c == ',')
                    sb.Append(' ');
                i++;
            }
        }
        return sb.ToString().Trim();
    }

    // Positional generic type-parameter placeholder (the real name isn't in the doc id): 0->T, 1->U,
    // 2->V, … then T7, T8 beyond the single-letter run.
    private static string TypeParamName(int index) => index is >= 0 and < 7 ? "TUVWXYZ"[index].ToString() : "T" + index;

    // Replaces XML-doc-id generic-ARITY markers in a NAME with readable placeholders, so a node reads
    // like C#: "Cache`2.GetResults" -> "Cache<T, U>.GetResults",
    // "CheckAllExternalApplications``1" -> "CheckAllExternalApplications<T>". A bare name is returned
    // unchanged. (Parameter type-param REFERENCES are handled in SimplifyParamType.)
    private static string PrettyGenericName(string name)
    {
        if (name.IndexOf('`') < 0)
            return name;
        var sb = new StringBuilder();
        var i = 0;
        while (i < name.Length)
        {
            if (name[i] == '`')
            {
                i++;
                if (i < name.Length && name[i] == '`')
                    i++;
                var ds = i;
                while (i < name.Length && char.IsDigit(name[i]))
                    i++;
                if (int.TryParse(name.Substring(ds, i - ds), out var n) && n > 0)
                    sb.Append('<').Append(string.Join(", ", Enumerable.Range(0, n).Select(TypeParamName))).Append('>');
            }
            else
            {
                sb.Append(name[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    // Loop detail (e.g. a foreach's "{ident} in {expr}") can be long/multi-line (LINQ predicates),
    // so collapse whitespace and truncate for single-line trace output.
    private static string ShortLoop(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
            return "?";
        var s = string.Join(" ", detail!.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= 60 ? s : s.Substring(0, 57) + "...";
    }

    // A resource-span hazard tag for an effect (P2b ordering/nesting): a network/IO/external effect
    // that fires while a transaction or lock is held ("transaction spans a network call" / "lock held
    // across IO"). Empty when the effect carries no span observation.
    private static string SpanTag(DerivedEffect effect)
    {
        var span = (effect.Observations ?? []).FirstOrDefault(o => o.Type is "transaction_spans_effect" or "lock_held_across_effect");
        if (span is null)
            return "";
        return span.Type == "transaction_spans_effect" ? "  ⚠ inside-open-tx" : "  ⚠ lock-held-across";
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // The flags each command accepts. Option VALUES never start with "--" for any rig command
    // (patterns, paths, ints, effect names), so any leading-"--" token that isn't listed here is a
    // genuine typo or wrong name — rejected up front rather than silently ignored. (This is what bit
    // `tree --depth 4`: --depth was unknown, dropped, and the tree rendered unbounded.)
    private static readonly Dictionary<string, string[]> KnownFlagsByCommand = new(StringComparer.Ordinal)
    {
        ["index"] = ["--rules", "--identity", "--from", "--parallelism", "--durable", "--merge", "--include-tests", "--no-tests"],
        ["mine"] = ["--from", "--rules", "--identity", "--parallelism"],
        ["runs"] = [],
        ["di"] = [],
        ["graph"] = [],
        ["profile"] = [],
        ["files"] = ["--skipped"],
        ["symbols"] = ["--kind", "--limit"],
        ["refs"] = ["--first-party", "--kind", "--limit"],
        ["path"] = ["--async", "--raw", "--rules", "--maxdepth", "--depth"],
        ["reaches"] = ["--async", "--rules", "--maxdepth", "--depth", "--format", "--raw", "--only", "--exclude"],
        ["tree"] =
        [
            "--full",
            "--summary",
            "--effects",
            "--async",
            "--raw",
            "--files",
            "--signatures",
            "--sig",
            "--rules",
            "--maxdepth",
            "--depth",
            "--only",
            "--exclude",
            "--no-cache",
            "--time",
        ],
        ["callers"] = ["--orphans", "--roots", "--entrypoints", "--async", "--raw", "--rules", "--maxdepth", "--depth"],
        ["derive"] = ["--limit", "--rules", "--only", "--exclude", "--format"],
        ["dead"] = ["--limit", "--include-lib", "--lib", "--include-dispatch", "--all", "--format", "--rules", "--root"],
    };

    // Reject the first unrecognised --flag for the command (--help/--version always allowed). Commands
    // with no registered flag set are not guarded. Returns true (and prints guidance) on a bad flag.
    private static bool HasUnknownFlag(string command, string[] args, TextWriter error)
    {
        if (!KnownFlagsByCommand.TryGetValue(command, out var known))
            return false;
        foreach (var a in args.Skip(1))
        {
            if (a.Length <= 2 || a[0] != '-' || a[1] != '-') // positional, value, or bare "--"
                continue;
            if (a is "--help" or "--version" || known.Contains(a))
                continue;
            error.WriteLine($"Unknown option '{a}' for 'rig {command}'.");
            if (known.Length > 0)
                error.WriteLine($"  Accepted: {string.Join(" ", known)}");
            error.WriteLine("  Run `rig --help` for usage.");
            return true;
        }
        return false;
    }

    // Reject more than one flag from a mutually-exclusive mode group (e.g. tree's --full/--summary/--effects).
    // Returns true (and prints guidance) when two or more are present.
    private static bool HasConflictingModes(string[] args, string command, TextWriter error, params string[] group)
    {
        var present = group.Where(args.Contains).ToList();
        if (present.Count <= 1)
            return false;
        error.WriteLine($"Options {string.Join(" and ", present)} can't be combined for 'rig {command}'.");
        error.WriteLine("  Run `rig --help` for usage.");
        return true;
    }

    // --maxdepth defaults to UNBOUNDED (int.MaxValue) — traversal runs to its natural frontier (the
    // closure, the maxNodes cap, and cycle/shared-callee dedup all still terminate it), not an arbitrary
    // hop cap. Pass --maxdepth <n> to bound it explicitly (e.g. for a shallow nearest-effects view).
    // --depth is accepted as an alias (the name most people reach for); --maxdepth wins if both given.
    private static int MaxDepthOf(string[] args) =>
        int.TryParse(GetOption(args, "--maxdepth") ?? GetOption(args, "--depth"), out var d) ? d : int.MaxValue;

    // Traversal DEFAULTS to SYNC-CUT everywhere: async handoff edges (a delegate handed to a
    // background/timer/actor/event dispatcher to run later) are NOT crossed, so a registration never
    // looks like it runs its callback and per-entry effect/reach counts are the trustworthy synchronous
    // ones. `--async` opts into walking handoffs, tagged with their dispatcher (the scheduled reach in
    // its own ⚡cross_thread bucket, never conflated with a synchronous call). sync ⊆ async.
    private static FactPathFinder.TraversalMode TraversalModeOf(string[] args) =>
        args.Contains("--async") ? FactPathFinder.TraversalMode.AsyncInclude : FactPathFinder.TraversalMode.SyncCut;

    // Effect selection for reaches/tree/derive: --only keeps just the listed effects, --exclude drops
    // them (exclude wins on overlap). Tokens match an effect's `provider` (e.g. "throw") or the precise
    // `provider:operation` (e.g. "llblgen:read"). Returns the input unchanged when neither flag is given.
    // A stable signature of the effect filters (--only/--exclude) for the render-sidecar key: sorted +
    // lowercased so token order/casing don't fragment it, empty in the common no-filter case. The seam
    // summaries in the sidecar are a function of the FILTERED effects, so two queries that differ only by
    // these flags must get distinct sidecars (the forest itself is filter-independent and is not affected).
    private static string EffectFilterSignature(string[] args)
    {
        var only = string.Join(",", ParseList(args, "--only").Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal));
        var exclude = string.Join(
            ",",
            ParseList(args, "--exclude").Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal)
        );
        return $"only={only};exclude={exclude}";
    }

    private static IReadOnlyList<DerivedEffect> ApplyEffectFilters(IReadOnlyList<DerivedEffect> effects, string[] args)
    {
        var only = ParseList(args, "--only");
        var exclude = ParseList(args, "--exclude");
        if (only.Count == 0 && exclude.Count == 0)
            return effects;
        return effects.Where(e => (only.Count == 0 || InSet(e, only)) && !InSet(e, exclude)).ToList();

        static bool InSet(DerivedEffect e, HashSet<string> set) => set.Contains(e.Provider) || set.Contains($"{e.Provider}:{e.Operation}");
    }

    // A repeatable list option whose value is split on commas OR whitespace (also ';' / tab), with
    // empties trimmed — so `--exclude throw`, `--exclude throw,llblgen:read`, `--exclude "throw cache"`,
    // and repeated `--exclude` flags all parse identically. Case-insensitive.
    private static HashSet<string> ParseList(string[] args, string name)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == name)
                foreach (
                    var token in args[i + 1]
                        .Split([',', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                )
                    set.Add(token);
        return set;
    }

    private static string ShortenPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Length <= 3 ? path : string.Join('/', parts[^3..]);
    }
}

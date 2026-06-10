using System.Reflection;
using Rig.Analysis;
using Rig.Analysis.Rules;
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
            ?? typeof(CliApplication).Assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static void WriteCommandSummary(TextWriter output)
    {
        output.WriteLine("Runtime Intelligence Graph");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  rig index <solution|project> [--rules <path>...] [--identity <id>] [--from <entry.csproj>] [--parallelism <n>]   (--from = index only the entry project's non-test closure, one workspace)");
        output.WriteLine("  rig mine <solution> --from <project.csproj> [--rules <path>...] [--identity <id>] [--parallelism <n>]");
        output.WriteLine("  rig runs");
        output.WriteLine("  rig di   (DI registrations: service -> implementation, lifetime, source)");
        output.WriteLine("  rig symbols <pattern> [--kind <k>] [--limit <n>]");
        output.WriteLine("  rig refs <pattern> [--first-party] [--kind <refkind>] [--limit <n>]");
        output.WriteLine("  rig path <fromPattern> <toPattern>");
        output.WriteLine("  rig reaches <fromPattern> [--rules <path>...] [--maxdepth <n>] [--format tsv]   (effects reachable from an entry point)");
        output.WriteLine("  rig tree <fromPattern> [--full|--summary] [--rules <path>...] [--maxdepth <n>]   (call tree from an entry point; default = paths that reach an effect)");
        output.WriteLine("  rig callers <toPattern> [--roots] [--maxdepth <n>]   (reverse reachability: who reaches this method; --roots = entry-point candidates)");
        output.WriteLine("  rig derive [--rules <path>...] [--limit <n>]   (stage-2 pass over facts: effects + handoffs)");
        output.WriteLine("  rig dead [--rules <path>...] [--lib] [--include-dispatch] [--all] [--root <pattern>...] [--format tsv]   (unreachable first-party methods; report-only, compiler-confirm before removing)");
        output.WriteLine("  rig files --skipped");
        output.WriteLine("  rig profile validate");
    }

    private static async Task<int> RunIndexAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Missing solution or project path.");
            error.WriteLine("Usage: rig index <solution|project> [--rules <path>...] [--identity <id>] [--from <entry.csproj>] [--parallelism <n>]");
            return 2;
        }

        var extraRules = new List<string>();
        string? identity = null;
        string? fromProject = null;
        int? parallelism = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--rules" && i + 1 < args.Length) { extraRules.Add(Path.GetFullPath(args[i + 1])); i++; }
            else if (args[i] == "--identity" && i + 1 < args.Length) { identity = args[i + 1]; i++; }
            else if (args[i] == "--from" && i + 1 < args.Length) { fromProject = Path.GetFullPath(args[i + 1]); i++; }
            else if (args[i] == "--parallelism" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) { parallelism = p; i++; }
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

        AnalysisResult result;
        try
        {
            output.WriteLine($"Indexing: {Path.GetFullPath(args[1])}");
            if (extraRules.Count > 0) output.WriteLine($"Rules: {string.Join(", ", extraRules)}");
            if (identity is not null) output.WriteLine($"Identity: {identity}");
            if (fromProject is not null) output.WriteLine($"From (closure): {fromProject}  ->  {scopeProjectPaths!.Count} project(s)");
            if (parallelism is not null) output.WriteLine($"Parallelism: {parallelism}");
            result = await SolutionAnalyzer.AnalyzeAsync(
                args[1],
                progress: message => output.WriteLine($"Progress: {message}"),
                extraRulesPaths: extraRules.Count > 0 ? extraRules : null,
                projectIdentity: identity,
                scopeProjectPaths: scopeProjectPaths,
                parallelism: parallelism
            );
        }
        catch (InvalidOperationException exception)
        {
            error.WriteLine("Failed to load solution/project for analysis.");
            error.WriteLine(exception.Message);
            error.WriteLine("Ensure the target solution has been restored and builds successfully, then retry.");
            error.WriteLine($"  dotnet restore {args[1]}");
            error.WriteLine($"  dotnet build {args[1]}");
            return 2;
        }

        var storeDirectory = Path.Combine(workingDirectory, ".rig");
        Directory.CreateDirectory(storeDirectory);
        await using var context = new RigDbContext(Path.Combine(storeDirectory, "rig.db"));
        await context.Database.EnsureCreatedAsync();

        output.WriteLine("Progress: Saving run");
        var runId = await Writes.SaveAsync(context, result);

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
        string solutionPath, string fromProject, string workingDirectory, TextWriter output, TextWriter error)
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
            if (!visited.Add(p)) continue;
            if (depGraph.TryGetValue(p, out var deps))
                foreach (var d in deps)
                    if (!visited.Contains(d)) queue.Enqueue(d);
        }

        // Drop test projects. Production projects don't reference them, so the closure is normally
        // test-free already; this honours --from's "without tests" contract defensively.
        var excludedTests = visited.Where(IsTestProjectPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var t in excludedTests) visited.Remove(t);

        var listPath = Path.Combine(workingDirectory, "relevant-projects.json");
        var listData = new
        {
            solutionPath = solutionFull,
            entryProject = entry,
            projectCount = visited.Count,
            excludedTestProjects = excludedTests,
            projects = visited.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        File.WriteAllText(listPath, System.Text.Json.JsonSerializer.Serialize(listData,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
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

    private static async Task<int> RunRunsAsync(TextWriter output, string workingDirectory)
    {
        var storeDirectory = Path.Combine(workingDirectory, ".rig");
        await using var context = new RigDbContext(Path.Combine(storeDirectory, "rig.db"));
        var runs = await Reads.ListRunsAsync(context);

        output.WriteLine("Runs");
        foreach (var run in runs)
        {
            output.WriteLine($"  {run.Id}");
            output.WriteLine($"    indexed={run.CreatedAtUtc:u}");
            output.WriteLine($"    solution={run.SolutionPath}");
            output.WriteLine(
                $"    symbols={run.SymbolCount} references={run.ReferenceCount} di={run.DiRegistrationCount}"
            );
        }

        return 0;
    }

    private static async Task<int> RunDiAsync(TextWriter output, TextWriter error, string workingDirectory)
    {
        await using var context = OpenContext(workingDirectory);
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

        await using var context = OpenContext(workingDirectory);
        var sourceFiles = await Reads.LoadSkippedSourceFilesAsync(context);
        if (sourceFiles is null)
        {
            return NoRunError(error);
        }

        SourceFileRenderer.RenderSkipped(sourceFiles, output);

        return 0;
    }

    private static RigDbContext OpenContext(string workingDirectory)
    {
        var storeDirectory = Path.Combine(workingDirectory, ".rig");
        return new RigDbContext(Path.Combine(storeDirectory, "rig.db"));
    }

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
            if (args[i] == "--from") { fromProject = Path.GetFullPath(args[i + 1]); i++; }
            else if (args[i] == "--rules") { extraRules.Add(Path.GetFullPath(args[i + 1])); i++; }
            else if (args[i] == "--identity") { identity = args[i + 1]; i++; }
            else if (args[i] == "--parallelism" && int.TryParse(args[i + 1], out var p)) { parallelism = p; i++; }
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

            if (batch.Count == 0) break;

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
                    var indexArgs = new[] { "index", proj }
                        .Concat(rulesArgs)
                        .Concat(new[] { "--identity", identity! })
                        .ToArray();

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
                finally { semaphore.Release(); }
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
        File.WriteAllText(indexPath, System.Text.Json.JsonSerializer.Serialize(indexData,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"[mine] Reachable projects index written: {indexPath}");

        return totalFailed > 0 ? 1 : 0;
    }

    private static string ComputeIdentity(string solutionPath) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(solutionPath))))[..16];

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

        var storeDirectory = Path.Combine(workingDirectory, ".rig");
        await using var context = new RigDbContext(Path.Combine(storeDirectory, "rig.db"));
        var hits = await Reads.SearchSymbolsAsync(context, pattern, kind, limit);

        output.WriteLine($"Symbols matching '{pattern}'{(kind is null ? "" : $" kind={kind}")}");
        foreach (var hit in hits)
            output.WriteLine($"  {hit.Kind,-8} {hit.SymbolId}  {ShortenPath(hit.FilePath)}:{hit.Line}");
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

        var storeDirectory = Path.Combine(workingDirectory, ".rig");
        await using var context = new RigDbContext(Path.Combine(storeDirectory, "rig.db"));
        var hits = await Reads.FindReferencesAsync(context, pattern, firstParty, refKind, limit);

        output.WriteLine($"References to '{pattern}'{(firstParty ? " (first-party)" : "")}{(refKind is null ? "" : $" kind={refKind}")}");
        foreach (var group in hits.GroupBy(h => h.TargetSymbolId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"  {group.Key}");
            foreach (var hit in group)
                output.WriteLine($"    {hit.RefKind,-11} {hit.EnclosingSymbolId ?? "(top-level)"}  {ShortenPath(hit.FilePath)}:{hit.Line}");
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
            error.WriteLine("Usage: rig path <fromPattern> <toPattern>");
            return 2;
        }

        var fromPattern = args[1];
        var toPattern = args[2];

        var storeDirectory = Path.Combine(workingDirectory, ".rig");
        await using var context = new RigDbContext(Path.Combine(storeDirectory, "rig.db"));
        var graph = await Reads.LoadFactGraphAsync(context);
        output.WriteLine($"Fact graph: {graph.CallEdges.Count} call edges, {graph.ImplementsEdges.Count} implements edges, {graph.Methods.Count} methods");

        var path = FactPathFinder.Find(graph, fromPattern, toPattern);
        if (path is null)
        {
            output.WriteLine($"No path from '{fromPattern}' to '{toPattern}'.");
            return 1;
        }

        output.WriteLine($"Path '{fromPattern}' -> '{toPattern}' ({path.Count} nodes):");
        for (var i = 0; i < path.Count; i++)
        {
            var step = path[i];
            var loop = step.LoopKind is null ? "" : $" | loop {step.LoopKind}: {ShortLoop(step.LoopDetail)}";
            var kind = step.Fanout > 1 ? $"{step.Kind} ×{step.Fanout} fan-out" : step.Kind;
            var via = i == 0 ? "" : $"  [{kind}{loop}{(step.FilePath is null ? "" : $" @ {ShortenPath(step.FilePath)}:{step.Line}")}]";
            output.WriteLine($"  {new string(' ', i * 2)}{step.SymbolId}{via}");
        }
        return 0;
    }

    // rig reaches <fromPattern> [--rules <path>...] [--maxdepth <n>] [--format tsv]
    // Reachability over the SAME fact call graph that powers `rig path` (incl. interface->impl
    // dispatch), intersected with the derived effects: "from this entry point, which captured
    // effects are reachable, and at what depth". Validates effect capture along real call paths.
    private static async Task<int> RunReachesAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Usage: rig reaches <fromPattern> [--rules <path>...] [--maxdepth <n>] [--format tsv]");
            return 2;
        }
        var fromPattern = args[1];
        var maxDepth = int.TryParse(GetOption(args, "--maxdepth"), out var d) ? d : 12;
        var tsv = string.Equals(GetOption(args, "--format"), "tsv", StringComparison.OrdinalIgnoreCase);
        var extraRules = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--rules" && i + 1 < args.Length) { extraRules.Add(Path.GetFullPath(args[i + 1])); i++; }

        await using var context = new RigDbContext(Path.Combine(workingDirectory, ".rig", "rig.db"));

        var graph = await Reads.LoadFactGraphAsync(context);
        var reachable = FactPathFinder.ReachesWithFanout(graph, fromPattern, maxDepth);

        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var effectRules = FactEffectRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var observationRules = FactObservationRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var effects = FactEffectDeriver.Derive(invocations, effectRules, providerFilter: null, baseEdges: epData.BaseEdges, ctorRefs: epData.CtorRefs, observationRules: observationRules, throwRefs: throwRefs);

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
                return (ri.Depth, Fanout: fanout, Loop: loopDetail, Via: ri.DispatchVia, ViaDegree: ri.DispatchDegree, Effect: e);
            })
            .OrderBy(h => h.Depth)
            .ToList();

        if (tsv)
        {
            // dispatchVia/dispatchDegree (last two cols) flag effects whose ONLY reach is a base-
            // virtual/interface dispatch fan-out from that source method — not a real call (D3/D7).
            foreach (var h in hits)
                output.WriteLine($"{h.Depth}\t{h.Effect.Provider}\t{h.Effect.Operation}\t{h.Effect.ResourceType}\t{h.Effect.EnclosingSymbolId}\t{ShortenPath(h.Effect.FilePath)}:{h.Effect.Line}\t{h.Fanout}\t{ShortLoop(h.Loop)}\t{h.Via}\t{(h.Via is null ? 0 : h.ViaDegree)}");
            return 0;
        }

        // Split real reach from dispatch fan-out: an effect tagged with a DispatchVia is reachable
        // ONLY by fanning a base-virtual/interface method out to all its overrides/impls (A1) — its
        // count is dispatch noise, not per-entry behaviour, so it's rolled up by source rather than
        // listed alongside genuinely-reached effects.
        var direct = hits.Where(h => h.Via is null).ToList();
        var fanned = hits.Where(h => h.Via is not null).ToList();

        output.WriteLine($"From: {fromPattern}");
        output.WriteLine($"Reachable methods (<= depth {maxDepth}): {reachable.Count}");
        output.WriteLine($"Direct effects (real call paths): {direct.Count}  (fanned out under a loop: {direct.Count(h => h.Fanout > 0)})");
        foreach (var g in direct.GroupBy(h => (h.Effect.Provider, h.Effect.Operation)).OrderByDescending(g => g.Count()))
            output.WriteLine($"  {g.Count(),4}  {g.Key.Provider} {g.Key.Operation}");
        output.WriteLine("--- nearest direct effects (depth  provider op  resource  <- method  [loop]) ---");
        foreach (var h in direct.Take(40))
        {
            var fan = h.Fanout > 0 ? $"  🔁x{h.Fanout} [loop: {ShortLoop(h.Loop)}]" : "";
            output.WriteLine($"  d{h.Depth}  {h.Effect.Provider} {h.Effect.Operation}  {ShortName(h.Effect.ResourceType)}  <- {ShortName(h.Effect.EnclosingSymbolId)}{fan}");
        }

        if (fanned.Count > 0)
        {
            output.WriteLine($"--- dispatch fan-out ({fanned.Count} effects; reach is base-virtual/interface dispatch, NOT a real call — see A1) ---");
            foreach (var g in fanned
                .GroupBy(h => (h.Via!, h.Effect.Provider, h.Effect.Operation))
                .OrderByDescending(g => g.Count()))
            {
                var degree = g.Max(h => h.ViaDegree);
                output.WriteLine($"  x{g.Count(),-5} {g.Key.Provider} {g.Key.Operation}  via {ShortName(g.Key.Item1)} dispatch [fan-out of {degree}]");
            }
        }
        return 0;
    }

    // rig tree <fromPattern> [--full|--summary] [--rules <path>...] [--maxdepth <n>]
    // The full first-party call TREE from an entry point over the fact graph (entrypoint-independent,
    // same edges as reaches/path — incl. interface->impl + base->override dispatch + loop context).
    // Modes mirror the legacy `callgraph`: default prunes to call paths that REACH an effect; --full
    // prints every reachable method; --summary prints just the effect-count rollup. Effects are
    // annotated inline ({provider:op resource}); looped edges get 🔁; cycle/shared-callee re-entry
    // is shown as "↺seen" (that method's subtree is printed under its first occurrence).
    private static async Task<int> RunTreeAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine("Usage: rig tree <fromPattern> [--full|--summary] [--rules <path>...] [--maxdepth <n>]");
            return 2;
        }
        var fromPattern = args[1];
        var maxDepth = int.TryParse(GetOption(args, "--maxdepth"), out var d) ? d : 20;
        var full = args.Contains("--full");
        var summary = args.Contains("--summary");
        var extraRules = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--rules" && i + 1 < args.Length) { extraRules.Add(Path.GetFullPath(args[i + 1])); i++; }

        await using var context = new RigDbContext(Path.Combine(workingDirectory, ".rig", "rig.db"));

        var graph = await Reads.LoadFactGraphAsync(context);
        var roots = FactPathFinder.BuildTree(graph, fromPattern, maxDepth);
        if (roots.Count == 0)
        {
            output.WriteLine($"No symbol matches '{fromPattern}'.");
            return 1;
        }

        // Effects per enclosing method — same derivation as `reaches` (incl. throws).
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var effectRules = FactEffectRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var observationRules = FactObservationRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var effects = FactEffectDeriver.Derive(invocations, effectRules, providerFilter: null, baseEdges: epData.BaseEdges, ctorRefs: epData.CtorRefs, observationRules: observationRules, throwRefs: throwRefs);

        var effectsByMethod = effects
            .Where(e => e.EnclosingSymbolId is not null)
            .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => $"{e.Provider}:{e.Operation} {ShortName(e.ResourceType)}").ToList(), StringComparer.Ordinal);

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
                output.WriteLine($"  {g.Count(),4}  {g.Key.Provider} {g.Key.Operation}");
            return 0;
        }

        foreach (var root in roots)
            RenderTreeNode(root, 0, effectsByMethod, prune: !full, output);
        return 0;
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

    private static void RenderTreeNode(
        TraceNode node, int depth, IReadOnlyDictionary<string, List<string>> effectsByMethod, bool prune, TextWriter output)
    {
        if (prune && !SubtreeHasEffect(node, effectsByMethod))
            return;

        var dispatch = node.EdgeKind is "impl-dispatch" or "override-dispatch"
            ? (node.Fanout > 1 ? $" «{node.EdgeKind} ×{node.Fanout} fan-out»" : $" «{node.EdgeKind}»")
            : "";
        var loop = node.LoopKind is null ? "" : $" 🔁[{ShortLoop(node.LoopDetail)}]";
        var seen = node.Truncated ? " ↺seen" : "";
        var fx = effectsByMethod.TryGetValue(node.SymbolId, out var list)
            ? "  " + string.Join(" ", list.Select(e => "{" + e + "}"))
            : "";
        output.WriteLine($"{new string(' ', depth * 2)}{ShortName(node.SymbolId)}{dispatch}{loop}{seen}{fx}");

        foreach (var c in node.Children)
            RenderTreeNode(c, depth + 1, effectsByMethod, prune, output);
    }

    // rig callers <toPattern> [--roots] [--maxdepth <n>]
    // Reverse reachability over the fact graph: every method that can reach toPattern (transitive
    // callers, incl. reverse interface/override dispatch). --roots filters to entry-point candidates
    // (reachable methods with no predecessor — framework/DI/reflection-invoked tops). Answers
    // "which entry points touch this method"; coverage is bounded by what's indexed.
    private static async Task<int> RunCallersAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine("Usage: rig callers <toPattern> [--roots] [--maxdepth <n>]");
            return 2;
        }
        var toPattern = args[1];
        var maxDepth = int.TryParse(GetOption(args, "--maxdepth"), out var d) ? d : 20;
        var rootsOnly = args.Contains("--roots");

        await using var context = new RigDbContext(Path.Combine(workingDirectory, ".rig", "rig.db"));
        var graph = await Reads.LoadFactGraphAsync(context);

        if (rootsOnly)
        {
            var roots = FactPathFinder.EntryRootsReaching(graph, toPattern, maxDepth);
            if (roots.Count == 0)
            {
                output.WriteLine($"No entry-point candidates reach '{toPattern}' (or no symbol matches).");
                return 1;
            }
            output.WriteLine($"Entry-point candidates reaching '{toPattern}': {roots.Count}");
            foreach (var r in roots)
                output.WriteLine($"  {r}");
            return 0;
        }

        var reachable = FactPathFinder.ReachedBy(graph, toPattern, maxDepth);
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
            if (args[i] == "--rules" && i + 1 < args.Length) { extraRules.Add(Path.GetFullPath(args[i + 1])); i++; }
        }
        var storeDirectory = Path.Combine(workingDirectory, ".rig");
        await using var context = new RigDbContext(Path.Combine(storeDirectory, "rig.db"));

        // Entry-point fact data is loaded up front: its base edges also feed the effect deriver's
        // base-type gates (e.g. clientpage_proxy = declaring type derives MedDBase.Pages.ProxyBase).
        var epData = await Reads.LoadFactEntryPointDataAsync(context);

        // --- Effects (data-driven over facts) ---
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var effectRules = FactEffectRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var observationRules = FactObservationRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var effects = FactEffectDeriver.Derive(invocations, effectRules, providerFilter: null, baseEdges: epData.BaseEdges, ctorRefs: epData.CtorRefs, observationRules: observationRules, throwRefs: throwRefs);

        // Machine-readable mode: emit full-fidelity rows (full DocIDs/paths) for tooling that joins
        // effects/entry points against the call graph. `rig derive --format tsv`.
        if (string.Equals(GetOption(args, "--format"), "tsv", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var e in effects)
                output.WriteLine($"effect\t{e.Provider}\t{e.Operation}\t{e.ResourceType}\t{e.EnclosingSymbolId}\t{e.FilePath}\t{e.Line}\t{string.Join(",", (e.Observations ?? []).Select(o => o.Type))}");
            var tsvEpRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
            var tsvClassRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(workingDirectory, extraRules);
            foreach (var ep in FactEntryPointDeriver.Derive(epData, tsvEpRules, tsvClassRules))
                output.WriteLine($"entrypoint\t{ep.Kind}\t{ep.Method}\t{ep.Route}\t{ep.FilePath}\t{ep.Line}");
            return 0;
        }

        output.WriteLine($"Effects re-derived from facts: {effects.Count}");
        foreach (var group in effects
            .GroupBy(e => (e.Provider, e.Operation))
            .OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  {group.Key.Provider} {group.Key.Operation}: {group.Count()}");
            foreach (var e in group.Take(limit / 8 + 1))
                output.WriteLine($"      {ShortName(e.ResourceType)}  <- {ShortName(e.EnclosingSymbolId)}  {ShortenPath(e.FilePath)}:{e.Line}");
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
        foreach (var kindGroup in derivedEps
            .GroupBy(e => e.Kind)
            .OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  {kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup.Take(limit / 4 + 1))
                output.WriteLine($"      {e.Route}  {ShortenPath(e.FilePath)}:{e.Line}");
        }

        // --- Handoff / background entry points (delegate/method-group, derived from facts) ---
        var handoffs = await Reads.DeriveHandoffEntryPointsAsync(context, limit);
        output.WriteLine();
        output.WriteLine($"Handoff entry points (delegate/method-group) derived from facts: {handoffs.Count}");
        foreach (var h in handoffs)
            output.WriteLine($"  {h.Target}\n      registered in {h.RegisteredIn}  {ShortenPath(h.FilePath)}:{h.Line}");
        return 0;
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
        var libMode = args.Contains("--lib");
        var includeDispatch = args.Contains("--include-dispatch");
        var showAll = args.Contains("--all");
        var tsv = string.Equals(GetOption(args, "--format"), "tsv", StringComparison.OrdinalIgnoreCase);
        var extraRules = new List<string>();
        var rootPatterns = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--rules" && i + 1 < args.Length) { extraRules.Add(Path.GetFullPath(args[i + 1])); i++; }
            else if (args[i] == "--root" && i + 1 < args.Length) { rootPatterns.Add(args[i + 1]); i++; }
        }

        await using var context = new RigDbContext(Path.Combine(workingDirectory, ".rig", "rig.db"));

        var graph = await Reads.LoadFactGraphAsync(context);
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
        foreach (var h in await Reads.DeriveHandoffEntryPointsAsync(context, int.MaxValue))
            roots.Add(h.Target);
        // Process entry points: any method named Main.
        foreach (var m in methods)
            if (m.Name == "Main")
                roots.Add(m.SymbolId);
        // Test methods are framework-invoked roots: a ctor ref to a test attribute marks its enclosing
        // method ([Fact]/[Theory]/[Test]). Built in so `rig dead` works with no rules file.
        foreach (var cr in epData.CtorRefs)
            if (cr.EnclosingSymbolId is not null && IsTestAttribute(cr.TargetSymbolId))
                roots.Add(cr.EnclosingSymbolId);
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
        output.WriteLine($"Dead-code candidates: {candidates.Count}  (High {candidates.Count(c => c.Tier == DeadCodeFinder.Tier.High)}, " +
            $"Medium {candidates.Count(c => c.Tier == DeadCodeFinder.Tier.Medium)}, Low {candidates.Count(c => c.Tier == DeadCodeFinder.Tier.Low)})");
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

    // Loop detail (e.g. a foreach's "{ident} in {expr}") can be long/multi-line (LINQ predicates),
    // so collapse whitespace and truncate for single-line trace output.
    private static string ShortLoop(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
            return "?";
        var s = string.Join(" ", detail!.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= 60 ? s : s.Substring(0, 57) + "...";
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string ShortenPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Length <= 3 ? path : string.Join('/', parts[^3..]);
    }
}

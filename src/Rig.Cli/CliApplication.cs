using System.Reflection;
using Rig.Analysis;
using Rig.Analysis.Rules;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
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
            "entrypoints" => await RunEntryPointsAsync(output, error, workingDirectory),
            "effects" => await RunEffectsAsync(args, output, error, workingDirectory),
            "trace" => await RunTraceAsync(args, output, error, workingDirectory),
            "callgraph" => await RunCallGraphAsync(args, output, error, workingDirectory),
            "callgraphs" => await RunCallGraphsAsync(args, output, error, workingDirectory),
            "di" => await RunDiAsync(output, error, workingDirectory),
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
        output.WriteLine("  rig index <solution|project> [--rules <path>...] [--identity <id>]");
        output.WriteLine("  rig mine <solution> --from <project.csproj> [--rules <path>...] [--identity <id>] [--parallelism <n>]");
        output.WriteLine("  rig runs");
        output.WriteLine("  rig entrypoints");
        output.WriteLine("  rig callgraph <index> [--full] [--summary]");
        output.WriteLine("  rig callgraphs [--full] [--summary]");
        output.WriteLine("  rig effects [--entrypoint <index>]");
        output.WriteLine("  rig trace <symbol> [--paths]");
        output.WriteLine("  rig trace --contains <text> [--paths]");
        output.WriteLine("  rig di");
        output.WriteLine("  rig files --skipped");
        output.WriteLine("  rig profile validate");
    }

    private static async Task<int> RunIndexAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Missing solution or project path.");
            error.WriteLine("Usage: rig index <solution|project> [--rules <path>...]");
            return 2;
        }

        var extraRules = new List<string>();
        string? identity = null;
        for (var i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--rules") { extraRules.Add(Path.GetFullPath(args[i + 1])); i++; }
            else if (args[i] == "--identity") { identity = args[i + 1]; i++; }
        }

        AnalysisResult result;
        try
        {
            output.WriteLine($"Indexing: {Path.GetFullPath(args[1])}");
            if (extraRules.Count > 0) output.WriteLine($"Rules: {string.Join(", ", extraRules)}");
            if (identity is not null) output.WriteLine($"Identity: {identity}");
            result = await SolutionAnalyzer.AnalyzeAsync(
                args[1],
                progress: message => output.WriteLine($"Progress: {message}"),
                extraRulesPaths: extraRules.Count > 0 ? extraRules : null,
                projectIdentity: identity
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
        output.WriteLine($"EntryPoints: {result.EntryPoints.Count}");
        output.WriteLine($"Effects: {result.Effects.Count}");

        return 0;
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
                $"    entrypoints={run.EntryPointCount} effects={run.EffectCount} di={run.DiRegistrationCount} methods={run.MethodObservationCount} invocations={run.InvocationObservationCount}"
            );
        }

        return 0;
    }

    private static async Task<int> RunEntryPointsAsync(TextWriter output, TextWriter error, string workingDirectory)
    {
        await using var context = OpenContext(workingDirectory);
        var entryPoints = await Reads.LoadEntryPointsAsync(context);
        if (entryPoints is null)
        {
            return NoRunError(error);
        }

        output.WriteLine("EntryPoints");
        for (var i = 0; i < entryPoints.Count; i++)
        {
            var ep = entryPoints[i];
            output.WriteLine($"  [{i, 3}] {ep.DisplayName}  {Path.GetFileName(ep.FilePath)}:{ep.Line}");
        }

        return 0;
    }

    private static async Task<int> RunEffectsAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        int? entryPointIndex = null;
        if (args.Length >= 3 && args[1] == "--entrypoint")
        {
            if (!int.TryParse(args[2], out var idx))
            {
                error.WriteLine("Invalid entrypoint index.");
                error.WriteLine("Usage: rig effects --entrypoint <index>");
                return 2;
            }
            entryPointIndex = idx;
        }

        await using var context = OpenContext(workingDirectory);

        IReadOnlyList<EffectInfo>? effects;
        if (entryPointIndex.HasValue)
        {
            effects = await Reads.LoadEffectsForEntryPointAsync(context, entryPointIndex.Value);
        }
        else
        {
            effects = await Reads.LoadEffectsAsync(context);
        }

        if (effects is null)
        {
            return NoRunError(error);
        }

        EffectRenderer.Render(effects, entryPointIndex, output);

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

    private static async Task<int> RunTraceAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        var pathsMode = args.Contains("--paths");
        string? symbol;

        if (args.Length >= 3 && args[1] == "--contains")
        {
            await using var context = OpenContext(workingDirectory);
            var matches = await Reads.FindCallGraphSymbolsAsync(context, args[2]);
            if (matches is null)
            {
                return NoRunError(error);
            }

            if (matches.Count == 0)
            {
                error.WriteLine($"No callgraph symbol matches: {args[2]}");
                return 2;
            }

            if (matches.Count > 1)
            {
                TraceRenderer.RenderAmbiguous(args[2], matches, error);
                return 2;
            }

            symbol = matches[0];
        }
        else if (args.Length >= 2 && args[1] != "--paths")
        {
            symbol = args[1];
        }
        else
        {
            error.WriteLine("Usage: rig trace <symbol> [--paths]");
            error.WriteLine("Usage: rig trace --contains <text> [--paths]");
            return 2;
        }

        await using var traceContext = OpenContext(workingDirectory);
        var trace = await Reads.LoadTraceAsync(traceContext, symbol);
        if (trace is null)
        {
            return NoRunError(error);
        }

        TraceRenderer.Render(trace, pathsMode, output);

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

    private static async Task<int> RunCallGraphAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var entryPointIndex))
        {
            error.WriteLine("Missing or invalid entrypoint index.");
            error.WriteLine("Usage: rig callgraph <index> [--full] [--summary]");
            return 2;
        }

        var fullMode = args.Contains("--full");
        var summaryMode = args.Contains("--summary");

        await using var context = OpenContext(workingDirectory);
        var runId = await GetLatestRunIdOrWriteErrorAsync(context, error);
        if (runId is null)
        {
            return 2;
        }

        var callGraph = await Reads.LoadCallGraphAsync(context, runId, entryPointIndex);

        if (callGraph is null)
        {
            error.WriteLine($"Callgraph not found: [{entryPointIndex}]");
            return 2;
        }

        CallGraphRenderer.Render(callGraph, entryPointIndex, fullMode, summaryMode, output);

        return 0;
    }

    private static async Task<int> RunCallGraphsAsync(string[] args, TextWriter output, TextWriter error, string workingDirectory)
    {
        var fullMode = args.Contains("--full");
        var summaryMode = args.Contains("--summary");

        await using var context = OpenContext(workingDirectory);
        var runId = await GetLatestRunIdOrWriteErrorAsync(context, error);
        if (runId is null)
        {
            return 2;
        }

        var entryPoints = await Reads.LoadEntryPointsAsync(context);
        if (entryPoints is null)
        {
            return NoRunError(error);
        }

        for (var i = 0; i < entryPoints.Count; i++)
        {
            var callGraph = await Reads.LoadCallGraphAsync(context, runId, i);
            if (callGraph is null)
            {
                continue;
            }

            CallGraphRenderer.Render(callGraph, i, fullMode, summaryMode, output);

            output.WriteLine();
        }

        return 0;
    }

    private static RigDbContext OpenContext(string workingDirectory)
    {
        var storeDirectory = Path.Combine(workingDirectory, ".rig");
        return new RigDbContext(Path.Combine(storeDirectory, "rig.db"));
    }

    private static async Task<string?> GetLatestRunIdOrWriteErrorAsync(RigDbContext context, TextWriter error)
    {
        var runId = await Reads.GetLatestRunIdAsync(context);
        if (runId is null)
        {
            NoRunError(error);
        }

        return runId;
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
}

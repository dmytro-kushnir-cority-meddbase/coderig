using Rig.Analysis;
using Rig.Storage;
using Rig.Storage.Queries;

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
            output.WriteLine("rig 0.0.0");
            return 0;
        }

        return args[0] switch
        {
            "index" => await RunIndexAsync(args, output, error, workingDirectory),
            "runs" => await RunRunsAsync(output, workingDirectory),
            "entrypoints" => await RunEntryPointsAsync(output, error, workingDirectory),
            "effects" => await RunEffectsAsync(args, output, error, workingDirectory),
            "callgraph" => await RunCallGraphAsync(args, output, error, workingDirectory),
            "di" => await RunDiAsync(output, error, workingDirectory),
            "files" => await RunFilesAsync(args, output, error, workingDirectory),
            "profile" => await RunProfileAsync(args, output, error, workingDirectory),
            _ => UnknownCommand(args[0], error)
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

    private static void WriteCommandSummary(TextWriter output)
    {
        output.WriteLine("Runtime Intelligence Graph");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  rig index <solution>");
        output.WriteLine("  rig runs");
        output.WriteLine("  rig entrypoints");
        output.WriteLine("  rig callgraph <index> [--focus]");
        output.WriteLine("  rig effects [--entrypoint <index>]");
        output.WriteLine("  rig di");
        output.WriteLine("  rig files --skipped");
        output.WriteLine("  rig profile validate");
    }

    private static async Task<int> RunIndexAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        string workingDirectory)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Missing solution path.");
            error.WriteLine("Usage: rig index <solution>");
            return 2;
        }

        AnalysisResult result;
        try
        {
            output.WriteLine($"Indexing: {Path.GetFullPath(args[1])}");
            result = await SolutionAnalyzer.AnalyzeAsync(
                args[1],
                progress: message => output.WriteLine($"Progress: {message}"));
        }
        catch (InvalidOperationException exception)
        {
            error.WriteLine("Failed to load solution for analysis.");
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
            output.WriteLine($"    entrypoints={run.EntryPointCount} effects={run.EffectCount} di={run.DiRegistrationCount} methods={run.MethodObservationCount} invocations={run.InvocationObservationCount}");
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
            output.WriteLine($"  [{i,3}] {ep.DisplayName}  {Path.GetFileName(ep.FilePath)}:{ep.Line}");
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

        if (entryPointIndex.HasValue)
        {
            output.WriteLine($"Effects [{entryPointIndex}]");
        }
        else
        {
            output.WriteLine("Effects");
        }

        foreach (var effect in effects
            .OrderBy(e => e.Provider, StringComparer.Ordinal)
            .ThenBy(e => e.Resource, StringComparer.Ordinal)
            .ThenBy(e => e.Operation, StringComparer.Ordinal))
        {
            var obs = string.Join(" ", effect.Observations.Select(o => $"[{o.Type}:{o.Context}]"));
            var obsStr = obs.Length > 0 ? $"  {obs}" : "";
            output.WriteLine($"  {effect.Provider} {effect.Operation}  {effect.Method}  {effect.Resource}  {Path.GetFileName(effect.FilePath)}:{effect.Line}{obsStr}");
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

        output.WriteLine("DI Registrations");
        foreach (var group in registrations
            .OrderBy(r => r.ServiceType, StringComparer.Ordinal)
            .ThenBy(r => r.Lifetime, StringComparer.Ordinal)
            .GroupBy(r => r.ServiceType, StringComparer.Ordinal))
        {
            var registrationsForService = group.ToArray();
            var collectionMarker = registrationsForService.Length > 1
                ? $" ({registrationsForService.Length} registrations)"
                : "";
            output.WriteLine($"  {group.Key}{collectionMarker}");

            foreach (var reg in registrationsForService
                .OrderBy(r => r.ImplementationType ?? "", StringComparer.Ordinal)
                .ThenBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Line))
            {
                output.WriteLine($"    -> {reg.ImplementationType ?? "(self)"}  lifetime={reg.Lifetime} kind={reg.RegistrationKind}");
                output.WriteLine($"       conf={reg.Confidence} basis={reg.Basis} reason={reg.Reason}");
                output.WriteLine($"       loc={Path.GetFileName(reg.FilePath)}:{reg.Line} evidence={reg.Evidence}");
            }
        }

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
            var _ = AnalysisRuleSet.LoadForSolution(workingDirectory);
            output.WriteLine("Profile: valid");
            return Task.FromResult(0);
        }
        catch (Exception exception)
        {
            error.WriteLine($"Profile: invalid — {exception.Message}");
            return Task.FromResult(2);
        }
    }

    private static async Task<int> RunFilesAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        string workingDirectory)
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

        output.WriteLine("Skipped Files");
        foreach (var sourceFile in sourceFiles)
        {
            output.WriteLine($"  {Path.GetFileName(sourceFile.FilePath)}");
            output.WriteLine($"    project={sourceFile.ProjectName} conf={sourceFile.Confidence} basis={sourceFile.Basis} reason={sourceFile.Reason}");
            output.WriteLine($"    path={sourceFile.FilePath}");
        }

        return 0;
    }

    private static async Task<int> RunCallGraphAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        string workingDirectory)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var entryPointIndex))
        {
            error.WriteLine("Missing or invalid entrypoint index.");
            error.WriteLine("Usage: rig callgraph <index> [--focus]");
            return 2;
        }

        var focusMode = args.Contains("--focus");

        await using var context = OpenContext(workingDirectory);
        var runId = await Reads.GetLatestRunIdAsync(context);
        if (runId is null)
        {
            return NoRunError(error);
        }

        var callGraph = await Reads.LoadCallGraphAsync(context, runId, entryPointIndex);

        if (callGraph is null)
        {
            error.WriteLine($"Callgraph not found: [{entryPointIndex}]");
            return 2;
        }

        var allNodes = callGraph.Nodes;
        IReadOnlyList<CallGraphNodeInfo> nodes;
        HashSet<string>? effectReachable = null;

        if (focusMode)
        {
            effectReachable = ComputeEffectReachable(allNodes);
            nodes = allNodes.Where(n => effectReachable.Contains(n.Symbol)).ToArray();
        }
        else
        {
            nodes = allNodes;
        }

        var focusSuffix = focusMode ? " (focused)" : "";
        var nodeCountSuffix = focusMode ? $" / {allNodes.Count} on effect paths" : "";
        output.WriteLine($"Callgraph: [{entryPointIndex}] {callGraph.EntryPoint}{focusSuffix}");
        output.WriteLine($"Nodes: {nodes.Count}{nodeCountSuffix}");

        RenderTree(nodes, effectReachable, focusMode, output);

        return 0;
    }

    private static void RenderTree(
        IReadOnlyList<CallGraphNodeInfo> nodes,
        HashSet<string>? effectReachable,
        bool focusMode,
        TextWriter output)
    {
        if (nodes.Count == 0) return;

        var symbolToNode = new Dictionary<string, CallGraphNodeInfo>();
        foreach (var node in nodes)
            symbolToNode.TryAdd(node.Symbol, node);

        var visited = new HashSet<string>();
        RenderTreeNode(nodes[0], "  ", "  ", symbolToNode, effectReachable, focusMode, visited, output);
    }

    private static void RenderTreeNode(
        CallGraphNodeInfo node,
        string linePrefix,
        string childrenPrefix,
        Dictionary<string, CallGraphNodeInfo> symbolToNode,
        HashSet<string>? effectReachable,
        bool focusMode,
        HashSet<string> visited,
        TextWriter output)
    {
        output.WriteLine($"{linePrefix}{Path.GetFileName(node.FilePath)}:{node.Line}  {node.Symbol}");

        if (!visited.Add(node.Symbol)) return;

        var calls = node.Calls
            .Where(c => !focusMode || effectReachable is null || effectReachable.Contains(c))
            .ToList();
        IReadOnlyList<BoundaryCallInfo> boundaries = focusMode ? [] : node.BoundaryCalls;
        var effects = node.Effects;

        int total = calls.Count + boundaries.Count + effects.Count;
        int idx = 0;

        foreach (var call in calls)
        {
            idx++;
            bool isLast = idx == total;
            var branch = childrenPrefix + (isLast ? "└─ " : "├─ ");
            var nextChildren = childrenPrefix + (isLast ? "   " : "│  ");

            if (symbolToNode.TryGetValue(call, out var childNode))
            {
                if (visited.Contains(call))
                    output.WriteLine($"{branch}{Path.GetFileName(childNode.FilePath)}:{childNode.Line}  {childNode.Symbol}  [^]");
                else
                    RenderTreeNode(childNode, branch, nextChildren, symbolToNode, effectReachable, focusMode, visited, output);
            }
            else
            {
                output.WriteLine($"{branch}CALL {call}");
            }
        }

        foreach (var boundary in boundaries)
        {
            idx++;
            bool isLast = idx == total;
            var branch = childrenPrefix + (isLast ? "└─ " : "├─ ");
            output.WriteLine($"{branch}BOUNDARY {boundary.Kind} {boundary.Method}");
        }

        foreach (var effect in effects)
        {
            idx++;
            bool isLast = idx == total;
            var branch = childrenPrefix + (isLast ? "└─ " : "├─ ");
            var obs = string.Join(" ", effect.Observations.Select(o => $"[{o.Type}:{o.Context}]"));
            var obsStr = obs.Length > 0 ? $"  {obs}" : "";
            output.WriteLine($"{branch}EFFECT {effect.Provider} {effect.Operation}  {effect.Method}  {effect.Resource}{obsStr}");
        }
    }

    private static HashSet<string> ComputeEffectReachable(
        IReadOnlyList<CallGraphNodeInfo> nodes)
    {
        // Build reverse edges: callee symbol → set of caller symbols
        var callers = new Dictionary<string, HashSet<string>>();
        foreach (var node in nodes)
        {
            foreach (var call in node.Calls)
            {
                if (!callers.TryGetValue(call, out var callerSet))
                {
                    callerSet = [];
                    callers[call] = callerSet;
                }
                callerSet.Add(node.Symbol);
            }
        }

        // BFS backward from effect nodes to find all ancestors
        var reachable = new HashSet<string>();
        var queue = new Queue<string>();

        foreach (var node in nodes.Where(n => n.Effects.Count > 0))
        {
            if (reachable.Add(node.Symbol))
                queue.Enqueue(node.Symbol);
        }

        while (queue.Count > 0)
        {
            var symbol = queue.Dequeue();
            if (!callers.TryGetValue(symbol, out var callerSet)) continue;
            foreach (var caller in callerSet)
            {
                if (reachable.Add(caller))
                    queue.Enqueue(caller);
            }
        }

        return reachable;
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
}

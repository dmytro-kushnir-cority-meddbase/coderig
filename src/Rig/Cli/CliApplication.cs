using Rig.Analysis;

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
            "entrypoints" => await RunEntryPointsAsync(output, error, workingDirectory),
            "effects" => await RunEffectsAsync(output, error, workingDirectory),
            "callgraph" => await RunCallGraphAsync(args, output, error, workingDirectory),
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
        output.WriteLine("  rig callgraph <entrypoint-id>");
        output.WriteLine("  rig effects --entrypoint <entrypoint-id>");
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

        var result = await SolutionAnalyzer.AnalyzeAsync(args[1]);
        await new RunStore(workingDirectory).SaveLatestAsync(result);

        output.WriteLine($"Indexed: {Path.GetFullPath(args[1])}");
        output.WriteLine($"EntryPoints: {result.EntryPoints.Count}");
        output.WriteLine($"Effects: {result.Effects.Count}");

        return 0;
    }

    private static async Task<int> RunEntryPointsAsync(TextWriter output, TextWriter error, string workingDirectory)
    {
        var result = await LoadLatestOrErrorAsync(error, workingDirectory);
        if (result is null)
        {
            return 2;
        }

        output.WriteLine("EntryPoints");
        foreach (var entryPoint in result.EntryPoints.OrderBy(entryPoint => entryPoint.DisplayName, StringComparer.Ordinal))
        {
            output.WriteLine($"  {entryPoint.DisplayName}");
            output.WriteLine($"    loc={Path.GetFileName(entryPoint.FilePath)}:{entryPoint.Line}");
        }

        return 0;
    }

    private static async Task<int> RunEffectsAsync(TextWriter output, TextWriter error, string workingDirectory)
    {
        var result = await LoadLatestOrErrorAsync(error, workingDirectory);
        if (result is null)
        {
            return 2;
        }

        output.WriteLine("Effects");
        foreach (var effect in result.Effects
            .OrderBy(effect => effect.Provider, StringComparer.Ordinal)
            .ThenBy(effect => effect.Resource, StringComparer.Ordinal)
            .ThenBy(effect => effect.Operation, StringComparer.Ordinal))
        {
            output.WriteLine($"  {effect.Provider} {effect.Operation} {effect.Resource}");
            output.WriteLine($"    method={effect.Method} conf={effect.Confidence} basis={effect.Basis} reason={effect.Reason}");
            output.WriteLine($"    loc={Path.GetFileName(effect.FilePath)}:{effect.Line}");
            foreach (var observation in effect.Observations)
            {
                output.WriteLine($"    OBS {observation.Type} ctx={observation.Context} conf={observation.Confidence} basis={observation.Basis} reason={observation.Reason}");
            }
        }

        return 0;
    }

    private static async Task<int> RunCallGraphAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        string workingDirectory)
    {
        if (args.Length < 2)
        {
            error.WriteLine("Missing entrypoint id.");
            error.WriteLine("Usage: rig callgraph <entrypoint-id>");
            return 2;
        }

        var result = await LoadLatestOrErrorAsync(error, workingDirectory);
        if (result is null)
        {
            return 2;
        }

        var entryPoint = string.Join(' ', args.Skip(1));
        var callGraph = result.CallGraphs.FirstOrDefault(graph =>
            string.Equals(graph.EntryPoint, entryPoint, StringComparison.Ordinal));

        if (callGraph is null)
        {
            error.WriteLine($"Callgraph not found: {entryPoint}");
            return 2;
        }

        output.WriteLine($"Callgraph: {callGraph.EntryPoint}");
        output.WriteLine($"Nodes: {callGraph.Nodes.Count}");

        foreach (var node in callGraph.Nodes)
        {
            output.WriteLine($"  {node.Symbol}");
            output.WriteLine($"    conf={node.Confidence} basis={node.Basis} reason={node.Reason}");
            output.WriteLine($"    loc={Path.GetFileName(node.FilePath)}:{node.Line}");

            foreach (var call in node.Calls)
            {
                output.WriteLine($"    CALL {call}");
            }

            foreach (var effect in node.Effects)
            {
                output.WriteLine($"    EFFECT {effect.Provider} {effect.Operation} {effect.Resource}");
                foreach (var observation in effect.Observations)
                {
                    output.WriteLine($"      OBS {observation.Type} ctx={observation.Context} conf={observation.Confidence} basis={observation.Basis} reason={observation.Reason}");
                }
            }
        }

        return 0;
    }

    private static async Task<AnalysisResult?> LoadLatestOrErrorAsync(TextWriter error, string workingDirectory)
    {
        var result = await new RunStore(workingDirectory).LoadLatestAsync();
        if (result is null)
        {
            error.WriteLine("No indexed run found. Run `rig index <solution>` first.");
        }

        return result;
    }

    private static int UnknownCommand(string command, TextWriter error)
    {
        error.WriteLine($"Unknown command: {command}");
        error.WriteLine("Run `rig --help` to see available commands.");
        return 2;
    }
}

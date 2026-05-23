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
            "effects" => await RunEffectsAsync(output, error, workingDirectory),
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
        output.WriteLine("  rig callgraph <entrypoint-id>");
        output.WriteLine("  rig effects --entrypoint <entrypoint-id>");
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
            result = await SolutionAnalyzer.AnalyzeAsync(args[1]);
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
        foreach (var entryPoint in entryPoints.OrderBy(ep => ep.DisplayName, StringComparer.Ordinal))
        {
            output.WriteLine($"  {entryPoint.DisplayName}");
            output.WriteLine($"    loc={Path.GetFileName(entryPoint.FilePath)}:{entryPoint.Line}");
        }

        return 0;
    }

    private static async Task<int> RunEffectsAsync(TextWriter output, TextWriter error, string workingDirectory)
    {
        await using var context = OpenContext(workingDirectory);
        var effects = await Reads.LoadEffectsAsync(context);
        if (effects is null)
        {
            return NoRunError(error);
        }

        output.WriteLine("Effects");
        foreach (var effect in effects
            .OrderBy(e => e.Provider, StringComparer.Ordinal)
            .ThenBy(e => e.Resource, StringComparer.Ordinal)
            .ThenBy(e => e.Operation, StringComparer.Ordinal))
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

    private static async Task<int> RunDiAsync(TextWriter output, TextWriter error, string workingDirectory)
    {
        await using var context = OpenContext(workingDirectory);
        var registrations = await Reads.LoadDiRegistrationsAsync(context);
        if (registrations is null)
        {
            return NoRunError(error);
        }

        output.WriteLine("DI Registrations");
        foreach (var reg in registrations
            .OrderBy(r => r.ServiceType, StringComparer.Ordinal)
            .ThenBy(r => r.Lifetime, StringComparer.Ordinal))
        {
            output.WriteLine($"  {reg.ServiceType}");
            output.WriteLine($"    impl={reg.ImplementationType ?? "(self)"} lifetime={reg.Lifetime} kind={reg.RegistrationKind}");
            output.WriteLine($"    conf={reg.Confidence} basis={reg.Basis} reason={reg.Reason}");
            output.WriteLine($"    loc={Path.GetFileName(reg.FilePath)}:{reg.Line}");
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
        if (args.Length < 2)
        {
            error.WriteLine("Missing entrypoint id.");
            error.WriteLine("Usage: rig callgraph <entrypoint-id>");
            return 2;
        }

        await using var context = OpenContext(workingDirectory);
        var runId = await Reads.GetLatestRunIdAsync(context);
        if (runId is null)
        {
            return NoRunError(error);
        }

        var entryPoint = string.Join(' ', args.Skip(1));
        var callGraph = await Reads.LoadCallGraphAsync(context, runId, entryPoint);

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

            foreach (var boundaryCall in node.BoundaryCalls)
            {
                output.WriteLine($"    BOUNDARY {boundaryCall.Kind} {boundaryCall.Method}");
                output.WriteLine($"      conf={boundaryCall.Confidence} basis={boundaryCall.Basis} reason={boundaryCall.Reason}");
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

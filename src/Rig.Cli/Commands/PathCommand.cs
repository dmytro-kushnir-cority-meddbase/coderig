using System.CommandLine;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Cli.Rules;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig path <from> <to>` — BFS the fact-derived, shaped call graph and print the first path found. Walks
// the SAME shaped graph as reaches/tree/callers so the path it reports is consistent with them; --raw
// bypasses shaping.
internal static class PathCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var from = CommonOptions.Pattern("from", "Source method pattern.");
        var to = CommonOptions.Pattern("to", "Target method pattern.");
        var async = CommonOptions.Async();
        var raw = CommonOptions.Raw();
        var rules = CommonOptions.Rules();
        var depth = CommonOptions.Depth();
        var cmd = new Command("path", "Print the first call path from one method to another.") { from, to, async, raw, rules, depth };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        pr.GetValue(from)!,
                        pr.GetValue(to)!,
                        pr.GetValue(async),
                        pr.GetValue(raw),
                        CommonOptions.RulesOf(pr.GetValue(rules)),
                        pr.GetValue(depth),
                        output,
                        workingDirectory
                    )
            )
        );
        return cmd;
    }

    private static async Task<int> RunAsync(
        string fromPattern,
        string toPattern,
        bool async,
        bool raw,
        IReadOnlyList<string> extraRules,
        int? depth,
        TextWriter output,
        string workingDirectory
    )
    {
        var mode = CommonOptions.Mode(async);
        var shaping = ShapingRuleSet.Load(workingDirectory, extraRules, raw);

        await using var context = OpenReadContext(workingDirectory);
        // Any path from a `from` node lies entirely within that node's forward closure, so the BOUNDED
        // forward subgraph (loaded on disk via the derived edge views, sized to the result) finds the
        // same first path as the full graph. Falls back to the full EF graph when `rig graph` hasn't run.
        var graph = await LoadShapedTraversalGraphAsync(
            context,
            fromPattern,
            SqlReachability.Direction.Forward,
            shaping.Handoff,
            shaping.Factory,
            shaping.Cut,
            shaping.Context
        );
        output.WriteLine(
            $"Fact graph: {graph.CallEdges.Count} call edges, {graph.ImplementsEdges.Count} implements edges, {graph.Methods.Count} methods"
        );

        var path = FactPathFinder.Find(graph, fromPattern, toPattern, maxDepth: CommonOptions.DepthOrUnbounded(depth), mode: mode);
        if (path is null)
        {
            output.WriteLine($"No path from '{fromPattern}' to '{toPattern}'.");
            return 1;
        }

        // Deployment/EP chip on the from-node (path[0]): which service(s) host this entry point.
        // Opt-in via deployments.json; no-op otherwise.
        var pathDeployments = await LoadDeploymentsAsync(context, workingDirectory);
        var pathEpContext = await BuildEpContextAsync(context, graph, workingDirectory, extraRules, shaping.Handoff, pathDeployments);

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
            output.WriteLine($"{Indent.Of(i + 1)}{step.SymbolId}{via}");
        }
        return 0;
    }
}

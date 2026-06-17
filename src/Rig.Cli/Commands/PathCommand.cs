using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
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
        var from = CommonOptions.Pattern(name: "from", description: "Source method pattern.");
        var to = CommonOptions.Pattern(name: "to", description: "Target method pattern.");
        var async = CommonOptions.Async();
        var raw = CommonOptions.Raw();
        var rules = CommonOptions.Rules();
        var depth = CommonOptions.Depth();
        var format = CommonOptions.Format();
        var store = CommonOptions.Store();
        var cmd = new Command(name: "path", description: "Print the first call path from one method to another.")
        {
            from,
            to,
            async,
            raw,
            rules,
            depth,
            format,
            store,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        fromPattern: pr.GetValue(from)!,
                        toPattern: pr.GetValue(to)!,
                        async: pr.GetValue(async),
                        raw: pr.GetValue(raw),
                        extraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                        depth: pr.GetValue(depth),
                        format: pr.GetValue(format),
                        output: output,
                        workingDirectory: workingDirectory,
                        storeRef: pr.GetValue(store)
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
        string? format,
        TextWriter output,
        string workingDirectory,
        string? storeRef
    )
    {
        var tsv = string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase);
        var mode = CommonOptions.Mode(async);
        // --raw bypasses all shaping (the exact unfiltered plumbing); else monomorphize factories + cut +
        // context-narrow, honoured symmetrically by the reverse/forward traversal.
        var rules = RuleSet.Load(workingDirectory, extraRules);
        var shaped = raw ? rules with { Factory = [], Cut = [], Context = [] } : rules;

        await using var context = OpenReadContext(workingDirectory, storeRef);
        // Any path from a `from` node lies entirely within that node's forward closure, so the BOUNDED
        // forward subgraph (loaded on disk via the derived edge views, sized to the result) finds the
        // same first path as the full graph. Falls back to the full EF graph when `rig graph` hasn't run.
        var graph = await LoadShapedTraversalGraphAsync(
            context: context,
            pattern: fromPattern,
            direction: SqlReachability.Direction.Forward,
            shaped
        );
        // Reclassify event-subscription (`+=`) method-group edges to `handoff` — mirroring reaches/tree
        // (ReachesCommand/TreeCommand do the same). The handler genuinely runs LATER via the event, not
        // synchronously at the `+=` site, so it must be sync-cut by default and only crossed under --async.
        // Without this, `path`/`callers` walked a `+=` handler as a synchronous call (the 2026-06-16
        // over-reach). `--raw` bypasses all shaping, so it is gated the same way reaches/tree gate it.
        if (!raw)
        {
            graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
        }

        if (!tsv)
        {
            output.WriteLine(
                $"Fact graph: {graph.CallEdges.Count} call edges, {graph.ImplementsEdges.Count} implements edges, {graph.Methods.Count} methods"
            );
        }

        var path = FactPathFinder.Find(graph, fromPattern, toPattern, maxDepth: CommonOptions.DepthOrUnbounded(depth), mode: mode);
        if (path is null)
        {
            if (!tsv)
            {
                output.WriteLine($"No path from '{fromPattern}' to '{toPattern}'.");
            }

            return 1;
        }

        // --format tsv: one row per step (full DocIDs + paths for tooling), no deployment chrome. Columns:
        // depth, symbolId, edgeKind, handoffVia, fanout, loopKind, loopDetail, dispatchBasis, file, line.
        if (tsv)
        {
            for (var i = 0; i < path.Count; i++)
            {
                var s = path[i];
                output.WriteLine(
                    $"{i}\t{s.SymbolId}\t{s.Kind}\t{s.HandoffVia}\t{s.Fanout}\t{s.LoopKind}\t{s.LoopDetail}\t{s.DispatchBasis}\t{s.FilePath}\t{s.Line}"
                );
            }
            return 0;
        }

        // Deployment/EP chip on the from-node (path[0]): which service(s) host this entry point.
        // Opt-in via deployments.json; no-op otherwise.
        var pathDeployments = await LoadDeploymentsAsync(context, workingDirectory);
        var pathEpContext = await BuildEpContextAsync(context, graph, workingDirectory, extraRules, rules.Handoff, pathDeployments);

        output.WriteLine($"Path '{fromPattern}' -> '{toPattern}' ({path.Count} nodes):");
        for (var i = 0; i < path.Count; i++)
        {
            var step = path[i];
            var loop = step.LoopKind is null ? "" : $" | loop {step.LoopKind}: {ShortLoop(step.LoopDetail)}";
            var kindBase = step.HandoffVia is not null ? $"⤳ handoff via {ShortName(step.HandoffVia)}" : step.Kind;
            if (step.DispatchBasis == "heuristic")
            {
                kindBase += " (heuristic)";
            }

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

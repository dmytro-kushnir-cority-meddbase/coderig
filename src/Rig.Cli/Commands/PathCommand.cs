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
        var includeDelivery = CommonOptions.IncludeDelivery();
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
            includeDelivery,
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
                        new Options(
                            FromPattern: pr.GetValue(from)!,
                            ToPattern: pr.GetValue(to)!,
                            Async: pr.GetValue(async),
                            IncludeDelivery: pr.GetValue(includeDelivery),
                            Raw: pr.GetValue(raw),
                            ExtraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                            Depth: pr.GetValue(depth),
                            Format: pr.GetValue(format)
                        ),
                        new CommandIo(new TextOutput(output, error), new WorkspaceLocation(workingDirectory, pr.GetValue(store)))
                    )
            )
        );
        return cmd;
    }

    // Bound option values for `rig path`. Raw user inputs (Format kept as the parsed string);
    // flag derivations (format -> tsv, mode) live at the top of RunAsync.
    private sealed record Options(
        string FromPattern,
        string ToPattern,
        bool Async,
        bool IncludeDelivery,
        bool Raw,
        IReadOnlyList<string> ExtraRules,
        int? Depth,
        string? Format
    );

    private static async Task<int> RunAsync(Options opts, CommandIo io)
    {
        var tsv = CommonOptions.IsTsv(opts.Format);
        var mode = CommonOptions.Mode(async: opts.Async, includeDelivery: opts.IncludeDelivery);
        // --raw bypasses all shaping (the exact unfiltered plumbing); else monomorphize factories + cut +
        // context-narrow, honoured symmetrically by the reverse/forward traversal.
        var rules = RuleSetLoader.Load(io.WorkspaceLocation.WorkingDirectory, opts.ExtraRules);
        var shaped = opts.Raw ? rules with { Factory = [], Cut = [], Context = [] } : rules;

        await using var context = await OpenReadContextGatedAsync(io.WorkspaceLocation);
        // Any path from a `from` node lies entirely within that node's forward closure, so the BOUNDED
        // forward subgraph (loaded on disk via the derived edge views, sized to the result) finds the
        // same first path as the full graph. Falls back to the full EF graph when `rig graph` hasn't run.
        var graph = await LoadShapedTraversalGraphAsync(
            context: context,
            pattern: opts.FromPattern,
            direction: SqlReachability.Direction.Forward,
            shaped
        );
        // Reclassify event-subscription (`+=`) method-group edges to `handoff` — mirroring reaches/tree
        // (ReachesCommand/TreeCommand do the same). The handler genuinely runs LATER via the event, not
        // synchronously at the `+=` site, so it must be sync-cut by default and only crossed under --async.
        // Without this, `path`/`callers` walked a `+=` handler as a synchronous call (the 2026-06-16
        // over-reach). `--raw` bypasses all shaping, so it is gated the same way reaches/tree gate it.
        if (!opts.Raw)
        {
            graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
        }

        if (!tsv)
        {
            io.TextOutput.Output.WriteLine(
                $"Fact graph: {graph.CallEdges.Count} call edges, {graph.ImplementsEdges.Count} implements edges, {graph.Methods.Count} methods"
            );
        }

        var path = FactPathFinder.Find(
            graph,
            fromPattern: opts.FromPattern,
            toPattern: opts.ToPattern,
            maxDepth: CommonOptions.DepthOrUnbounded(opts.Depth),
            mode: mode
        );
        // Phase 3 display-collapse: fold any monomorphized (`~mono`) step ids back to their base method id
        // before render (no-op until Phase 2's Materialize is wired into the load path).
        path = path is null ? null : MonomorphCollapse.CollapsePath(path);
        if (path is null)
        {
            if (!tsv)
            {
                io.TextOutput.Output.WriteLine($"No path from '{opts.FromPattern}' to '{opts.ToPattern}'.");
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
                io.TextOutput.Output.WriteLine(
                    $"{i}\t{s.SymbolId}\t{s.Kind}\t{s.HandoffVia}\t{s.Fanout}\t{s.LoopKind}\t{s.LoopDetail}\t{s.DispatchBasis}\t{s.FilePath}\t{s.Line}"
                );
            }
            return 0;
        }

        // Deployment/EP chip on the from-node (path[0]): which service(s) host this entry point.
        // Opt-in via deployments.json; no-op otherwise.
        var pathDeployments = await LoadDeploymentsAsync(context, io.WorkspaceLocation.WorkingDirectory);
        var pathEpContext = await BuildEpContextAsync(context, graph, io.WorkspaceLocation.WorkingDirectory, opts.ExtraRules, rules, pathDeployments);

        io.TextOutput.Output.WriteLine($"Path '{opts.FromPattern}' -> '{opts.ToPattern}' ({path.Count} nodes):");
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
            io.TextOutput.Output.WriteLine($"{Indent.Of(i + 1)}{step.SymbolId}{via}");
        }
        return 0;
    }
}

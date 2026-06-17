using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.EntryPointListRenderer;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig reaches <from>` — reachability over the shaped fact graph intersected with the derived effects:
// "from this entry point, which captured effects are reachable, and at what depth". Validates effect
// capture along real call paths. Three buckets: direct, scheduled (cross-thread via handoff), dispatch-fanout.
internal static class ReachesCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var from = CommonOptions.Pattern(name: "from", description: "Entry-point method pattern.");
        var async = CommonOptions.Async();
        var raw = CommonOptions.Raw();
        var rules = CommonOptions.Rules();
        var depth = CommonOptions.Depth();
        var format = CommonOptions.Format();
        var only = CommonOptions.Only();
        var exclude = CommonOptions.Exclude();
        var limit = CommonOptions.Limit();
        var store = CommonOptions.Store();
        var cmd = new Command(name: "reaches", description: "Effects reachable from an entry point, by depth.")
        {
            from,
            async,
            raw,
            rules,
            depth,
            format,
            only,
            exclude,
            limit,
            store,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        fromPattern: pr.GetValue(from)!,
                        async: pr.GetValue(async),
                        raw: pr.GetValue(raw),
                        extraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                        depth: pr.GetValue(depth),
                        format: pr.GetValue(format),
                        only: CommonOptions.FilterSet(pr.GetValue(only)),
                        exclude: CommonOptions.FilterSet(pr.GetValue(exclude)),
                        limit: pr.GetValue(limit),
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
        bool async,
        bool raw,
        IReadOnlyList<string> extraRules,
        int? depth,
        string? format,
        HashSet<string> only,
        HashSet<string> exclude,
        int? limit,
        TextWriter output,
        string workingDirectory,
        string? storeRef
    )
    {
        var maxDepth = CommonOptions.DepthOrUnbounded(depth);
        var max = limit ?? int.MaxValue; // --limit absent => unbounded
        var tsv = string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase);
        var mode = CommonOptions.Mode(async);

        var rules = RuleSet.Load(workingDirectory, extraRules);
        // --raw zeroes cut/context; reaches keeps Factory (it monomorphizes generic factories even under
        // --raw, a long-standing asymmetry vs path/tree/callers).
        var shaped = raw ? rules with { Cut = [], Context = [] } : rules;

        await using var context = OpenReadContext(workingDirectory, storeRef);

        var inputs = await LoadEffectReachInputsAsync(context, fromPattern, SqlReachability.Direction.Forward, shaped);
        var graph = inputs.Graph;
        if (!raw)
        {
            graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
        }

        var reachable = FactPathFinder.ReachesWithFanout(graph, fromPattern, maxDepth, mode: mode);

        var effects = DeriveEffects(
            rules.Effects,
            rules.Observations,
            inputs.Invocations,
            BaseEdgeTuples(graph),
            ctorRefs: inputs.CtorRefs,
            throwRefs: inputs.ThrowRefs
        );
        effects = ApplyEffectFilters(effects, only, exclude); // --only / --exclude (e.g. --exclude throw)

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
            foreach (var h in hits.Take(max))
            {
                output.WriteLine(
                    $"{h.Depth}\t{h.Effect.Provider}\t{h.Effect.Operation}\t{h.Effect.ResourceType}\t{h.Effect.EnclosingSymbolId}\t{ShortenPath(h.Effect.FilePath)}:{h.Effect.Line}\t{h.Fanout}\t{ShortLoop(h.Loop)}\t{h.Via}\t{(h.Via is null ? 0 : h.ViaDegree)}\t{h.HandoffVia}\t{h.Basis}"
                );
            }

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
        var reachDeployments = await LoadDeploymentsAsync(context, workingDirectory);
        var reachEpContext = await BuildEpContextAsync(context, graph, workingDirectory, extraRules, rules, reachDeployments);
        var reachFromRoot = reachable.Where(kv => kv.Value.Depth == 0).Select(kv => kv.Key).FirstOrDefault();
        output.WriteLine(
            $"From: {fromPattern}{(mode == FactPathFinder.TraversalMode.AsyncInclude ? "  (--async: handoffs included)" : "")}"
                + (reachFromRoot is null ? "" : HeaderSuffix(reachEpContext, reachFromRoot))
        );
        output.WriteLine($"Reachable methods (<= depth {maxDepth}): {reachable.Count}");
        output.WriteLine($"Direct effects (real call paths): {direct.Count}  (fanned out under a loop: {direct.Count(h => h.Fanout > 0)})");
        foreach (var g in direct.GroupBy(h => (h.Effect.Provider, h.Effect.Operation)).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{g.Count(), 4}  {g.Key.Provider} {g.Key.Operation}");
        }

        output.WriteLine("--- nearest direct effects (depth  provider op  resource  <- method  [loop]) ---");
        foreach (var h in direct.Take(max))
        {
            var fan = h.Fanout > 0 ? $"  🔁x{h.Fanout} [loop: {ShortLoop(h.Loop)}]" : "";
            var heuristic = h.Basis == "heuristic" ? "  ~heuristic" : "";
            output.WriteLine(
                $"{Indent.L1}d{h.Depth}  {h.Effect.Provider} {h.Effect.Operation}  {ShortName(h.Effect.ResourceType)}  <- {ShortName(h.Effect.EnclosingSymbolId)}{fan}{SpanTag(h.Effect)}{heuristic}"
            );
        }
        // Default is unbounded; only a `--limit` smaller than the result truncates — say so, so a grep over
        // this listing isn't a silent false negative.
        if (direct.Count > max)
        {
            output.WriteLine($"{Indent.L1}… +{direct.Count - max} more direct effect(s) (raise --limit, or --format tsv for all)");
        }

        if (scheduled.Count > 0)
        {
            output.WriteLine(
                $"--- async (scheduled) effects ({scheduled.Count}; reached across a handoff boundary — ⚡cross_thread, NOT synchronous) ---"
            );
            foreach (
                var g in scheduled.GroupBy(h => (h.HandoffVia!, h.Effect.Provider, h.Effect.Operation)).OrderByDescending(g => g.Count())
            )
            {
                output.WriteLine(
                    $"{Indent.L1}⚡x{g.Count(), -4} {g.Key.Provider} {g.Key.Operation}  ⤳ via {ShortName(g.Key.Item1)} [cross_thread]"
                );
            }
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
                    $"{Indent.L1}x{g.Count(), -5} {g.Key.Provider} {g.Key.Operation}  via {ShortName(g.Key.Item1)} dispatch [fan-out of {degree}]{heuristic}"
                );
            }
        }
        return 0;
    }
}

using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.Graph.TraversalGraphLoader;

namespace Rig.Cli.Services;

// The reusable path-finding computation, lifted out of PathCommand.RunAsync so BOTH the CLI and the
// in-process web host (Web/) find the SAME concrete call path — no shelling out, no re-parsing text. Mirrors
// TreeQueryService's split (cold compute here, CLI-only render chrome — deployment/EP chips, TSV, --time —
// stays in PathCommand). Reuses FactPathFinder.Find (the domain BFS `rig path` runs) verbatim, so a path
// found here is IDENTICAL to `rig path`'s, hop for hop.
//
// Deliberately public + primitives-in (workingDirectory/storeRef, not the internal WorkspaceLocation) so the
// contract survives a later lift to a standalone Rig.Web project, matching TreeQueryService's convention.
public static class PathQueryService
{
    // The found path (Steps, in order — empty when Matched is false) plus a best-effort per-method effect
    // annotation and ambiguity disclosure. Effects are derived over the SAME bounded forward closure the
    // path search already loaded (LoadEffectReachInputsAsync rides along for free), keyed by
    // DerivedEffect.EnclosingSymbolId so a consumer can look up "what does this hop's method do" the same
    // way TreeMapper keys effects to TraceNode.SymbolId. EffectEmoji is the repo's provider:operation ->
    // glyph map, carried through so a renderer matches `rig tree`'s glyphs without re-loading the rule set.
    //
    // FromMatches/ToMatches mirror the CLI's AmbiguityNotice: the distinct symbols each pattern resolved to,
    // within the FROM-node's forward slice (the same graph the search ran over). Count <= 1 = unambiguous.
    // NOTE (mirrors PathCommand's comment on the same lookup): because `graph` is the from-side forward
    // slice, a `to` target that lies outside it is simply absent from ToMatches — that's fine for
    // disclosure, since an unreachable target could never have been the picked answer anyway.
    public sealed record PathQueryResult(
        bool Matched,
        IReadOnlyList<PathStep> Steps,
        IReadOnlyDictionary<string, IReadOnlyList<DerivedEffect>> EffectsBySymbol,
        IReadOnlyDictionary<string, string> EffectEmoji,
        IReadOnlyList<string> FromMatches,
        IReadOnlyList<string> ToMatches
    );

    // Find the first concrete call path from `fromPattern` to `toPattern` over the store at
    // `workingDirectory` (optionally a specific `storeRef` commit/id). Mirrors the cold path of
    // PathCommand.RunAsync: same shaping rules, same event-subscription handoff marking, same
    // FactPathFinder.Find + MonomorphCollapse.CollapsePath — so the web finds exactly what `rig path` would
    // (minus the CLI-only render chrome: TSV/pretty rendering, deployment/EP header chip, --time).
    public static async Task<PathQueryResult> BuildAsync(
        string workingDirectory,
        string fromPattern,
        string toPattern,
        string? storeRef = null,
        int? depth = null,
        bool async = false,
        bool includeDelivery = false,
        bool raw = false,
        IReadOnlyList<string>? extraRules = null
    )
    {
        var rules = RuleSetLoader.Load(workingDirectory: workingDirectory, extraRules: extraRules ?? [], loadedPaths: out _);
        // --raw parity: zero the graph-shaping rules so the search runs over the exact unfiltered graph.
        var shaped = raw ? rules with { Factory = [], Cut = [], Context = [] } : rules;

        await using var context = await OpenReadContextGatedAsync(
            new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: storeRef)
        );

        // Any path from a `from` node lies entirely within that node's forward closure, so the bounded
        // forward subgraph (loaded via the derived edge views, sized to the result) finds the same first
        // path as the full graph — same reasoning PathCommand documents for LoadShapedTraversalGraphAsync.
        // Going through LoadEffectReachInputsAsync (rather than the narrower LoadShapedTraversalGraphAsync)
        // gets the SAME shaped graph PLUS the bounded invocation/ctor/throw refs the effects-if-handy
        // annotation needs, in one load — no second graph fetch.
        var inputs = await LoadEffectReachInputsAsync(context, fromPattern, SqlReachability.Direction.Forward, shaped);
        var graph = inputs.Graph;

        // Reclassify event-subscription (`+=`) method-group edges to `handoff` — mirrors PathCommand (and
        // reaches/tree): the handler runs LATER via the event, not synchronously at the `+=` site, so it
        // must be sync-cut by default and only crossed under --async. Skipped under --raw, same as the CLI.
        if (!raw)
        {
            graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
        }

        var mode = CommonOptions.Mode(async: async, includeDelivery: includeDelivery);
        var path = FactPathFinder.Find(
            graph,
            fromPattern: fromPattern,
            toPattern: toPattern,
            maxDepth: CommonOptions.DepthOrUnbounded(depth),
            mode: mode
        );
        // Phase 3 display-collapse: fold any monomorphized (`~mono`) step ids back to their base method id,
        // identical to PathCommand's post-processing.
        path = path is null ? null : MonomorphCollapse.CollapsePath(path);

        var symbolIds = graph.Methods.Select(m => m.SymbolId);
        var fromMatches = FactPathFinder.DistinctMatchTargets(symbolIds, fromPattern);
        var toMatches = FactPathFinder.DistinctMatchTargets(symbolIds, toPattern);

        if (path is null)
        {
            return new PathQueryResult(
                Matched: false,
                Steps: [],
                EffectsBySymbol: new Dictionary<string, IReadOnlyList<DerivedEffect>>(StringComparer.Ordinal),
                EffectEmoji: rules.EffectEmoji,
                FromMatches: fromMatches,
                ToMatches: toMatches
            );
        }

        // Effects-if-handy: derive the SAME effect set `tree`/`reaches` would over this closure (no extra
        // load — inputs.Invocations/CtorRefs/ThrowRefs are already bounded to it), then key by enclosing
        // method so a consumer can annotate each path step without a second query.
        var effects = DeriveEffects(
            effectRules: rules.Effects,
            observationRules: rules.Observations,
            invocations: inputs.Invocations,
            baseEdges: BaseEdgeTuples(graph),
            ctorRefs: inputs.CtorRefs,
            throwRefs: inputs.ThrowRefs,
            allocationFacts: inputs.AllocationFacts
        );
        var effectsBySymbol = effects
            .Where(e => e.EnclosingSymbolId is not null)
            .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, IReadOnlyList<DerivedEffect> (g) => g.ToList(), StringComparer.Ordinal);

        return new PathQueryResult(
            Matched: true,
            Steps: path,
            EffectsBySymbol: effectsBySymbol,
            EffectEmoji: rules.EffectEmoji,
            FromMatches: fromMatches,
            ToMatches: toMatches
        );
    }
}

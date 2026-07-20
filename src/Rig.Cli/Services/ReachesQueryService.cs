using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.Graph.TraversalGraphLoader;

namespace Rig.Cli.Services;

// The reusable REACHABLE-EFFECTS computation, lifted out of ReachesCommand.RunAsync so BOTH the CLI and the
// in-process web host (Web/) run the SAME engine — no shelling out, no re-parsing text. Unlike the CLI (which
// splits hits into three buckets — direct/scheduled(async-handoff)/dispatch-fanout — for its bucketed text
// render), this produces a FLAT effect inventory: every effect whose enclosing method is reachable from the
// pattern, folded together and aggregated by (provider, operation) with a site count and the repo's glyph.
// That's the "which effects does this reach" summary a caller wants first; the CLI's finer bucketing (sync
// vs cross-thread vs dispatch-fanout provenance) is left for a later, richer endpoint if the SPA needs it.
//
// Deliberately public + primitives-in (workingDirectory/storeRef, not the internal WorkspaceLocation) so the
// contract survives a later lift to a standalone Rig.Web project — mirrors TreeQueryService.
public static class ReachesQueryService
{
    // One aggregated (provider, operation) bucket over every effect reachable from the pattern — sync,
    // async-handoff, and dispatch-fanout reaches folded together (see the type-level note on why this is
    // flat rather than the CLI's three buckets). Sites = distinct call sites (matches `rig reaches` semantics
    // — sites in code, not runtime executions).
    public sealed record EffectSummary(string Provider, string Operation, string Glyph, int Sites);

    public sealed record ReachesQueryResult(
        string FromPattern,
        bool Matched,
        // Count of distinct methods reachable from the pattern (<= depth, unbounded here — the flat
        // inventory doesn't expose a depth knob; see the type-level note).
        int ReachableCount,
        IReadOnlyList<EffectSummary> Effects
    );

    // Build the flat effect inventory for `fromPattern` over the store at `workingDirectory` (optionally a
    // specific `storeRef` commit/id). Mirrors the cold path of ReachesCommand.RunAsync: same shaping rules,
    // same event-subscription handoff marking, same ReachesWithFanout + monomorph collapse, same DeriveEffects
    // inputs — so the web reports the same reachable-effect set `rig reaches` would compute (minus the CLI's
    // depth-ordered, bucketed render, and minus the CLI's --raw/--depth/--only/--exclude/--include-delivery
    // knobs, which this first cut of the endpoint does not expose).
    public static async Task<ReachesQueryResult> BuildAsync(
        string workingDirectory,
        string fromPattern,
        string? storeRef = null,
        bool async = false
    )
    {
        var rules = RuleSetLoader.Load(workingDirectory: workingDirectory, extraRules: [], loadedPaths: out _);

        await using var context = await OpenReadContextGatedAsync(
            new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: storeRef)
        );

        var inputs = await LoadEffectReachInputsAsync(context, fromPattern, SqlReachability.Direction.Forward, rules);
        // Event subscriptions (`someEvent += Handler`) are deferred handlers, not synchronous calls — mark
        // them as handoffs so a non-async reach doesn't count the handler as directly reachable (mirrors
        // ReachesCommand/TreeQueryService; this endpoint doesn't expose a --raw opt-out yet).
        var graph = FactPathFinder.MarkEventSubscriptionHandoffs(inputs.Graph, await Reads.EventSubscriptionSitesAsync(context));

        var mode = CommonOptions.Mode(async: async);
        var reachable = MonomorphCollapse.CollapseReachInfo(
            FactPathFinder.ReachesWithFanout(graph, fromPattern, CommonOptions.DepthOrUnbounded(null), mode: mode)
        );

        var effects = DeriveEffects(
            rules.Effects,
            rules.Observations,
            inputs.Invocations,
            BaseEdgeTuples(graph),
            ctorRefs: inputs.CtorRefs,
            throwRefs: inputs.ThrowRefs,
            allocationFacts: inputs.AllocationFacts
        );

        // Effects whose enclosing method is reachable from the entry point — same join ReachesCommand does
        // (reachable.ContainsKey(e.EnclosingSymbolId)) — then folded into one (provider, operation) inventory
        // regardless of which of the CLI's three buckets they'd land in.
        var summaries = effects
            .Where(e => e.EnclosingSymbolId is not null && reachable.ContainsKey(e.EnclosingSymbolId))
            .GroupBy(e => (e.Provider, e.Operation))
            .Select(g => new EffectSummary(
                Provider: g.Key.Provider,
                Operation: g.Key.Operation,
                Glyph: EmojiLookup.For(rules.EffectEmoji, provider: g.Key.Provider, operation: g.Key.Operation),
                Sites: g.Count()
            ))
            .OrderByDescending(s => s.Sites)
            .ThenBy(s => s.Provider, StringComparer.Ordinal)
            .ThenBy(s => s.Operation, StringComparer.Ordinal)
            .ToList();

        return new ReachesQueryResult(
            FromPattern: fromPattern,
            Matched: reachable.Count > 0,
            ReachableCount: reachable.Count,
            Effects: summaries
        );
    }
}

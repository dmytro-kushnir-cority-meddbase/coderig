using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.Graph.TraversalGraphLoader;

namespace Rig.Cli.Services;

// The reusable call-TREE computation, lifted out of TreeCommand.RunAsync so BOTH the CLI and the in-process
// web host (Web/) run the SAME engine — no shelling out, no re-parsing text. Produces the three things a
// consumer needs to render a tree: the TraceNode forest, the effects keyed to enclosing method, and a
// symbol-id -> file:line map. This is the COLD compute (rules load -> graph load -> BuildTree -> derive
// effects); the CLI's query-cache is a presentation-layer optimization that stays in TreeCommand for now.
//
// Deliberately public + primitives-in (workingDirectory/storeRef, not the internal WorkspaceLocation) so the
// contract survives a later lift to a standalone Rig.Web project. Return types are public Rig.Domain records.
public static class TreeQueryService
{
    public sealed record SymbolLocation(string? File, int Line);

    public sealed record TreeQueryResult(
        IReadOnlyList<TraceNode> Roots,
        IReadOnlyList<DerivedEffect> Effects,
        IReadOnlyDictionary<string, SymbolLocation> Locations,
        // The repo's provider:operation -> glyph map (from rig.effect-emoji.json / builtins), carried through
        // so a renderer shows the SAME glyphs as `rig tree` without re-loading the rule set.
        IReadOnlyDictionary<string, string> EffectEmoji,
        // Opaque/collapse render rules so the web mapper folds seams the same way the pretty/llm renderers do.
        // Empty under raw=true (the endpoint's ?raw= opt-out) — the tree is then served fully unfolded.
        FactRenderRules Render
    );

    // The richer result of the shared cold compute (ComputeAsync): the forest + effects PLUS the graph and
    // entry-point data the CLI needs downstream. Internal — the web only consumes the public TreeQueryResult
    // projection; TreeCommand consumes this directly to keep its existing render pipeline unchanged.
    internal sealed record TreeComputation(
        IReadOnlyList<TraceNode> Roots,
        IReadOnlyList<DerivedEffect> Effects,
        FactGraphData Graph,
        FactEntryPointDeriver.FactEntryPointData? EpData
    );

    // Build the forest + effects for `fromPattern` over the store at `workingDirectory` (optionally a specific
    // `storeRef` commit/id). Mirrors the cold path of TreeCommand.RunAsync: same shaping rules, same event-
    // subscription handoff marking, same BuildTree + monomorph collapse, same DeriveEffects inputs — so the
    // web renders exactly what `rig tree` would (minus the CLI-only render chrome).
    public static async Task<TreeQueryResult> BuildAsync(
        string workingDirectory,
        string fromPattern,
        string? storeRef = null,
        int? depth = null,
        bool async = false,
        bool includeDelivery = false,
        bool raw = false,
        IReadOnlyList<string>? extraRules = null
    )
    {
        var rules = RuleSetLoader.Load(workingDirectory: workingDirectory, extraRules: extraRules ?? [], loadedPaths: out _);
        // --raw parity: zero the graph-shaping rules so the tree is the exact unfiltered structure.
        var shaped = raw ? rules with { Factory = [], Cut = [], Context = [] } : rules;

        await using var context = await OpenReadContextGatedAsync(
            new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: storeRef)
        );

        var computation = await ComputeAsync(
            context: context,
            rules: rules,
            shaped: shaped,
            fromPattern: fromPattern,
            maxDepth: CommonOptions.DepthOrUnbounded(depth),
            maxNodes: FactPathFinder.DefaultTreeNodeBudget,
            mode: CommonOptions.Mode(async: async, includeDelivery: includeDelivery),
            raw: raw
        );

        var locations = computation
            .Graph.Methods.GroupBy(m => m.SymbolId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new SymbolLocation(g.First().FilePath, g.First().Line), StringComparer.Ordinal);

        // raw parity: no fold rules either, so the web serves the exact unfolded tree (mirrors CLI --raw).
        var renderRules = raw ? FactRenderRules.Empty : rules.Render;
        return new TreeQueryResult(computation.Roots, computation.Effects, locations, rules.EffectEmoji, renderRules);
    }

    // The COLD tree computation shared by the web host (BuildAsync, above) and `rig tree`'s cold path
    // (TreeCommand). It operates on an ALREADY-OPEN context + ALREADY-LOADED/SHAPED rules, so a caller that
    // holds those — TreeCommand does, plus its query-cache — reuses them instead of re-opening/re-loading.
    // Returns the graph + entry-point data too (not just roots/effects): the CLI threads those into its
    // downstream render stages (locations, seam, EP-site chips, --full library calls). This is the single
    // source of truth for the forest + effects, so `rig tree` and `/api/tree` cannot diverge.
    internal static async Task<TreeComputation> ComputeAsync(
        RigDbContext context,
        RuleSet rules,
        RuleSet shaped,
        string fromPattern,
        int maxDepth,
        int maxNodes,
        FactPathFinder.TraversalMode mode,
        bool raw
    )
    {
        var inputs = await LoadEffectReachInputsAsync(context, fromPattern, SqlReachability.Direction.Forward, shaped);
        var graph = inputs.Graph;
        // Event subscriptions (`someEvent += Handler`) are deferred handlers, not synchronous calls — mark them
        // as handoffs so the sync tree doesn't expand the handler as if the registrar ran it. Skipped under --raw.
        if (!raw)
        {
            graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
        }

        var roots = MonomorphCollapse.CollapseTree(FactPathFinder.BuildTree(graph, fromPattern, maxDepth, maxNodes: maxNodes, mode: mode));

        IReadOnlyList<DerivedEffect> effects =
            roots.Count == 0
                ? []
                : DeriveEffects(
                    effectRules: rules.Effects,
                    observationRules: rules.Observations,
                    invocations: inputs.Invocations,
                    baseEdges: BaseEdgeTuples(graph),
                    ctorRefs: inputs.CtorRefs,
                    throwRefs: inputs.ThrowRefs
                );

        return new TreeComputation(roots, effects, graph, inputs.EpData);
    }
}

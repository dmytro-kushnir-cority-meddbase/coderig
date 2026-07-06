using Rig.Analysis.Rules;
using Rig.Cli.Rendering;
using Rig.Cli.Services;
using Rig.Domain.Data;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Web;

// Maps a TreeQueryService result (TraceNode forest + effects + locations + emoji map) into the flat JSON DTO
// the SPA consumes. Pure projection — no engine logic. Effects are aggregated per enclosing method into
// distinct provider:operation with a call-site count and the repo's glyph, mirroring how `rig tree` annotates.
//
// Folds opaque/collapse seams the same way the pretty and llm renderers do (this consumer previously walked
// every child regardless, so the SPA received the full unfolded tree). A folded node is emitted as a labelled
// leaf with empty Children; a COLLAPSE node also carries the union of the effects it hides + the hidden count,
// so the SPA can render the seam summary without the subtree. Pass FactRenderRules.Empty (the ?raw= opt-out)
// to disable folding and get the exact unfolded tree.
internal static class TreeMapper
{
    public static TreeResponseDto ToResponse(
        string from,
        IReadOnlyList<TraceNode> roots,
        IReadOnlyList<DerivedEffect> effects,
        IReadOnlyDictionary<string, TreeQueryService.SymbolLocation> locations,
        IReadOnlyDictionary<string, string> emoji,
        FactRenderRules renderRules
    )
    {
        var effectsByMethod = BuildEffectIndex(effects, emoji);
        // Raw "provider:operation" multiset per enclosing method — the substrate for a collapsed seam's
        // hidden-effect union (same keying the llm renderer uses).
        var rawByMethod = effects
            .Where(e => e.EnclosingSymbolId is not null)
            .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
            .ToDictionary(
                keySelector: g => g.Key,
                elementSelector: g => g.Select(e => $"{e.Provider}:{e.Operation}").ToList(),
                comparer: StringComparer.Ordinal
            );

        var dtoRoots = roots
            .Select(r => MapNode(r, effectsByMethod, rawByMethod, locations, emoji, renderRules, isRoot: true))
            .ToList();
        return new TreeResponseDto(From: from, Matched: dtoRoots.Count > 0, Roots: dtoRoots);
    }

    private static TreeNodeDto MapNode(
        TraceNode node,
        IReadOnlyDictionary<string, IReadOnlyList<EffectDto>> effectsByMethod,
        IReadOnlyDictionary<string, List<string>> rawByMethod,
        IReadOnlyDictionary<string, TreeQueryService.SymbolLocation> locations,
        IReadOnlyDictionary<string, string> emoji,
        FactRenderRules renderRules,
        bool isRoot
    )
    {
        var loc = locations.GetValueOrDefault(node.SymbolId);
        var ownEffects = effectsByMethod.GetValueOrDefault(node.SymbolId, []);

        // Opaque/collapse fold: draw a labelled leaf, hide the subtree. Roots never fold (mirrors the renderers).
        var fold = TreeFoldSupport.Decide(renderRules, node.SymbolId, isRoot: isRoot);
        if (fold.Kind != TreeFoldSupport.FoldKind.None)
        {
            var isCollapse = fold.Kind == TreeFoldSupport.FoldKind.Collapse;
            var hidden = 0;
            var seamEffects = ownEffects;
            if (isCollapse)
            {
                // Union of what the folded branch touches: this node's own raw effects + the subtree's, so the
                // seam leaf reports its reach (e.g. one "llblgen:fetch ×N" chip) without shipping the subtree.
                var (subtreeRaw, hiddenCount) = TreeFoldSupport.SummarizeHidden(node.Children, rawByMethod);
                hidden = hiddenCount;
                var union = new List<string>();
                if (rawByMethod.TryGetValue(node.SymbolId, out var own))
                {
                    union.AddRange(own);
                }

                union.AddRange(subtreeRaw);
                seamEffects = AggregateRaw(union, emoji);
            }

            return ToDto(
                node,
                loc,
                effects: seamEffects,
                children: [],
                foldKind: isCollapse ? "collapse" : "opaque",
                foldLabel: fold.Label,
                foldHidden: hidden
            );
        }

        return ToDto(
            node,
            loc,
            effects: ownEffects,
            children: node
                .Children.Select(c => MapNode(c, effectsByMethod, rawByMethod, locations, emoji, renderRules, isRoot: false))
                .ToList(),
            foldKind: null,
            foldLabel: null,
            foldHidden: 0
        );
    }

    private static TreeNodeDto ToDto(
        TraceNode node,
        TreeQueryService.SymbolLocation? loc,
        IReadOnlyList<EffectDto> effects,
        IReadOnlyList<TreeNodeDto> children,
        string? foldKind,
        string? foldLabel,
        int foldHidden
    ) =>
        new(
            Id: node.SymbolId,
            Name: ShortName(node.SymbolId),
            Signature: ShortSignature(node.SymbolId),
            // Full (untruncated) predicate — the UI ellipsises on render, keeping full text in a tooltip.
            Guards: Rig.Cli.Rendering.TreeRenderer.ShortGuards(
                encoded: node.EnclosingGuards,
                loopDetail: node.LoopDetail,
                maxLength: int.MaxValue
            ),
            EdgeKind: node.EdgeKind,
            Fanout: node.Fanout,
            CallSites: node.CallSites,
            Truncated: node.Truncated,
            TruncationCause: node.TruncationCause == Rig.Domain.Data.TruncationCause.None ? null : node.TruncationCause.ToString(),
            DispatchBasis: node.DispatchBasis,
            File: loc?.File,
            Line: loc?.Line ?? 0,
            Effects: effects,
            Children: children,
            FoldKind: foldKind,
            FoldLabel: foldLabel,
            FoldHidden: foldHidden
        );

    // Aggregate a raw "provider:operation" multiset into distinct EffectDto with site counts + glyph — the same
    // shape BuildEffectIndex produces for a method's own effects, so the SPA renders a seam's union identically.
    private static IReadOnlyList<EffectDto> AggregateRaw(IReadOnlyList<string> rawProviderOps, IReadOnlyDictionary<string, string> emoji)
    {
        var counts = new Dictionary<(string Provider, string Operation), int>();
        var order = new List<(string Provider, string Operation)>();
        foreach (var raw in rawProviderOps)
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = (raw[..colon], raw[(colon + 1)..]);
            if (counts.TryGetValue(key, out var n))
            {
                counts[key] = n + 1;
            }
            else
            {
                counts[key] = 1;
                order.Add(key);
            }
        }

        return order
            .OrderByDescending(k => counts[k])
            .ThenBy(k => k.Provider, StringComparer.Ordinal)
            .ThenBy(k => k.Operation, StringComparer.Ordinal)
            .Select(k => new EffectDto(
                Provider: k.Provider,
                Operation: k.Operation,
                Glyph: EmojiLookup.For(emoji, provider: k.Provider, operation: k.Operation),
                Sites: counts[k]
            ))
            .ToList();
    }

    // enclosing method DocID -> its distinct (provider, operation) effects with site counts + glyph.
    private static IReadOnlyDictionary<string, IReadOnlyList<EffectDto>> BuildEffectIndex(
        IReadOnlyList<DerivedEffect> effects,
        IReadOnlyDictionary<string, string> emoji
    ) =>
        effects
            .Where(e => e.EnclosingSymbolId is not null)
            .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
            .ToDictionary(
                keySelector: g => g.Key,
                elementSelector: IReadOnlyList<EffectDto> (g) =>
                    g.GroupBy(e => (e.Provider, e.Operation))
                        .Select(pg => new EffectDto(
                            Provider: pg.Key.Provider,
                            Operation: pg.Key.Operation,
                            Glyph: EmojiLookup.For(emoji, provider: pg.Key.Provider, operation: pg.Key.Operation),
                            Sites: pg.Count()
                        ))
                        .OrderBy(e => e.Provider, StringComparer.Ordinal)
                        .ThenBy(e => e.Operation, StringComparer.Ordinal)
                        .ToList(),
                comparer: StringComparer.Ordinal
            );
}

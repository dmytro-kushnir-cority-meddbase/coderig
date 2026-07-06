using Rig.Analysis.Rules;
using Rig.Cli.Services;
using Rig.Domain.Data;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Web;

// Maps a TreeQueryService result (TraceNode forest + effects + locations + emoji map) into the flat JSON DTO
// the SPA consumes. Pure projection — no engine logic. Effects are aggregated per enclosing method into
// distinct provider:operation with a call-site count and the repo's glyph, mirroring how `rig tree` annotates.
internal static class TreeMapper
{
    public static TreeResponseDto ToResponse(
        string from,
        IReadOnlyList<TraceNode> roots,
        IReadOnlyList<DerivedEffect> effects,
        IReadOnlyDictionary<string, TreeQueryService.SymbolLocation> locations,
        IReadOnlyDictionary<string, string> emoji
    )
    {
        var effectsByMethod = BuildEffectIndex(effects, emoji);
        var dtoRoots = roots.Select(r => MapNode(r, effectsByMethod, locations)).ToList();
        return new TreeResponseDto(From: from, Matched: dtoRoots.Count > 0, Roots: dtoRoots);
    }

    private static TreeNodeDto MapNode(
        TraceNode node,
        IReadOnlyDictionary<string, IReadOnlyList<EffectDto>> effectsByMethod,
        IReadOnlyDictionary<string, TreeQueryService.SymbolLocation> locations
    )
    {
        var loc = locations.GetValueOrDefault(node.SymbolId);
        return new TreeNodeDto(
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
            Effects: effectsByMethod.GetValueOrDefault(node.SymbolId, []),
            Children: node.Children.Select(c => MapNode(c, effectsByMethod, locations)).ToList()
        );
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

using Rig.Domain.Data;

namespace Rig.Cli.Rendering;

// Shared fold decision + subtree effect-union for the NON-pretty tree consumers (the llm/llm-ids TSV renderer
// and the web DTO mapper). The pretty TreeRenderer already honours the opaque/collapse render rules inline; the
// llm and web consumers historically did NOT (they walked every child regardless), so a model asking for the
// LLM format — or the SPA — got the FULL unfolded tree while the human box view got the folded spine. This
// helper centralises the fold decision so all three consumers agree on WHICH nodes fold; each consumer still
// controls its own prune/opt-out (`--raw` on the CLI zeroes the rules; `?raw=true` on the web endpoint).
//
// The pretty renderer computes the collapse effect-union from the realistic reach CLOSURE (ComputeSeamEffects,
// which needs the graph). These consumers use a self-contained SUBTREE walk instead — no graph dependency, so
// the fold works wherever a TraceNode forest is in hand. The subtree union is a subset of the closure union
// (it misses effects reachable only past an already-truncated edge), which is an acceptable, clearly-scoped
// approximation for a "what does this folded branch touch" summary.
internal static class TreeFoldSupport
{
    internal enum FoldKind
    {
        None,
        Opaque,
        Collapse,
    }

    internal readonly record struct Fold(FoldKind Kind, string Label);

    // Decide whether a node folds under the render rules. Roots never fold (mirrors TreeRenderer's isRoot
    // guard). Opaque takes precedence over collapse — the same order TreeRenderer uses (it returns after the
    // opaque leaf before ever reaching the seam check).
    internal static Fold Decide(FactRenderRules rules, string symbolId, bool isRoot)
    {
        if (isRoot)
        {
            return new Fold(FoldKind.None, "");
        }

        if (rules.MatchOpaque(symbolId) is { } opaque)
        {
            return new Fold(FoldKind.Opaque, opaque.Label);
        }

        if (rules.MatchCollapseSeam(symbolId) is { } seam)
        {
            return new Fold(FoldKind.Collapse, seam.Label);
        }

        return new Fold(FoldKind.None, "");
    }

    // The MULTISET of raw "provider:operation" effect strings across a node's subtree (the part a collapse
    // fold hides), in first-encounter DFS order, plus the count of hidden nodes. Multiset (not deduped) so the
    // caller's count-aggregation renders "llblgen:fetch*14" the way the pretty seam summary shows "×14".
    // Respects Truncated: an already-cut edge is not crossed (mirrors the renderer's subtree walk).
    internal static (List<string> Effects, int Hidden) SummarizeHidden(
        IReadOnlyList<TraceNode> children,
        IReadOnlyDictionary<string, List<string>> rawEffectsByMethod
    )
    {
        var acc = new List<string>();
        var hidden = 0;

        void Walk(TraceNode node)
        {
            hidden++;
            if (rawEffectsByMethod.TryGetValue(node.SymbolId, out var effs))
            {
                acc.AddRange(effs);
            }

            if (node.Truncated)
            {
                return;
            }

            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }

        foreach (var child in children)
        {
            Walk(child);
        }

        return (acc, hidden);
    }
}

using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Phase 3 of static monomorphization (docs/design-dispatch-precision.md): the DISPLAY-COLLAPSE layer.
// Phase 2's GenericMonomorphizer SPLITS each reachable generic instantiation into a DISTINCT node id
// (`{baseMethodId}~mono⟨…⟩`) so the traversal narrows the type-param receiver to a concrete override. But
// users think in ONE source method, and — load-bearingly — `reaches`/`tree` join effects via
// `reachable.ContainsKey(e.EnclosingSymbolId)`, where the effect's `EnclosingSymbolId` is the BASE method id
// (effects derive on the base, not the split node). A split reachable set holds only the `~mono` id, so a
// base-keyed effect would NEVER join. These folds rewrite each split id back to its base (via
// MonomorphizedNodeId.BaseOf) AFTER the traversal and BEFORE the effect-join / render — restoring the join,
// deduping fragmented instantiations into one base entry, and giving users the single source method they think in.
//
// GUARDED NO-OP: Phase 2's Materialize is not yet wired into the load path (Phase 4), so NO `~mono` id exists
// in any real traversal result today. Every fold short-circuits — returning its INPUT unchanged (reference-
// equal) — when no key/SymbolId carries the `~mono` marker, so real CLI output stays byte-identical until
// Phase 4 flips Materialize on. Mirrors GenericMonomorphizer's pure-function, immutable-record house style.
public static class MonomorphCollapse
{
    // Reach-with-fanout map (the load-bearing one — its key set is the effect-join key set in `reaches`).
    // Rewrite each key via BaseOf; when several `~mono` keys (or a `~mono` key and its already-present base)
    // collapse to the same base id, UNION them keeping the entry with the MINIMUM Depth (shallowest reach
    // wins — matches BFS shortest-depth semantics; on equal depth the first-seen entry is kept deterministically).
    public static IReadOnlyDictionary<string, FactPathFinder.ReachInfo> CollapseReachInfo(
        IReadOnlyDictionary<string, FactPathFinder.ReachInfo> reachable
    )
    {
        if (!ContainsMono(reachable.Keys))
        {
            return reachable;
        }

        var collapsed = new Dictionary<string, FactPathFinder.ReachInfo>(StringComparer.Ordinal);
        foreach (var kv in reachable)
        {
            var baseId = MonomorphizedNodeId.BaseOf(kv.Key);
            if (!collapsed.TryGetValue(baseId, out var existing) || kv.Value.Depth < existing.Depth)
            {
                collapsed[baseId] = kv.Value;
            }
        }

        return collapsed;
    }

    // Depth map (serves both `Reaches` forward and `ReachedBy` reverse — key = node id, value = depth).
    // Same key fold; keep the MIN depth on collision.
    public static IReadOnlyDictionary<string, int> CollapseDepthMap(IReadOnlyDictionary<string, int> map)
    {
        if (!ContainsMono(map.Keys))
        {
            return map;
        }

        var collapsed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in map)
        {
            var baseId = MonomorphizedNodeId.BaseOf(kv.Key);
            if (!collapsed.TryGetValue(baseId, out var existing) || kv.Value < existing)
            {
                collapsed[baseId] = kv.Value;
            }
        }

        return collapsed;
    }

    // Path step list (`path`): relabel each step's SymbolId via BaseOf; keep order and every other field.
    // No dedupe — a path is an ordered walk, each step is distinct by position.
    public static IReadOnlyList<PathStep> CollapsePath(IReadOnlyList<PathStep> path)
    {
        var anyMono = false;
        for (var i = 0; i < path.Count; i++)
        {
            if (MonomorphizedNodeId.IsMonomorphized(path[i].SymbolId))
            {
                anyMono = true;
                break;
            }
        }

        if (!anyMono)
        {
            return path;
        }

        var collapsed = new List<PathStep>(path.Count);
        foreach (var step in path)
        {
            collapsed.Add(step with { SymbolId = MonomorphizedNodeId.BaseOf(step.SymbolId) });
        }

        return collapsed;
    }

    // Trace forest (`tree`): recursively relabel each node's SymbolId via BaseOf, recursing into Children.
    // Do NOT merge sibling instantiation subtrees (different instantiations are genuinely different precise
    // subtrees) and do NOT touch the binding fields (DeclaringTypeArgBinding/MethodTypeArgBinding) — those
    // preserve the `SaveServices<BillingRule>` render label after the id collapses to base. A monomorphized
    // node is a real method the user cares about, so it is relabelled, never suppressed (unlike lambdas).
    public static IReadOnlyList<TraceNode> CollapseTree(IReadOnlyList<TraceNode> forest)
    {
        if (!ForestContainsMono(forest))
        {
            return forest;
        }

        var collapsed = new List<TraceNode>(forest.Count);
        foreach (var node in forest)
        {
            collapsed.Add(CollapseNode(node));
        }

        return collapsed;
    }

    private static TraceNode CollapseNode(TraceNode node) =>
        node with
        {
            SymbolId = MonomorphizedNodeId.BaseOf(node.SymbolId),
            Children = CollapseChildren(node.Children),
        };

    private static IReadOnlyList<TraceNode> CollapseChildren(IReadOnlyList<TraceNode> children)
    {
        if (children.Count == 0)
        {
            return children;
        }

        var collapsed = new List<TraceNode>(children.Count);
        foreach (var child in children)
        {
            collapsed.Add(CollapseNode(child));
        }

        return collapsed;
    }

    // Cheap scan: true if any id carries the `~mono` marker. Short-circuits the no-op guard.
    private static bool ContainsMono(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (MonomorphizedNodeId.IsMonomorphized(id))
            {
                return true;
            }
        }

        return false;
    }

    // Recursive `~mono` scan over a forest — the no-op guard for CollapseTree (a `~mono` node can sit at any depth).
    private static bool ForestContainsMono(IReadOnlyList<TraceNode> forest)
    {
        foreach (var node in forest)
        {
            if (MonomorphizedNodeId.IsMonomorphized(node.SymbolId) || ForestContainsMono(node.Children))
            {
                return true;
            }
        }

        return false;
    }
}

using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Generic effect-set diff. Given two entry points A and B, computes each one's set of forward-reachable
// effect RESOURCE KEYS (optionally filtered to a provider:operation set) and reports the SYMMETRIC
// DIFFERENCE — every resource one reaches that the other doesn't. Purely mechanical: it has no opinion
// about what the diff MEANS. "write-set divergence" (UI-save vs import-save writing different tables) is
// one USAGE — run it with Filter = the write effects; the operator/agent supplies the interpretation.
//
// Architecture:
//   - Pure: no I/O, input not mutated, deterministic (de-dup + stable sort by (Label, ResourceKey, Side)).
//   - Spec-driven: the caller resolves entry-point PATTERNS to exact node DocIDs and passes resolved
//     pairs; the deriver does NOT do substring matching.
//   - An EP's effect-set = the normalized resource keys of every effect that (a) matches the Filter
//     (EMPTY filter = match ALL effects) and (b) has its EnclosingSymbolId in that EP's forward reach.

// One resolved (A id, B id) pair, plus an optional display Label for output grouping (no semantics).
public sealed record EffectSetDiffPair(string Label, string AId, string BId);

public sealed record EffectSetDiffSpec(
    IReadOnlyList<EffectSetDiffPair> Pairs,
    // Effects that count, matched against DerivedEffect.Provider + Operation. EMPTY = match every effect.
    IReadOnlyList<EffectPredicate> Filter,
    // Normalize the effect's ResourceType to a comparable resource key (simple-type-name + optional
    // suffix strip), so e.g. PersonEntity/PersonEntityCollection/PersonDAO collapse to one logical key.
    NormalizeSpec Normalize,
    int MaxDepth = 20,
    FactPathFinder.TraversalMode Mode = FactPathFinder.TraversalMode.SyncCut
);

// Which side of the pair holds a resource the other lacks. AOnly = in A's effect-set, not B's; BOnly = reverse.
public enum EffectDiffSide
{
    AOnly,
    BOnly,
}

// One diverging resource. PresentEpId = the EP whose effect-set CONTAINS it; AbsentEpId = the one that lacks it.
public sealed record EffectSetDiffFinding(
    string Label,
    string ResourceKey,
    EffectDiffSide Direction,
    string PresentEpId,
    string AbsentEpId
);

public static class FactEffectSetDiffDeriver
{
    // Returns every table in the symmetric write-set difference across the declared pairs. Determinism:
    // de-duped, ordered stably by (Label ordinal, ResourceKey ordinal, Direction).
    public static IReadOnlyList<EffectSetDiffFinding> Derive(
        FactGraphData graph,
        IReadOnlyList<DerivedEffect> effects,
        EffectSetDiffSpec spec
    )
    {
        if (spec.Pairs.Count == 0)
        {
            return [];
        }
        // An empty Filter = match every effect (see MatchesFilter) — the generic "diff ALL effects" default.

        // 1. Collect all distinct EP ids that need a forward reach.
        var distinctEpIds = new List<string>();
        var epIdSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in spec.Pairs)
        {
            if (epIdSet.Add(pair.AId))
            {
                distinctEpIds.Add(pair.AId);
            }
            if (epIdSet.Add(pair.BId))
            {
                distinctEpIds.Add(pair.BId);
            }
        }

        // 2. Batch all distinct EP ids in ONE ReachesFromEachSeed call (mirrors FactCorrelationDeriver step 4).
        var reachSets = FactPathFinder.ReachesFromEachSeed(
            graph: graph,
            seedIds: distinctEpIds,
            maxDepth: spec.MaxDepth,
            maxNodes: 20000,
            narrowDispatch: true,
            mode: spec.Mode
        );
        var reachOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (var i = 0; i < distinctEpIds.Count; i++)
        {
            reachOf[distinctEpIds[i]] = reachSets[i];
        }

        // 3. Build a lookup: enclosing-id -> set of normalized write-resource keys for that method.
        //    This is the per-method write-key set; an EP's write-set is the UNION over its reach set.
        var writeKeysByEnclosing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var e in effects)
        {
            if (e.EnclosingSymbolId is null)
            {
                continue;
            }

            if (!MatchesFilter(e, spec.Filter))
            {
                continue;
            }

            var key = ResourceKey.Of(e.ResourceType, spec.Normalize);
            if (key is null)
            {
                continue;
            }

            if (!writeKeysByEnclosing.TryGetValue(e.EnclosingSymbolId, out var keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                writeKeysByEnclosing[e.EnclosingSymbolId] = keys;
            }

            keys.Add(key);
        }

        // 4. For each pair compute write-sets and emit symmetric-difference findings.
        var findings = new List<EffectSetDiffFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in spec.Pairs)
        {
            var primaryWrites = CollectWriteSet(
                reachSet: reachOf.TryGetValue(pair.AId, out var pr) ? pr : [],
                writeKeysByEnclosing: writeKeysByEnclosing
            );
            var secondaryWrites = CollectWriteSet(
                reachSet: reachOf.TryGetValue(pair.BId, out var sr) ? sr : [],
                writeKeysByEnclosing: writeKeysByEnclosing
            );

            // primary \ secondary (tables the primary writes that secondary doesn't).
            foreach (var key in primaryWrites)
            {
                if (!secondaryWrites.Contains(key))
                {
                    AddFinding(
                        findings: findings,
                        seen: seen,
                        entityLabel: pair.Label,
                        resourceKey: key,
                        direction: EffectDiffSide.AOnly,
                        presentEpId: pair.AId,
                        absentEpId: pair.BId
                    );
                }
            }

            // secondary \ primary (tables the secondary writes that primary doesn't).
            foreach (var key in secondaryWrites)
            {
                if (!primaryWrites.Contains(key))
                {
                    AddFinding(
                        findings: findings,
                        seen: seen,
                        entityLabel: pair.Label,
                        resourceKey: key,
                        direction: EffectDiffSide.BOnly,
                        presentEpId: pair.BId,
                        absentEpId: pair.AId
                    );
                }
            }
        }

        // 5. Determinism: stable sort by (Label ordinal, ResourceKey ordinal, Direction).
        findings.Sort(
            (a, b) =>
            {
                var byEntity = string.CompareOrdinal(a.Label, b.Label);
                if (byEntity != 0)
                {
                    return byEntity;
                }

                var byKey = string.CompareOrdinal(a.ResourceKey, b.ResourceKey);
                if (byKey != 0)
                {
                    return byKey;
                }

                return a.Direction.CompareTo(b.Direction);
            }
        );

        return findings;
    }

    // Compute the write-key set for an EP: the UNION of all normalized write keys for every method node
    // in the EP's reach set (which includes the seed itself).
    private static HashSet<string> CollectWriteSet(
        IReadOnlyCollection<string> reachSet,
        Dictionary<string, HashSet<string>> writeKeysByEnclosing
    )
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in reachSet)
        {
            if (writeKeysByEnclosing.TryGetValue(node, out var keys))
            {
                result.UnionWith(keys);
            }
        }

        return result;
    }

    private static bool MatchesFilter(DerivedEffect e, IReadOnlyList<EffectPredicate> predicates)
    {
        if (predicates.Count == 0)
        {
            return true; // empty filter = match every effect
        }

        foreach (var p in predicates)
        {
            if (
                string.Equals(e.Provider, p.Provider, StringComparison.Ordinal)
                && (p.Operation is null || string.Equals(e.Operation, p.Operation, StringComparison.Ordinal))
            )
            {
                return true;
            }
        }

        return false;
    }

    private static void AddFinding(
        List<EffectSetDiffFinding> findings,
        HashSet<string> seen,
        string entityLabel,
        string resourceKey,
        EffectDiffSide direction,
        string presentEpId,
        string absentEpId
    )
    {
        // De-dup key: entity + resource key + direction + absent EP (the primary signal is the gap on the absent side).
        var dedupeKey = entityLabel + "\0" + resourceKey + "\0" + (int)direction + "\0" + absentEpId;
        if (seen.Add(dedupeKey))
        {
            findings.Add(
                new EffectSetDiffFinding(
                    Label: entityLabel,
                    ResourceKey: resourceKey,
                    Direction: direction,
                    PresentEpId: presentEpId,
                    AbsentEpId: absentEpId
                )
            );
        }
    }
}

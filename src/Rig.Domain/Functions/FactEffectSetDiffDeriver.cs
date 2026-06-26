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
// Categories = the sorted distinct `provider:operation`(s) of the PRESENT EP's effects that produced this
// resource (post-Filter) — so a consumer can label the row by KIND (e.g. `permission:assert` = a guard the
// present EP enforces that the absent one doesn't; `llblgen:write` = a durable write). Empty only if a caller
// constructs a finding directly without categories.
public sealed record EffectSetDiffFinding(
    string Label,
    string ResourceKey,
    EffectDiffSide Direction,
    string PresentEpId,
    string AbsentEpId,
    IReadOnlyList<string> Categories
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

        // 3. Build a lookup: enclosing-id -> (normalized resource key -> set of `provider:operation`).
        //    This is the per-method effect-key set with its category(ies); an EP's effect-set is the UNION
        //    over its reach set. Carrying the provider:op lets the diff label each row by KIND (guard vs write).
        var keysByEnclosing = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
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

            if (!keysByEnclosing.TryGetValue(e.EnclosingSymbolId, out var keys))
            {
                keys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                keysByEnclosing[e.EnclosingSymbolId] = keys;
            }

            if (!keys.TryGetValue(key, out var cats))
            {
                cats = new HashSet<string>(StringComparer.Ordinal);
                keys[key] = cats;
            }

            cats.Add($"{e.Provider}:{e.Operation}");
        }

        // 4. For each pair compute write-sets and emit symmetric-difference findings.
        var findings = new List<EffectSetDiffFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in spec.Pairs)
        {
            var primaryWrites = CollectWriteSet(
                reachSet: reachOf.TryGetValue(pair.AId, out var pr) ? pr : [],
                keysByEnclosing: keysByEnclosing
            );
            var secondaryWrites = CollectWriteSet(
                reachSet: reachOf.TryGetValue(pair.BId, out var sr) ? sr : [],
                keysByEnclosing: keysByEnclosing
            );

            // primary \ secondary (tables the primary writes that secondary doesn't).
            foreach (var (key, cats) in primaryWrites)
            {
                if (!secondaryWrites.ContainsKey(key))
                {
                    AddFinding(
                        findings: findings,
                        seen: seen,
                        entityLabel: pair.Label,
                        resourceKey: key,
                        direction: EffectDiffSide.AOnly,
                        presentEpId: pair.AId,
                        absentEpId: pair.BId,
                        categories: cats
                    );
                }
            }

            // secondary \ primary (tables the secondary writes that primary doesn't).
            foreach (var (key, cats) in secondaryWrites)
            {
                if (!primaryWrites.ContainsKey(key))
                {
                    AddFinding(
                        findings: findings,
                        seen: seen,
                        entityLabel: pair.Label,
                        resourceKey: key,
                        direction: EffectDiffSide.BOnly,
                        presentEpId: pair.BId,
                        absentEpId: pair.AId,
                        categories: cats
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

    // Compute the effect-key set for an EP: the UNION of all normalized resource keys for every method node
    // in the EP's reach set (which includes the seed itself), each mapped to the set of `provider:operation`
    // categories that produced it across the reach.
    private static Dictionary<string, HashSet<string>> CollectWriteSet(
        IReadOnlyCollection<string> reachSet,
        Dictionary<string, Dictionary<string, HashSet<string>>> keysByEnclosing
    )
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var node in reachSet)
        {
            if (!keysByEnclosing.TryGetValue(node, out var keys))
            {
                continue;
            }

            foreach (var (key, cats) in keys)
            {
                if (!result.TryGetValue(key, out var acc))
                {
                    acc = new HashSet<string>(StringComparer.Ordinal);
                    result[key] = acc;
                }

                acc.UnionWith(cats);
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
        string absentEpId,
        IReadOnlyCollection<string> categories
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
                    AbsentEpId: absentEpId,
                    Categories: categories.OrderBy(c => c, StringComparer.Ordinal).ToArray()
                )
            );
        }
    }
}

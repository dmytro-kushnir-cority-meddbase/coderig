using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Stage-2 STRUCTURAL write-set divergence detector. Two entry points that perform the "same" logical
// operation on an entity (e.g. a canonical UI/save path vs an import/API path) may write DIFFERENT
// sets of tables: the secondary path silently skips junction/link/event/denormalized rows the primary
// maintains, leaving stale/inconsistent data with no exception. This deriver surfaces the set-diff
// over per-EP reachable write-sets declared in the rule.
//
// Architecture:
//   - Pure: no I/O, input not mutated, deterministic (de-dup + stable sort by (EntityLabel, ResourceKey,
//     Direction)).
//   - Spec-driven: the caller (DeriveCommand) resolves entry-point PATTERNS to exact node DocIDs and
//     passes the resolved pairs; the deriver does NOT do substring matching.
//   - Write-set of an EP = the set of normalized resource keys of every effect that (a) matches one of
//     the write predicates and (b) has its EnclosingSymbolId in that EP's forward reach set.
//   - One finding per table in the SYMMETRIC difference: primary-only or secondary-only.

// One resolved (primary EP id, secondary EP id) pair for a declared entity.
public sealed record WriteSetDivergencePair(string EntityLabel, string PrimaryEnclosingId, string SecondaryEnclosingId);

// Spec handed to the deriver: already-resolved pairs + the predicates that define "write".
public sealed record WriteSetDivergenceSpec(
    IReadOnlyList<WriteSetDivergencePair> Pairs,
    // The effects that count as a write (matched against DerivedEffect.Provider + Operation).
    IReadOnlyList<EffectPredicate> WritePredicates,
    // Normalize the effect's ResourceType to a comparable resource key (simple-type-name + suffix strip,
    // same helper FactCorrelationDeriver uses). Mirror the LLBLGen-shaped defaults the caller passes.
    NormalizeSpec WriteNormalize,
    int MaxDepth = 20,
    FactPathFinder.TraversalMode Mode = FactPathFinder.TraversalMode.SyncCut
);

// "primary-only" = the table is written by the primary EP but NOT the secondary (the secondary path
// silently skips it). "secondary-only" = the reverse (secondary writes something the primary doesn't —
// also anomalous, but less common as the primary-only shape).
public enum WriteSetDirection
{
    PrimaryOnly,
    SecondaryOnly,
}

// One diverging table. PresentEpId = the EP that DOES write it; AbsentEpId = the EP that SHOULD but
// doesn't. Site (Enclosing/FilePath/Line) anchors the ABSENT EP for the hazard finding site so the
// engineer is sent to the path that is MISSING the write.
public sealed record WriteSetDivergenceFinding(
    string EntityLabel,
    string ResourceKey,
    WriteSetDirection Direction,
    string PresentEpId,
    string AbsentEpId
);

public static class FactWriteSetDivergenceDeriver
{
    // Returns every table in the symmetric write-set difference across the declared pairs. Determinism:
    // de-duped, ordered stably by (EntityLabel ordinal, ResourceKey ordinal, Direction).
    public static IReadOnlyList<WriteSetDivergenceFinding> Derive(
        FactGraphData graph,
        IReadOnlyList<DerivedEffect> effects,
        WriteSetDivergenceSpec spec
    )
    {
        if (spec.Pairs.Count == 0 || spec.WritePredicates.Count == 0)
        {
            return [];
        }

        // 1. Collect all distinct EP ids that need a forward reach.
        var distinctEpIds = new List<string>();
        var epIdSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in spec.Pairs)
        {
            if (epIdSet.Add(pair.PrimaryEnclosingId))
            {
                distinctEpIds.Add(pair.PrimaryEnclosingId);
            }
            if (epIdSet.Add(pair.SecondaryEnclosingId))
            {
                distinctEpIds.Add(pair.SecondaryEnclosingId);
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

            if (!MatchesAnyWrite(e, spec.WritePredicates))
            {
                continue;
            }

            var key = ResourceKey.Of(e.ResourceType, spec.WriteNormalize);
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
        var findings = new List<WriteSetDivergenceFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in spec.Pairs)
        {
            var primaryWrites = CollectWriteSet(
                reachSet: reachOf.TryGetValue(pair.PrimaryEnclosingId, out var pr) ? pr : [],
                writeKeysByEnclosing: writeKeysByEnclosing
            );
            var secondaryWrites = CollectWriteSet(
                reachSet: reachOf.TryGetValue(pair.SecondaryEnclosingId, out var sr) ? sr : [],
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
                        entityLabel: pair.EntityLabel,
                        resourceKey: key,
                        direction: WriteSetDirection.PrimaryOnly,
                        presentEpId: pair.PrimaryEnclosingId,
                        absentEpId: pair.SecondaryEnclosingId
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
                        entityLabel: pair.EntityLabel,
                        resourceKey: key,
                        direction: WriteSetDirection.SecondaryOnly,
                        presentEpId: pair.SecondaryEnclosingId,
                        absentEpId: pair.PrimaryEnclosingId
                    );
                }
            }
        }

        // 5. Determinism: stable sort by (EntityLabel ordinal, ResourceKey ordinal, Direction).
        findings.Sort(
            (a, b) =>
            {
                var byEntity = string.CompareOrdinal(a.EntityLabel, b.EntityLabel);
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

    private static bool MatchesAnyWrite(DerivedEffect e, IReadOnlyList<EffectPredicate> predicates)
    {
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
        List<WriteSetDivergenceFinding> findings,
        HashSet<string> seen,
        string entityLabel,
        string resourceKey,
        WriteSetDirection direction,
        string presentEpId,
        string absentEpId
    )
    {
        // De-dup key: entity + resource key + direction + absent EP (the primary signal is the gap on the absent side).
        var dedupeKey = entityLabel + "\0" + resourceKey + "\0" + (int)direction + "\0" + absentEpId;
        if (seen.Add(dedupeKey))
        {
            findings.Add(
                new WriteSetDivergenceFinding(
                    EntityLabel: entityLabel,
                    ResourceKey: resourceKey,
                    Direction: direction,
                    PresentEpId: presentEpId,
                    AbsentEpId: absentEpId
                )
            );
        }
    }
}

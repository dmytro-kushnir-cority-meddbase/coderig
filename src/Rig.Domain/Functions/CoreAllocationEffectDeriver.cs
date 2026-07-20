using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Compiler-owned allocation facts are core effects, independent of user effect rules. This pure stage-2
// projection deliberately reuses the ordinary observation vocabulary so allocations inside loops carry
// looped_effect exactly like rule-derived effects.
public static class CoreAllocationEffectDeriver
{
    public static IReadOnlyList<DerivedEffect> Derive(IReadOnlyList<AllocationFact> allocations, FactObservationRules observationRules) =>
        allocations
            .Select(a => new DerivedEffect(
                Provider: "alloc",
                Operation: a.Operation,
                ResourceType: a.ResourceType,
                EnclosingSymbolId: a.EnclosingSymbolId,
                FilePath: a.FilePath,
                Line: a.Line,
                Observations: FactObservationDeriver.Derive(
                    methodName: "",
                    // A compiler-cached delegate may be referenced from a loop, but its allocation happens
                    // only on first use; presenting it as loop-amplified would turn honest cardinality into a
                    // false hot-path signal. Keep the raw lexical loop on the fact, not the derived observation.
                    loopKind: a.Cardinality == "cached_first_use" ? null : a.EnclosingLoopKind,
                    loopDetail: a.Cardinality == "cached_first_use" ? null : a.EnclosingLoopDetail,
                    enclosingInvocations: [],
                    catchTypes: [],
                    rules: observationRules
                ),
                EnclosingGuards: a.EnclosingGuards,
                Mechanism: a.Mechanism,
                Cardinality: a.Cardinality,
                ShallowSizeBytes: a.ShallowSizeBytes,
                SizeConfidence: a.SizeConfidence,
                SizeBasis: a.SizeBasis
            ))
            .ToList();
}

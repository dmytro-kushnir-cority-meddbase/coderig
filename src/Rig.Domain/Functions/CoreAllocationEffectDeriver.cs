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
                    loopKind: a.EnclosingLoopKind,
                    loopDetail: a.EnclosingLoopDetail,
                    enclosingInvocations: [],
                    catchTypes: [],
                    rules: observationRules
                ),
                EnclosingGuards: a.EnclosingGuards
            ))
            .ToList();
}

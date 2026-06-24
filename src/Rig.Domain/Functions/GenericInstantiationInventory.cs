using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Phase 1 of static monomorphization (docs/design-dispatch-precision.md): the INSTANTIATION INVENTORY.
//
// Computes the set of generic-method INSTANTIATIONS — `(genericMethod, concrete declaring type-args,
// concrete method type-args)` triples — that actually occur on call edges in the graph, so a LATER phase
// can clone+substitute their bodies. This phase ONLY inventories from the facts: it does NOT clone,
// substitute bodies, mutate the graph, or touch any traversal. Output is a pure data structure.
//
// v1 scope — DIRECT, CONCRETE-ONLY, CAPPED:
//   - An instantiation is recorded ONLY when each present binding is FULLY CONCRETE (every element `C:`).
//     A forwarded (`M:`/`T:`) or unresolved (`?`) element means the caller is itself generic / the arg
//     didn't bind — those need transitive resolution when the caller is monomorphized (a later refinement);
//     v1 SKIPS the edge and the method stays CHA (the sound fallback). ~92% of bindings are fully concrete.
//   - Covers BOTH method generics (MethodTypeArgBinding) AND declaring-type generics (DeclaringTypeArgBinding).
//   - Capped per-method and in total; over-cap methods drop ALL their instantiations and stay CHA (disclosed).
public static class GenericInstantiationInventory
{
    // One reachable generic-method instantiation. Keyed by callee method id + its concrete declaring-type
    // binding (empty when the callee's declaring type is non-generic) + its concrete method binding (empty
    // when the callee method itself is non-generic). Both bindings are the parsed, marker-stripped concrete
    // type lists (e.g. ["MedDBase…BillingRuleEntity", "int"]).
    public sealed record GenericInstantiation(string MethodId, IReadOnlyList<string> DeclaringBinding, IReadOnlyList<string> MethodBinding);

    // The inventory plus the methods that were CAPPED (too many distinct instantiations, or total cap hit) —
    // those contribute ZERO instantiations and remain CHA at materialization.
    public sealed record InstantiationInventoryResult(
        IReadOnlyList<GenericInstantiation> Instantiations,
        IReadOnlyList<string> CappedMethods
    );

    public static InstantiationInventoryResult Build(FactGraphData graph, int maxPerMethod = 50, int maxTotal = 100_000)
    {
        // Distinct instantiations grouped by method, deduped by full key (method + both bindings).
        var byMethod = new Dictionary<string, Dictionary<string, GenericInstantiation>>(StringComparer.Ordinal);
        var capped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in graph.CallEdges)
        {
            var declaringBinding = edge.DeclaringTypeArgBinding;
            var methodBinding = edge.MethodTypeArgBinding;

            var hasDeclaring = !string.IsNullOrWhiteSpace(declaringBinding);
            var hasMethod = !string.IsNullOrWhiteSpace(methodBinding);

            // Neither binding present -> not a generic instantiation.
            if (!hasDeclaring && !hasMethod)
            {
                continue;
            }

            // Any PRESENT binding that is not fully concrete -> forwarded/unresolved; v1 leaves it CHA.
            if (hasDeclaring && !GenericSubstitution.IsFullyConcrete(declaringBinding))
            {
                continue;
            }

            if (hasMethod && !GenericSubstitution.IsFullyConcrete(methodBinding))
            {
                continue;
            }

            var concreteDeclaring = hasDeclaring ? GenericSubstitution.ParseBinding(declaringBinding) : Array.Empty<string>();
            var concreteMethod = hasMethod ? GenericSubstitution.ParseBinding(methodBinding) : Array.Empty<string>();

            var methodId = edge.Callee;
            var key = KeyOf(methodId: methodId, declaring: concreteDeclaring, method: concreteMethod);

            if (capped.Contains(methodId))
            {
                continue;
            }

            if (!byMethod.TryGetValue(methodId, out var distinct))
            {
                distinct = new Dictionary<string, GenericInstantiation>(StringComparer.Ordinal);
                byMethod[methodId] = distinct;
            }

            if (distinct.ContainsKey(key))
            {
                // Same instantiation reached from another call site — already inventoried.
                continue;
            }

            // Per-method cap: adding this would exceed maxPerMethod distinct instantiations -> drop them all.
            if (distinct.Count >= maxPerMethod)
            {
                capped.Add(methodId);
                byMethod.Remove(methodId);
                continue;
            }

            distinct[key] = new GenericInstantiation(
                MethodId: methodId,
                DeclaringBinding: concreteDeclaring,
                MethodBinding: concreteMethod
            );
        }

        // Apply the TOTAL cap deterministically: flatten in stable order, take up to maxTotal, and record
        // any method that contributes an instantiation beyond the cut as capped (it stays CHA).
        var ordered = byMethod
            .Values.SelectMany(distinct => distinct.Values)
            .OrderBy(i => i.MethodId, StringComparer.Ordinal)
            .ThenBy(i => string.Join(",", i.DeclaringBinding), StringComparer.Ordinal)
            .ThenBy(i => string.Join(",", i.MethodBinding), StringComparer.Ordinal)
            .ToList();

        var kept = new List<GenericInstantiation>(Math.Min(ordered.Count, maxTotal));
        foreach (var instantiation in ordered)
        {
            if (kept.Count >= maxTotal)
            {
                capped.Add(instantiation.MethodId);
                continue;
            }

            kept.Add(instantiation);
        }

        // A method partially cut by the total cap also has its kept instantiations dropped — capped means CHA.
        if (capped.Count > 0)
        {
            kept = kept.Where(i => !capped.Contains(i.MethodId)).ToList();
        }

        var cappedMethods = capped.OrderBy(m => m, StringComparer.Ordinal).ToList();
        return new InstantiationInventoryResult(Instantiations: kept, CappedMethods: cappedMethods);
    }

    // Stable dedupe key: type names never contain '|' or the unit-separator, so join is unambiguous enough
    // for an in-memory dedupe (the ordered output is the canonical form).
    private static string KeyOf(string methodId, IReadOnlyList<string> declaring, IReadOnlyList<string> method) =>
        methodId + "" + string.Join("", declaring) + "" + string.Join("", method);
}

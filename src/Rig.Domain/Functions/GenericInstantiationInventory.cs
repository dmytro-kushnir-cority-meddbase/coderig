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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<GenericInstantiation>();

        // Closure index: a generic method's BODY edges plus the edges of its LAMBDA sub-nodes (id
        // `{method}~λN`), which close over the method's type-params — so a type-param dispatch or a forwarded
        // generic call INSIDE a lambda is materialized with the method (the dominant real-world shape).
        var edgesByCaller = new Dictionary<string, List<CallEdge>>(StringComparer.Ordinal);
        var lambdaCallersByMethod = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in graph.CallEdges)
        {
            if (!edgesByCaller.TryGetValue(edge.Caller, out var list))
            {
                edgesByCaller[edge.Caller] = list = new List<CallEdge>();
            }

            list.Add(edge);
        }

        foreach (var caller in edgesByCaller.Keys)
        {
            var lambda = caller.IndexOf("~λ", StringComparison.Ordinal);
            if (lambda > 0)
            {
                var baseMethod = caller[..lambda];
                if (!lambdaCallersByMethod.TryGetValue(baseMethod, out var lambdas))
                {
                    lambdaCallersByMethod[baseMethod] = lambdas = new List<string>();
                }

                lambdas.Add(caller);
            }
        }

        // Record + enqueue an instantiation; returns false (and may CAP the method) when over the per-method
        // bound. Dedup is GLOBAL (a transitively-reached instantiation can arrive from several callers).
        bool TryEnqueue(string methodId, IReadOnlyList<string> declaring, IReadOnlyList<string> method)
        {
            if (capped.Contains(methodId))
            {
                return false;
            }

            var key = KeyOf(methodId: methodId, declaring: declaring, method: method);
            if (!seen.Add(key))
            {
                return false; // already inventoried (this exact instantiation)
            }

            if (!byMethod.TryGetValue(methodId, out var distinct))
            {
                distinct = new Dictionary<string, GenericInstantiation>(StringComparer.Ordinal);
                byMethod[methodId] = distinct;
            }

            // Per-method cap: drop ALL of the method's instantiations -> it stays CHA (disclosed).
            if (distinct.Count >= maxPerMethod)
            {
                capped.Add(methodId);
                byMethod.Remove(methodId);
                return false;
            }

            var instantiation = new GenericInstantiation(MethodId: methodId, DeclaringBinding: declaring, MethodBinding: method);
            distinct[key] = instantiation;
            queue.Enqueue(instantiation);
            return true;
        }

        // Resolve an edge's (declaring, method) bindings against an ENCLOSING instantiation's concrete bindings
        // (empty enclosing = the seed pass, where only all-`C:` edges resolve). Returns the callee instantiation
        // to enqueue, or null when nothing resolves / it's a non-generic edge.
        void ScanEdge(CallEdge edge, IReadOnlyList<string> enclDeclaring, IReadOnlyList<string> enclMethod)
        {
            var declTokens = GenericSubstitution.ParseBindingTokens(edge.DeclaringTypeArgBinding);
            var methTokens = GenericSubstitution.ParseBindingTokens(edge.MethodTypeArgBinding);
            if (declTokens.Count == 0 && methTokens.Count == 0)
            {
                return; // non-generic edge
            }

            var declaring = GenericSubstitution.ResolveTokens(declTokens, enclDeclaring, enclMethod);
            var method = GenericSubstitution.ResolveTokens(methTokens, enclDeclaring, enclMethod);
            if (declaring is null || method is null)
            {
                return; // a forwarded token didn't resolve in this context -> leave the callee CHA
            }

            TryEnqueue(edge.Callee, declaring, method);
        }

        // SEED: every edge whose bindings are fully concrete on their own (resolve against empty enclosing).
        foreach (var edge in graph.CallEdges)
        {
            ScanEdge(edge, Array.Empty<string>(), Array.Empty<string>());
        }

        // FIXPOINT: each instantiation's closure (its body + its lambdas') edges, resolving forwarded tokens
        // against THIS instantiation's concrete bindings, enqueues the nested instantiations it reaches.
        while (queue.Count > 0)
        {
            var inst = queue.Dequeue();
            ScanClosure(inst.MethodId);
            if (lambdaCallersByMethod.TryGetValue(inst.MethodId, out var lambdas))
            {
                foreach (var lambdaCaller in lambdas)
                {
                    ScanClosure(lambdaCaller);
                }
            }

            void ScanClosure(string caller)
            {
                if (!edgesByCaller.TryGetValue(caller, out var bodyEdges))
                {
                    return;
                }

                foreach (var e in bodyEdges)
                {
                    ScanEdge(e, inst.DeclaringBinding, inst.MethodBinding);
                }
            }
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

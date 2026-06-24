using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Phase 2 of static monomorphization (docs/design-dispatch-precision.md): MATERIALIZE the monomorphized
// subgraph as a PURE FUNCTION over an in-memory FactGraphData. Each reachable generic-method instantiation
// (from the Phase-1 inventory) becomes a DISTINCT node whose body call edges have their type-param receiver
// SUBSTITUTED with the concrete type the caller bound — so the EXISTING dispatch-narrowing machinery
// (FactPathFinder.ResolveNarrowRoot / NarrowByReceiver, driven by CallEdge.ReceiverType) resolves the
// concrete runtime override instead of CHA-fanning to every base-type override.
//
// NO loader wiring, NO Roslyn, NO traversal-engine change. Returns a NEW FactGraphData (FactGraphData and
// CallEdge are immutable records — never mutated). The base generic methods + their body edges are KEPT
// unchanged (soundness: forwarded / unresolved / capped callers still reach base `M` with full CHA).
//
// v1 scope (mirrors the inventory): DIRECT, CONCRETE-ONLY. Only the body edges' ReceiverType (the fan
// driver) is substituted; nested TypeArguments / *Binding are left as-is — transitive/nested-binding
// propagation is explicitly DEFERRED. Only an incoming edge whose bindings are FULLY CONCRETE and match an
// inventory entry is redirected; anything else is left pointing at base `M` (sound CHA fallback).
public static class GenericMonomorphizer
{
    public static FactGraphData Materialize(
        FactGraphData graph,
        GenericInstantiationInventory.InstantiationInventoryResult inventory,
        // Ordered type-parameter names for a symbol id (a METHOD id OR a declaring-TYPE id). MethodRef
        // carries no Signature, so the names cannot be read off the graph — the caller supplies them
        // (in real wiring, mined from symbol_facts.Signature via GenericSubstitution.ParseTypeParameterNames;
        // in tests, supplied directly). May return empty for a non-generic / unknown id.
        Func<string, IReadOnlyList<string>> typeParamNamesFor
    )
    {
        // Index methods by id -> ContainingTypeId (for the declaring-type param zip), and call edges by
        // Caller (to find a generic method's body edges to clone).
        var containingTypeOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var m in graph.Methods)
        {
            containingTypeOf[m.SymbolId] = m.ContainingTypeId;
        }

        var edgesByCaller = new Dictionary<string, List<CallEdge>>(StringComparer.Ordinal);
        foreach (var edge in graph.CallEdges)
        {
            if (!edgesByCaller.TryGetValue(edge.Caller, out var list))
            {
                list = new List<CallEdge>();
                edgesByCaller[edge.Caller] = list;
            }

            list.Add(edge);
        }

        // Redirect lookup: (callee method id + parsed concrete declaring binding + parsed concrete method
        // binding) -> the instantiation node id. Same key shape as the inventory dedupe. Also the per-
        // instantiation cloned body edges, emitted after the (redirected) originals in a stable order.
        var instIdByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var clonedBodyEdges = new List<CallEdge>();

        foreach (var inst in inventory.Instantiations)
        {
            var instId = MonomorphizedNodeId.For(
                baseMethodId: inst.MethodId,
                declaringBinding: inst.DeclaringBinding,
                methodBinding: inst.MethodBinding
            );
            instIdByKey[KeyOf(methodId: inst.MethodId, declaring: inst.DeclaringBinding, method: inst.MethodBinding)] = instId;

            // Merged type-param -> concrete map. DECLARING-type params first, METHOD params second so that
            // on a (rare) name collision the METHOD param wins (later TryAdd is a no-op; so seed declaring,
            // then overwrite-by-priority isn't needed — instead add method LAST with a plain indexer to win).
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (containingTypeOf.TryGetValue(inst.MethodId, out var containingType) && containingType is not null)
            {
                ZipInto(map, names: typeParamNamesFor(containingType), binding: inst.DeclaringBinding, methodWins: false);
            }

            ZipInto(map, names: typeParamNamesFor(inst.MethodId), binding: inst.MethodBinding, methodWins: true);

            // Clone each body edge of the generic method, substituting ONLY ReceiverType against the map.
            if (edgesByCaller.TryGetValue(inst.MethodId, out var bodyEdges))
            {
                foreach (var e in bodyEdges)
                {
                    var newReceiver = e.ReceiverType is null ? null : GenericSubstitution.Substitute(e.ReceiverType, map);
                    clonedBodyEdges.Add(e with { Caller = instId, ReceiverType = newReceiver });
                }
            }
        }

        // REDIRECT incoming edges: for each original edge whose callee is a generic method with an
        // instantiation, when this edge's present bindings are each fully concrete AND (callee, parsed decl,
        // parsed meth) matches an inventory entry, REPLACE the edge's callee with the instId. Otherwise keep
        // the original (pointing at base `M`) — the sound CHA fallback. REPLACE, not add: keeping both would
        // still reach base `M` and re-explode the fan.
        var outEdges = new List<CallEdge>(graph.CallEdges.Count + clonedBodyEdges.Count);
        foreach (var e in graph.CallEdges)
        {
            var instId = ResolveRedirect(e, instIdByKey);
            outEdges.Add(instId is null ? e : e with { Callee = instId });
        }

        // Cloned body edges AFTER the (redirected) originals, ordered by instId then by source-edge order
        // (clonedBodyEdges already preserves the inventory order + per-method body order, which is stable;
        // an explicit ordered-by-instId pass makes the emit order independent of inventory ordering).
        clonedBodyEdges.Sort((a, b) => string.Compare(a.Caller, b.Caller, StringComparison.Ordinal));
        outEdges.AddRange(clonedBodyEdges);

        // Every other field passes through unchanged.
        return graph with
        {
            CallEdges = outEdges,
        };
    }

    // Zip ordered param NAMES with the concrete BINDING positionally into `map`. Out-of-range positions are
    // skipped (arity mismatch tolerated, no throw). `methodWins` true => a name already present is
    // OVERWRITTEN (method params shadow declaring-type params); false => first-writer (declaring) is kept.
    private static void ZipInto(Dictionary<string, string> map, IReadOnlyList<string> names, IReadOnlyList<string> binding, bool methodWins)
    {
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (string.IsNullOrEmpty(name) || i >= binding.Count)
            {
                continue;
            }

            if (methodWins)
            {
                map[name] = binding[i];
            }
            else
            {
                map.TryAdd(name, binding[i]);
            }
        }
    }

    // The instantiation node id an incoming edge should be redirected to, or null to leave it at base `M`.
    // Uses the SAME parse / fully-concrete logic as the inventory: each PRESENT binding must be fully
    // concrete (else forwarded/unresolved — keep base `M`); the parsed key must match an inventory entry.
    private static string? ResolveRedirect(CallEdge edge, Dictionary<string, string> instIdByKey)
    {
        if (instIdByKey.Count == 0)
        {
            return null;
        }

        var hasDeclaring = !string.IsNullOrWhiteSpace(edge.DeclaringTypeArgBinding);
        var hasMethod = !string.IsNullOrWhiteSpace(edge.MethodTypeArgBinding);
        if (!hasDeclaring && !hasMethod)
        {
            return null;
        }

        if (hasDeclaring && !GenericSubstitution.IsFullyConcrete(edge.DeclaringTypeArgBinding))
        {
            return null;
        }

        if (hasMethod && !GenericSubstitution.IsFullyConcrete(edge.MethodTypeArgBinding))
        {
            return null;
        }

        var declaring = hasDeclaring ? GenericSubstitution.ParseBinding(edge.DeclaringTypeArgBinding) : Array.Empty<string>();
        var method = hasMethod ? GenericSubstitution.ParseBinding(edge.MethodTypeArgBinding) : Array.Empty<string>();
        return instIdByKey.TryGetValue(KeyOf(methodId: edge.Callee, declaring: declaring, method: method), out var instId) ? instId : null;
    }

    // Stable key matching GenericInstantiationInventory.KeyOf: type names never contain the unit separator,
    // so the join is unambiguous for an in-memory lookup.
    private static string KeyOf(string methodId, IReadOnlyList<string> declaring, IReadOnlyList<string> method) =>
        methodId + "" + string.Join("", declaring) + "" + string.Join("", method);
}

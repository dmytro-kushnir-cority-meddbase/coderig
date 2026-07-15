using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// DELEGATE-FIELD JOIN — the conservative seam that reconnects a delegate-field INVOCATION to the
// callable(s) ASSIGNED to that field. The forward walk otherwise cuts here: `saveFunc(...)` invokes a
// value held in mutable field state, and the lambdas assigned to `saveFunc` elsewhere are unreachable
// from the invocation (the DFS Azure-blob leg the classic API can't reach). This restores that hop as a
// DIRECT invoking-method -> assigned-callable call edge — never keyed to the `F:` field id, which is not
// a call-graph node.
//
// The inputs are the stage-1 dispatch facts (DispatchKinds.DelegateField*), each already carrying the
// LOCAL soundness decision the extractor could make per site:
//   * bind facts exist only for assignments INSIDE the declaring type;
//   * an escape fact exists iff SOME assignment happened OUTSIDE it;
//   * invoke facts exist only for invocations INSIDE the declaring type.
// This layer adds the one GLOBAL check the extractor couldn't: a field with ANY escape fact is dropped
// entirely (its assignments aren't all in the declaring type — leave the cut, a disclosed residual).
// Surviving fields fan the union of their assigned callables to every in-type invoker (same over-
// approximation philosophy as dispatch fan-out; the single-assignment DFS case is precise). ONE HOP: the
// joined callable's BODY is walked normally, but the join synthesizes no further dispatch — it is a real
// call edge, so the one-hop dispatch discipline is never composed onto it (mirrors a methodGroup edge
// into a lambda).
//
// Applied identically by BOTH FactGraphData builders (FromAnalysis at index, LoadFactGraphAsync at query)
// so the two projections stay field-for-field in parity. Reads ONLY the new fact kinds, so a store mined
// before they existed yields no join edges — the change is invisible on old facts (no cache-schema bump).
public static class FactDelegateFieldJoin
{
    public static FactGraphData Apply(FactGraphData graph)
    {
        var extra = Edges(graph.MinedDispatch);
        return extra.Count == 0 ? graph : graph with { CallEdges = [.. graph.CallEdges, .. extra] };
    }

    // The synthesized delegate-field call edges, deterministically ordered so both projections emit an
    // identical set (the parity contract). Pure over the mined facts.
    public static IReadOnlyList<CallEdge> Edges(IReadOnlyList<DispatchFact>? mined)
    {
        if (mined is null or { Count: 0 })
        {
            return [];
        }

        var binds = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var invokes = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var escaped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fact in mined)
        {
            switch (fact.Kind)
            {
                case DispatchKinds.DelegateFieldBind:
                    Add(binds, fact.SourceMember, fact.TargetMember);
                    break;
                case DispatchKinds.DelegateFieldInvoke:
                    Add(invokes, fact.SourceMember, fact.TargetMember);
                    break;
                case DispatchKinds.DelegateFieldEscape:
                    escaped.Add(fact.SourceMember);
                    break;
            }
        }

        var edges = new List<CallEdge>();
        foreach (var slot in binds.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            // An assignment outside the declaring type -> not a controlled seam. Leave the cut.
            if (escaped.Contains(slot) || !invokes.TryGetValue(slot, out var invokers))
            {
                continue;
            }

            foreach (var invoker in invokers)
            foreach (var callable in binds[slot])
            {
                edges.Add(new CallEdge(Caller: invoker, Callee: callable, Kind: EdgeKinds.DelegateField, FilePath: "", Line: 0));
            }
        }

        return edges;
    }

    private static void Add(Dictionary<string, SortedSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
        {
            map[key] = set = new SortedSet<string>(StringComparer.Ordinal);
        }

        set.Add(value);
    }
}

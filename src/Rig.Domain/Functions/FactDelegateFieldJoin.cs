using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Reconnects in-type delegate-field invocations to the field's known assigned callables.
// Any external assignment suppresses the join; otherwise multiple assignments conservatively fan out.
// Old stores contain none of these facts and therefore gain no synthesized edges.
public static class FactDelegateFieldJoin
{
    public static FactGraphData Apply(FactGraphData graph)
    {
        var extra = Edges(graph.MinedDispatch);
        return extra.Count == 0 ? graph : graph with { CallEdges = [.. graph.CallEdges, .. extra] };
    }

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

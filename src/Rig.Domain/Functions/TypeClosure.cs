namespace Rig.Domain.Functions;

// Shared base-type BFS closure over type_relation_facts ("base" edges). Used by both the
// entry-point deriver (ClientPage subclass gate) and the effect deriver (ProxyBase subclass gate).
// Generic handling: a subclass edge stores the *instantiated* base (T:Foo`1{T:Bar}) while the
// base's own edge stores the *open* form (T:Foo`1). Both normalise to the bare T:Foo via
// StripGeneric, so keying the edge lookup on the bare form lets the BFS cross generic bases.
public static class TypeClosure
{
    // Builds the base-edge lookup keyed by the generic-stripped base id (BaseId -> child TypeIds).
    public static ILookup<string, string> BuildBaseEdgeLookup(IEnumerable<(string TypeId, string BaseId)> edges) =>
        edges.ToLookup(e => StripGeneric(e.BaseId), e => e.TypeId, StringComparer.Ordinal);

    // BFS down the base-edge graph from the given root type names (DocID or bare), collecting all
    // descendant type DocIDs (both instantiated and bare forms are recorded for membership checks).
    public static HashSet<string> Compute(ILookup<string, string> strippedBaseEdges, IReadOnlyList<string> roots)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>();

        foreach (var root in roots)
        {
            var normalised = root.StartsWith("T:", StringComparison.Ordinal) ? root : $"T:{root}";
            foreach (var seed in ExpandGeneric(normalised))
                if (visited.Add(seed))
                    frontier.Enqueue(seed);
        }

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var child in strippedBaseEdges[StripGeneric(current)])
                foreach (var expanded in ExpandGeneric(child))
                    if (visited.Add(expanded))
                        frontier.Enqueue(expanded);
        }

        return visited;
    }

    // True when the type (or its generic-stripped form) is in the closure.
    public static bool Contains(HashSet<string> closure, string typeId) =>
        ExpandGeneric(typeId).Any(closure.Contains);

    // Reduces a type DocID to its bare (non-generic) form: strips an instantiated argument list
    // (T:Foo`1{T:Bar} -> T:Foo`1 -> T:Foo) and the open-generic arity (T:Foo`1 -> T:Foo).
    public static string StripGeneric(string typeId)
    {
        var brace = typeId.IndexOf('{');
        if (brace > 0)
            typeId = typeId.Substring(0, brace);
        var backtick = typeId.IndexOf('`');
        return backtick > 0 ? typeId.Substring(0, backtick) : typeId;
    }

    // Yields the original DocID and, when generic, its bare prefix — so lookups match both the
    // instantiated (T:Foo{A}) and open-generic (T:Foo`1) forms stored in type_relation_facts.
    public static IEnumerable<string> ExpandGeneric(string typeId)
    {
        yield return typeId;
        var brace = typeId.IndexOf('{');
        if (brace > 0)
        {
            yield return typeId.Substring(0, brace);
            yield break;
        }
        var backtick = typeId.IndexOf('`');
        if (backtick > 0)
            yield return typeId.Substring(0, backtick);
    }
}

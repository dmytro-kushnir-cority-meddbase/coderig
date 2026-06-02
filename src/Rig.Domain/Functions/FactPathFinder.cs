using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Stage-2-over-facts path finding: BFS the fact-derived call graph from any symbol matching
// `fromPattern` to any symbol matching `toPattern`, cross-project, with no entry-point anchoring.
// Includes the interface->concrete DI hop (the single-impl dispatch from Q5) reconstructed from
// type-relation facts + DocID member-name matching — no Roslyn, no SemanticModel.
// (Rig.Domain targets netstandard2.0, so this avoids TryAdd / ranges / Contains(string,cmp).)
public static class FactPathFinder
{
    public static IReadOnlyList<PathStep>? Find(
        FactGraphData graph, string fromPattern, string toPattern, int maxDepth = 20)
    {
        var index = BuildIndex(graph);

        // Parent links carry the edge that reached the node (for path + kind reconstruction).
        var parent = new Dictionary<string, (string From, string Kind, string? File, int Line)?>(StringComparer.Ordinal);
        var queue = new Queue<(string Node, int Depth)>();
        foreach (var start in index.Nodes.Where(n => Contains(n, fromPattern)))
        {
            if (parent.ContainsKey(start))
                continue;
            parent[start] = null;
            queue.Enqueue((start, 0));
        }

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            if (parent[current] is not null && Contains(current, toPattern))
                return Reconstruct(parent, current);

            if (depth >= maxDepth)
                continue;

            foreach (var s in Successors(current, index))
                Enqueue(parent, queue, s.Node, current, s.Kind, s.File, s.Line, depth);
        }

        return null;
    }

    // Full reachability: BFS the call graph (incl. interface->impl dispatch) from every node
    // matching `fromPattern`, returning each reachable method DocID with its shortest depth.
    // Same traversal as Find — so "what does this entry point reach" is consistent with `rig path`.
    public static IReadOnlyDictionary<string, int> Reaches(
        FactGraphData graph, string fromPattern, int maxDepth = 20, int maxNodes = 20000)
    {
        var index = BuildIndex(graph);
        var depthOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var start in index.Nodes.Where(n => Contains(n, fromPattern)))
        {
            if (depthOf.ContainsKey(start))
                continue;
            depthOf[start] = 0;
            queue.Enqueue(start);
        }

        while (queue.Count > 0 && depthOf.Count < maxNodes)
        {
            var current = queue.Dequeue();
            var depth = depthOf[current];
            if (depth >= maxDepth)
                continue;
            foreach (var s in Successors(current, index))
            {
                if (depthOf.ContainsKey(s.Node))
                    continue;
                depthOf[s.Node] = depth + 1;
                queue.Enqueue(s.Node);
            }
        }

        return depthOf;
    }

    private sealed class GraphIndex
    {
        public Dictionary<string, List<CallEdge>> Adjacency = new(StringComparer.Ordinal);
        public Dictionary<string, List<MethodRef>> MethodsByType = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> ImplsByInterface = new(StringComparer.Ordinal);
        public HashSet<string> Nodes = new(StringComparer.Ordinal);
    }

    private static GraphIndex BuildIndex(FactGraphData graph)
    {
        var index = new GraphIndex();
        foreach (var edge in graph.CallEdges)
        {
            if (!index.Adjacency.TryGetValue(edge.Caller, out var list))
                index.Adjacency[edge.Caller] = list = new List<CallEdge>();
            list.Add(edge);
            index.Nodes.Add(edge.Caller);
            index.Nodes.Add(edge.Callee);
        }
        index.MethodsByType = graph.Methods
            .Where(m => m.ContainingTypeId is not null)
            .GroupBy(m => m.ContainingTypeId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        index.ImplsByInterface = graph.ImplementsEdges
            .GroupBy(e => e.InterfaceType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ImplType).Distinct().ToList(), StringComparer.Ordinal);
        foreach (var method in graph.Methods)
            index.Nodes.Add(method.SymbolId);
        return index;
    }

    // Direct call edges + the interface->concrete DI dispatch hop (single shared definition so
    // Find and Reaches traverse identically).
    private static IEnumerable<(string Node, string Kind, string? File, int Line)> Successors(string current, GraphIndex index)
    {
        if (index.Adjacency.TryGetValue(current, out var edges))
            foreach (var edge in edges)
                yield return (edge.Callee, edge.Kind, edge.FilePath, edge.Line);

        var parsed = ParseMethod(current);
        if (parsed is not null && index.ImplsByInterface.TryGetValue(parsed.Value.TypeId, out var impls))
        {
            foreach (var impl in impls)
            {
                if (!index.MethodsByType.TryGetValue(impl, out var implMethods))
                    continue;
                foreach (var concrete in implMethods)
                {
                    if (string.Equals(concrete.Name, parsed.Value.Name, StringComparison.Ordinal))
                        yield return (concrete.SymbolId, "impl-dispatch", null, 0);
                }
            }
        }
    }

    private static void Enqueue(
        Dictionary<string, (string From, string Kind, string? File, int Line)?> parent,
        Queue<(string, int)> queue,
        string node, string from, string kind, string? file, int line, int depth)
    {
        if (parent.ContainsKey(node))
            return;
        parent[node] = (from, kind, file, line);
        queue.Enqueue((node, depth + 1));
    }

    private static IReadOnlyList<PathStep> Reconstruct(
        Dictionary<string, (string From, string Kind, string? File, int Line)?> parent, string target)
    {
        var steps = new List<PathStep>();
        var node = target;
        while (true)
        {
            var link = parent[node];
            steps.Add(new PathStep(node, link?.Kind ?? "entry", link?.File, link?.Line ?? 0));
            if (link is null)
                break;
            node = link.Value.From;
        }
        steps.Reverse();
        return steps;
    }

    // "M:Ns.Type.Member(args)" -> ("T:Ns.Type", "Member"). Null when not a method DocID.
    private static (string TypeId, string Name)? ParseMethod(string docId)
    {
        if (!docId.StartsWith("M:", StringComparison.Ordinal))
            return null;
        var body = docId.Substring(2);
        var paren = body.IndexOf('(');
        if (paren >= 0)
            body = body.Substring(0, paren);
        var lastDot = body.LastIndexOf('.');
        if (lastDot < 0)
            return null;
        return ("T:" + body.Substring(0, lastDot), body.Substring(lastDot + 1));
    }

    private static bool Contains(string value, string pattern) =>
        value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
}

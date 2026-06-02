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
        var adjacency = new Dictionary<string, List<CallEdge>>(StringComparer.Ordinal);
        foreach (var edge in graph.CallEdges)
        {
            if (!adjacency.TryGetValue(edge.Caller, out var list))
                adjacency[edge.Caller] = list = new List<CallEdge>();
            list.Add(edge);
        }

        var methodsByType = graph.Methods
            .Where(m => m.ContainingTypeId is not null)
            .GroupBy(m => m.ContainingTypeId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var implsByInterface = graph.ImplementsEdges
            .GroupBy(e => e.InterfaceType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ImplType).Distinct().ToList(), StringComparer.Ordinal);

        var nodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in graph.CallEdges) { nodes.Add(edge.Caller); nodes.Add(edge.Callee); }
        foreach (var method in graph.Methods) nodes.Add(method.SymbolId);

        // Parent links carry the edge that reached the node (for path + kind reconstruction).
        var parent = new Dictionary<string, (string From, string Kind, string? File, int Line)?>(StringComparer.Ordinal);
        var queue = new Queue<(string Node, int Depth)>();
        foreach (var start in nodes.Where(n => Contains(n, fromPattern)))
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

            // Direct call edges.
            if (adjacency.TryGetValue(current, out var edges))
            {
                foreach (var edge in edges)
                    Enqueue(parent, queue, edge.Callee, current, edge.Kind, edge.FilePath, edge.Line, depth);
            }

            // Interface -> concrete dispatch hop: if `current` is an interface method whose
            // declaring type is implemented somewhere, branch to each implementation's same-named method.
            var parsed = ParseMethod(current);
            if (parsed is not null && implsByInterface.TryGetValue(parsed.Value.TypeId, out var impls))
            {
                foreach (var impl in impls)
                {
                    if (!methodsByType.TryGetValue(impl, out var implMethods))
                        continue;
                    foreach (var concrete in implMethods)
                    {
                        if (string.Equals(concrete.Name, parsed.Value.Name, StringComparison.Ordinal))
                            Enqueue(parent, queue, concrete.SymbolId, current, "impl-dispatch", null, 0, depth);
                    }
                }
            }
        }

        return null;
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

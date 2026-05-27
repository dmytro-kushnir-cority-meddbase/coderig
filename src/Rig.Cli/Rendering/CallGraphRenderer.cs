using Rig.Domain.Data;

namespace Rig.Cli.Rendering;

internal static class CallGraphRenderer
{
    public static void Render(CallGraphInfo callGraph, int entryPointIndex, bool fullMode, bool summaryMode, TextWriter output)
    {
        var allNodes = callGraph.Nodes;
        var focusMode = !fullMode;
        IReadOnlyList<CallGraphNodeInfo> nodes;
        HashSet<string>? effectReachable = null;

        if (focusMode)
        {
            effectReachable = ComputeEffectReachable(allNodes);
            nodes = allNodes.Where(n => effectReachable.Contains(n.Symbol)).ToArray();
        }
        else
        {
            nodes = allNodes;
        }

        if (summaryMode)
        {
            RenderSummary(callGraph, allNodes, entryPointIndex, output);
            return;
        }

        var focusSuffix =
            focusMode && !fullMode ? " (focused)"
            : fullMode ? " (full)"
            : "";
        var nodeCountSuffix = focusMode ? $" / {allNodes.Count} on effect paths" : "";
        output.WriteLine($"Callgraph: [{entryPointIndex}] {callGraph.EntryPoint}{focusSuffix}");
        output.WriteLine($"Nodes: {nodes.Count}{nodeCountSuffix}");
        RenderCycles(callGraph.Cycles, output);

        RenderTree(callGraph, nodes, effectReachable, focusMode, output);
    }

    private static void RenderSummary(
        CallGraphInfo callGraph,
        IReadOnlyList<CallGraphNodeInfo> allNodes,
        int entryPointIndex,
        TextWriter output
    )
    {
        var allEffects = allNodes.SelectMany(n => n.Effects).ToList();

        var seen = new Dictionary<(string, string, string), int>();
        var ordered = new List<(EffectInfo Effect, int Count)>();
        foreach (var e in allEffects)
        {
            var key = (e.Provider, e.Operation, e.Resource);
            if (seen.TryGetValue(key, out var idx))
            {
                var existing = ordered[idx];
                ordered[idx] = (existing.Effect, existing.Count + 1);
            }
            else
            {
                seen[key] = ordered.Count;
                ordered.Add((e, 1));
            }
        }

        output.WriteLine($"Summary: [{entryPointIndex}] {callGraph.EntryPoint}");
        output.WriteLine($"Nodes: {allNodes.Count}  Effects: {allEffects.Count}");

        foreach (var (effect, count) in ordered)
        {
            var countSuffix = count > 1 ? $"  [x{count}]" : "";
            var observations = string.Join("", effect.Observations.Select(o => $"  [{o.Type}:{o.Context}]"));
            output.WriteLine($"  {effect.Provider, -16} {effect.Operation, -14}  {effect.Resource}{countSuffix}{observations}");
        }
    }

    private static void RenderCycles(IReadOnlyList<CallGraphCycleInfo> cycles, TextWriter output)
    {
        if (cycles.Count == 0)
        {
            return;
        }

        output.WriteLine($"Cycles: {cycles.Count}");
        foreach (var cycle in cycles)
        {
            output.WriteLine($"  CYCLE {string.Join(" -> ", cycle.Path.Select(SymbolNameFormatter.Shorten))}");
        }
    }

    private static void RenderTree(
        CallGraphInfo callGraph,
        IReadOnlyList<CallGraphNodeInfo> nodes,
        HashSet<string>? effectReachable,
        bool focusMode,
        TextWriter output
    )
    {
        if (nodes.Count == 0)
            return;

        var symbolToNode = new Dictionary<string, CallGraphNodeInfo>();
        foreach (var node in nodes)
            symbolToNode.TryAdd(node.Symbol, node);

        var visited = new HashSet<string>();
        RenderTreeNode(nodes[0], "  ", "  ", symbolToNode, callGraph.Cycles, effectReachable, focusMode, visited, output);
    }

    private static void RenderTreeNode(
        CallGraphNodeInfo node,
        string linePrefix,
        string childrenPrefix,
        Dictionary<string, CallGraphNodeInfo> symbolToNode,
        IReadOnlyList<CallGraphCycleInfo> cycles,
        HashSet<string>? effectReachable,
        bool focusMode,
        HashSet<string> visited,
        TextWriter output
    )
    {
        output.WriteLine($"{linePrefix}{Path.GetFileName(node.FilePath)}:{node.Line}  {SymbolNameFormatter.Shorten(node.Symbol)}");

        if (!visited.Add(node.Symbol))
            return;

        var calls = node.Calls.Where(c => !focusMode || effectReachable is null || effectReachable.Contains(c)).ToList();
        IReadOnlyList<BoundaryCallInfo> boundaries = focusMode ? [] : node.BoundaryCalls;
        var effects = focusMode ? node.Effects : EffectRenderFormatter.GetUnmatchedEffects(boundaries, node.Effects);

        int total = calls.Count + boundaries.Count + effects.Count;
        int idx = 0;

        foreach (var call in calls)
        {
            idx++;
            bool isLast = idx == total;
            var branch = childrenPrefix + (isLast ? "└─ " : "├─ ");
            var nextChildren = childrenPrefix + (isLast ? "   " : "│  ");

            if (symbolToNode.TryGetValue(call, out var childNode))
            {
                if (visited.Contains(call))
                {
                    var marker = IsCycleEdge(node.Symbol, call, cycles) ? "[cycle]" : "[^]";
                    output.WriteLine(
                        $"{branch}{Path.GetFileName(childNode.FilePath)}:{childNode.Line}  {SymbolNameFormatter.Shorten(childNode.Symbol)}  {marker}"
                    );
                }
                else
                    RenderTreeNode(childNode, branch, nextChildren, symbolToNode, cycles, effectReachable, focusMode, visited, output);
            }
            else
            {
                output.WriteLine($"{branch}CALL {SymbolNameFormatter.Shorten(call)}");
            }
        }

        foreach (var boundary in boundaries)
        {
            idx++;
            bool isLast = idx == total;
            var branch = childrenPrefix + (isLast ? "└─ " : "├─ ");
            var effect = EffectRenderFormatter.FindEffectForBoundary(boundary, node.Effects);
            output.WriteLine(
                effect is null
                    ? $"{branch}BOUNDARY {boundary.Kind} {boundary.Method}"
                    : $"{branch}{EffectRenderFormatter.FormatEffect(effect)}"
            );
        }

        foreach (var effect in effects)
        {
            idx++;
            bool isLast = idx == total;
            var branch = childrenPrefix + (isLast ? "└─ " : "├─ ");
            output.WriteLine($"{branch}{EffectRenderFormatter.FormatEffect(effect)}");
        }
    }

    private static bool IsCycleEdge(string source, string target, IReadOnlyList<CallGraphCycleInfo> cycles)
    {
        return cycles.Any(cycle =>
        {
            for (var i = 0; i < cycle.Path.Count - 1; i++)
            {
                if (
                    string.Equals(cycle.Path[i], source, StringComparison.Ordinal)
                    && string.Equals(cycle.Path[i + 1], target, StringComparison.Ordinal)
                )
                {
                    return true;
                }
            }

            return false;
        });
    }

    private static HashSet<string> ComputeEffectReachable(IReadOnlyList<CallGraphNodeInfo> nodes)
    {
        var callers = new Dictionary<string, HashSet<string>>();
        foreach (var node in nodes)
        {
            foreach (var call in node.Calls)
            {
                if (!callers.TryGetValue(call, out var callerSet))
                {
                    callerSet = [];
                    callers[call] = callerSet;
                }
                callerSet.Add(node.Symbol);
            }
        }

        var reachable = new HashSet<string>();
        var queue = new Queue<string>();

        foreach (var node in nodes.Where(n => n.Effects.Count > 0))
        {
            if (reachable.Add(node.Symbol))
                queue.Enqueue(node.Symbol);
        }

        while (queue.Count > 0)
        {
            var symbol = queue.Dequeue();
            if (!callers.TryGetValue(symbol, out var callerSet))
                continue;
            foreach (var caller in callerSet)
            {
                if (reachable.Add(caller))
                    queue.Enqueue(caller);
            }
        }

        return reachable;
    }
}

using Rig.Analysis;

namespace Rig.Cli.Rendering;

internal static class TraceRenderer
{
    private const int MaxPathsPerEntryPoint = 50;

    public static void Render(TraceInfo trace, bool pathsMode, TextWriter output)
    {
        output.WriteLine($"Trace: {trace.Symbol}");
        output.WriteLine($"Run: {trace.Run.Id} indexed={trace.Run.CreatedAtUtc:u}");
        output.WriteLine();
        output.WriteLine("Reached by entrypoints");

        if (trace.CallGraphs.Count == 0)
        {
            output.WriteLine("  (none)");
            return;
        }

        foreach (var graph in trace.CallGraphs)
        {
            output.WriteLine($"  [{graph.EntryPointIndex,3}] {graph.CallGraph.EntryPoint}");
        }

        if (!pathsMode)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine("Paths");
        foreach (var graph in trace.CallGraphs)
        {
            RenderGraphPaths(trace.Symbol, graph, output);
        }
    }

    public static void RenderAmbiguous(string query, IReadOnlyList<string> symbols, TextWriter error)
    {
        error.WriteLine($"Ambiguous symbol query: {query}");
        error.WriteLine();
        error.WriteLine("Matches");
        foreach (var symbol in symbols)
        {
            error.WriteLine($"  {symbol}");
        }
    }

    private static void RenderGraphPaths(string symbol, TraceCallGraphInfo graph, TextWriter output)
    {
        output.WriteLine($"[{graph.EntryPointIndex,3}] {graph.CallGraph.EntryPoint}");

        var nodesBySymbol = graph.CallGraph.Nodes
            .GroupBy(node => node.Symbol, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        if (!nodesBySymbol.TryGetValue(symbol, out var targetNode))
        {
            output.WriteLine("  target node missing");
            return;
        }

        var callersByTarget = BuildCallersByTarget(graph.CallGraph.Nodes);
        var upstreamPaths = BuildUpstreamPaths(targetNode.Symbol, callersByTarget);

        output.WriteLine("  Upstream");
        foreach (var path in upstreamPaths.Paths)
        {
            RenderPath(path, nodesBySymbol, output);
        }

        if (upstreamPaths.Truncated)
        {
            output.WriteLine($"    ... truncated after {MaxPathsPerEntryPoint} paths");
        }

        output.WriteLine("  Downstream");
        RenderDownstreamNode(targetNode, nodesBySymbol, "    ", new HashSet<string>(StringComparer.Ordinal), output);
    }

    private static Dictionary<string, List<string>> BuildCallersByTarget(IReadOnlyList<CallGraphNodeInfo> nodes)
    {
        var callersByTarget = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            foreach (var call in node.Calls)
            {
                if (!callersByTarget.TryGetValue(call, out var callers))
                {
                    callers = [];
                    callersByTarget[call] = callers;
                }

                callers.Add(node.Symbol);
            }
        }

        return callersByTarget;
    }

    private static (IReadOnlyList<IReadOnlyList<string>> Paths, bool Truncated) BuildUpstreamPaths(
        string targetSymbol,
        Dictionary<string, List<string>> callersByTarget)
    {
        var paths = new List<IReadOnlyList<string>>();
        var stack = new Stack<string>();
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        void Walk(string symbol)
        {
            if (paths.Count >= MaxPathsPerEntryPoint)
            {
                truncated = true;
                return;
            }

            stack.Push(symbol);
            onPath.Add(symbol);

            if (!callersByTarget.TryGetValue(symbol, out var callers) || callers.Count == 0)
            {
                paths.Add(stack.Reverse().ToArray());
            }
            else
            {
                foreach (var caller in callers.OrderBy(c => c, StringComparer.Ordinal))
                {
                    if (onPath.Contains(caller))
                    {
                        continue;
                    }

                    Walk(caller);
                }
            }

            onPath.Remove(symbol);
            stack.Pop();
        }

        Walk(targetSymbol);
        return (paths, truncated);
    }

    private static void RenderPath(
        IReadOnlyList<string> path,
        Dictionary<string, CallGraphNodeInfo> nodesBySymbol,
        TextWriter output)
    {
        for (var i = 0; i < path.Count; i++)
        {
            var prefix = i == 0 ? "    " : "      -> ";
            if (nodesBySymbol.TryGetValue(path[i], out var node))
            {
                output.WriteLine($"{prefix}{FormatNode(node)}");
            }
            else
            {
                output.WriteLine($"{prefix}{SymbolNameFormatter.Shorten(path[i])}");
            }
        }
    }

    private static void RenderDownstreamNode(
        CallGraphNodeInfo node,
        Dictionary<string, CallGraphNodeInfo> nodesBySymbol,
        string indent,
        HashSet<string> visited,
        TextWriter output)
    {
        output.WriteLine($"{indent}{FormatNode(node)}");

        if (!visited.Add(node.Symbol))
        {
            output.WriteLine($"{indent}  [cycle]");
            return;
        }

        foreach (var call in node.Calls)
        {
            if (nodesBySymbol.TryGetValue(call, out var child))
            {
                output.WriteLine($"{indent}  ->");
                RenderDownstreamNode(child, nodesBySymbol, indent + "    ", visited, output);
            }
            else
            {
                output.WriteLine($"{indent}  CALL {SymbolNameFormatter.Shorten(call)}");
            }
        }

        foreach (var boundary in node.BoundaryCalls)
        {
            var effect = EffectRenderFormatter.FindEffectForBoundary(boundary, node.Effects);
            output.WriteLine(effect is null
                ? $"{indent}  BOUNDARY {boundary.Kind} {boundary.Method}"
                : $"{indent}  {EffectRenderFormatter.FormatEffect(effect)}");
        }

        foreach (var effect in EffectRenderFormatter.GetUnmatchedEffects(node.BoundaryCalls, node.Effects))
        {
            output.WriteLine($"{indent}  {EffectRenderFormatter.FormatEffect(effect)}");
        }

        visited.Remove(node.Symbol);
    }

    private static string FormatNode(CallGraphNodeInfo node)
    {
        return $"{Path.GetFileName(node.FilePath)}:{node.Line}  {SymbolNameFormatter.Shorten(node.Symbol)}";
    }
}

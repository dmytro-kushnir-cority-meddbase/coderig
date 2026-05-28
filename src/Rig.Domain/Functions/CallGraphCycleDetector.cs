using Rig.Domain.Data;

namespace Rig.Domain.Functions;

public static class CallGraphCycleDetector
{
    public static IReadOnlyList<CallGraphCycleInfo> Detect(IReadOnlyList<CallGraphNodeInfo> nodes)
    {
        var nodesBySymbol = nodes.ToDictionary(node => node.Symbol, StringComparer.Ordinal);
        var state = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var path = new List<string>();
        var pathIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var cycles = new Dictionary<string, CallGraphCycleInfo>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            Visit(node.Symbol);
        }

        return cycles.Values.OrderBy(cycle => string.Join(" -> ", cycle.Path), StringComparer.Ordinal).ToArray();

        void Visit(string symbol)
        {
            if (!nodesBySymbol.TryGetValue(symbol, out var node))
            {
                return;
            }

            if (state.TryGetValue(symbol, out var existingState) && existingState == VisitState.Done)
            {
                return;
            }

            if (pathIndexes.ContainsKey(symbol))
            {
                AddCycle(symbol);
                return;
            }

            state[symbol] = VisitState.Visiting;
            pathIndexes[symbol] = path.Count;
            path.Add(symbol);

            foreach (var call in node.Calls)
            {
                if (pathIndexes.ContainsKey(call))
                {
                    AddCycle(call);
                }
                else
                {
                    Visit(call);
                }
            }

            path.RemoveAt(path.Count - 1);
            pathIndexes.Remove(symbol);
            state[symbol] = VisitState.Done;
        }

        void AddCycle(string repeatedSymbol)
        {
            var start = pathIndexes[repeatedSymbol];
            var cycle = path.Skip(start).Concat([repeatedSymbol]).ToArray();
            var cycleKey = GetCanonicalKey(cycle);
            if (!cycles.ContainsKey(cycleKey)) cycles.Add(cycleKey, new CallGraphCycleInfo(cycle));
        }
    }

    private static string GetCanonicalKey(IReadOnlyList<string> cycle)
    {
        var uniquePath = cycle.Take(cycle.Count - 1).ToArray();
        if (uniquePath.Length == 0)
        {
            return "";
        }

        var rotations = Enumerable
            .Range(0, uniquePath.Length)
            .Select(start => string.Join("\u001f", uniquePath.Skip(start).Concat(uniquePath.Take(start))));

        return rotations.OrderBy(r => r, StringComparer.Ordinal).First();
    }

    private enum VisitState
    {
        Visiting,
        Done,
    }
}

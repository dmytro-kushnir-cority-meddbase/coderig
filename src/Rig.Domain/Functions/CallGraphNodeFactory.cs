using Rig.Domain.Data;

namespace Rig.Domain.Functions;

public static class CallGraphNodeFactory
{
    public static CallGraphNodeInfo Create(
        string symbol,
        string filePath,
        int line,
        ResolvedCallSetInfo calls,
        IReadOnlyList<EffectInfo> effects,
        string confidence,
        string basis,
        string reason
    )
    {
        return new CallGraphNodeInfo(
            symbol,
            filePath,
            line,
            confidence,
            basis,
            reason,
            calls.Application.OrderBy(call => call.Line).Select(call => call.Key).Distinct(StringComparer.Ordinal).ToArray(),
            calls.Boundary.GroupBy(call => $"{call.Kind}|{call.Target}|{call.Line}").Select(g => g.First()).OrderBy(call => call.Line).ToArray(),
            effects.OrderBy(e => e.Line).ToList()
        );
    }
}

namespace Rig.Analysis;

internal static class CallGraphNodeFactory
{
    public static CallGraphNodeInfo Create(
        string symbol,
        string filePath,
        int line,
        ResolvedCallSet calls,
        IReadOnlyList<EffectInfo> effects,
        string confidence,
        string basis,
        string reason)
    {
        return new CallGraphNodeInfo(
            symbol,
            filePath,
            line,
            confidence,
            basis,
            reason,
            calls.Application.OrderBy(call => call.Line).Select(call => call.Key).Distinct(StringComparer.Ordinal).ToArray(),
            calls.Boundary.DistinctBy(call => $"{call.Kind}|{call.Target}|{call.Line}").OrderBy(call => call.Line).ToArray(),
            effects.OrderBy(e => e.Line).ToList());
    }
}

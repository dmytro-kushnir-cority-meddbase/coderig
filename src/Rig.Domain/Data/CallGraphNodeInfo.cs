namespace Rig.Analysis;

public sealed record CallGraphNodeInfo(
    string Symbol,
    string FilePath,
    int Line,
    string Confidence,
    string Basis,
    string Reason,
    IReadOnlyList<string> Calls,
    IReadOnlyList<BoundaryCallInfo> BoundaryCalls,
    IReadOnlyList<EffectInfo> Effects);
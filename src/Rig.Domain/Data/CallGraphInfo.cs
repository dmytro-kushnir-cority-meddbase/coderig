namespace Rig.Analysis;

public sealed record CallGraphInfo(
    string EntryPoint,
    IReadOnlyList<CallGraphNodeInfo> Nodes,
    IReadOnlyList<CallGraphCycleInfo> Cycles);
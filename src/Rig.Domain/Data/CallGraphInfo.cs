namespace Rig.Domain.Data;

public sealed record CallGraphInfo(string EntryPoint, IReadOnlyList<CallGraphNodeInfo> Nodes, IReadOnlyList<CallGraphCycleInfo> Cycles);

namespace Rig.Analysis;

public sealed record CallGraphCycleInfo(
    IReadOnlyList<string> Path);
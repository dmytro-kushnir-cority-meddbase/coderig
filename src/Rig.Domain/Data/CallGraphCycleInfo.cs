namespace Rig.Domain.Data;

public sealed record CallGraphCycleInfo(IReadOnlyList<string> Path);

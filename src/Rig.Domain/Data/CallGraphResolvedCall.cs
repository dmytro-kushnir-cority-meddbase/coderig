namespace Rig.Domain.Data;

public sealed record ResolvedCallInfo(string Key, int Line = 0);

public sealed record ResolvedCallSetInfo(IReadOnlyList<ResolvedCallInfo> Application, IReadOnlyList<BoundaryCallInfo> Boundary);

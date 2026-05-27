namespace Rig.Domain.Data;

public sealed record TraceInfo(string Symbol, RunSummary Run, IReadOnlyList<TraceCallGraphInfo> CallGraphs);

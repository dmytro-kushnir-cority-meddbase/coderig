namespace Rig.Analysis;

public sealed record TraceInfo(
    string Symbol,
    RunSummary Run,
    IReadOnlyList<TraceCallGraphInfo> CallGraphs);
namespace Rig.Analysis;

public sealed record TraceCallGraphInfo(
    int EntryPointIndex,
    CallGraphInfo CallGraph);
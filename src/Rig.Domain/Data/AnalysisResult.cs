namespace Rig.Domain.Data;

public sealed record AnalysisResult(
    string SolutionPath,
    IReadOnlyList<SourceFileInfo> SourceFiles,
    IReadOnlyList<EntryPointInfo> EntryPoints,
    IReadOnlyList<EffectInfo> Effects,
    IReadOnlyList<DiRegistrationInfo> DiRegistrations,
    IReadOnlyList<CallGraphInfo> CallGraphs,
    IReadOnlyList<MethodObservationInfo> MethodObservations,
    IReadOnlyList<InvocationObservationInfo> InvocationObservations,
    // Optional: stable identity grouping incremental per-project runs of the same solution.
    // When set, the run is linked into a cross-run symbol index so callgraphs can be stitched
    // across project boundaries at query time.
    string? ProjectIdentity = null,
    // Which specific project was indexed (csproj path).  Null for solution-level runs.
    string? SourceProjectPath = null
);

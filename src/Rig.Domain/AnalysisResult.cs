namespace Rig.Analysis;

public record RunSummary(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string SolutionPath,
    int EntryPointCount,
    int EffectCount,
    int DiRegistrationCount,
    int MethodObservationCount,
    int InvocationObservationCount);

public sealed record AnalysisResult(
    string SolutionPath,
    IReadOnlyList<SourceFileInfo> SourceFiles,
    IReadOnlyList<EntryPointInfo> EntryPoints,
    IReadOnlyList<EffectInfo> Effects,
    IReadOnlyList<DiRegistrationInfo> DiRegistrations,
    IReadOnlyList<CallGraphInfo> CallGraphs,
    IReadOnlyList<MethodObservationInfo> MethodObservations,
    IReadOnlyList<InvocationObservationInfo> InvocationObservations);

public sealed record SourceFileInfo(
    string ProjectName,
    string FilePath,
    string Status,
    string Confidence,
    string Basis,
    string Reason,
    string Evidence);

public sealed record EntryPointInfo(
    string Kind,
    string Method,
    string Route,
    string DisplayName,
    string FilePath,
    int Line);

public sealed record EffectInfo(
    string Provider,
    string Operation,
    string Resource,
    string Method,
    string FilePath,
    int Line,
    string Confidence,
    string Basis,
    string Reason,
    IReadOnlyList<EffectObservationInfo> Observations);

public sealed record EffectObservationInfo(
    string Type,
    string Context,
    string Detail,
    string Confidence,
    string Basis,
    string Reason);

public sealed record DiRegistrationInfo(
    string ServiceType,
    string? ImplementationType,
    string Lifetime,
    string RegistrationKind,
    string FilePath,
    int Line,
    string Confidence,
    string Basis,
    string Reason,
    string Evidence);

public sealed record CallGraphInfo(
    string EntryPoint,
    IReadOnlyList<CallGraphNodeInfo> Nodes);

public sealed record TraceInfo(
    string Symbol,
    RunSummary Run,
    IReadOnlyList<TraceCallGraphInfo> CallGraphs);

public sealed record TraceCallGraphInfo(
    int EntryPointIndex,
    CallGraphInfo CallGraph);

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

public sealed record BoundaryCallInfo(
    string Kind,
    string Target,
    string Method,
    string FilePath,
    int Line,
    string Confidence,
    string Basis,
    string Reason);

public sealed record MethodObservationInfo(
    string Symbol,
    string DisplayName,
    string FilePath,
    int Line,
    string ProjectName);

public sealed record InvocationObservationInfo(
    string ContainingMethodSymbol,
    string TargetSymbol,
    string TargetDisplayName,
    string FilePath,
    int Line,
    string Confidence,
    string Basis,
    string Reason);

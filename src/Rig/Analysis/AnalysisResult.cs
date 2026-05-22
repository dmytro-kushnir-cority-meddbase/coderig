namespace Rig.Analysis;

public sealed record AnalysisResult(
    IReadOnlyList<EntryPointInfo> EntryPoints,
    IReadOnlyList<EffectInfo> Effects);

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
    string Reason);

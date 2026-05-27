namespace Rig.Analysis;

public sealed record MethodObservationInfo(
    string Symbol,
    string DisplayName,
    string FilePath,
    int Line,
    string ProjectName);
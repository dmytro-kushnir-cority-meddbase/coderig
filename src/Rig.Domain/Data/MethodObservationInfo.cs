namespace Rig.Domain.Data;

public sealed record MethodObservationInfo(
    string Symbol,
    string DisplayName,
    string FilePath,
    int Line,
    string ProjectName
);

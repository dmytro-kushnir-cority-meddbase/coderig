namespace Rig.Domain.Data;

public sealed record SourceFileInfo(
    string ProjectName,
    string FilePath,
    string Status,
    string Confidence,
    string Basis,
    string Reason,
    string Evidence
);

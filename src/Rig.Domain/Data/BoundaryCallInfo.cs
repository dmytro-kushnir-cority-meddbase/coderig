namespace Rig.Domain.Data;

public sealed record BoundaryCallInfo(
    string Kind,
    string Target,
    string Method,
    string FilePath,
    int Line,
    string Confidence,
    string Basis,
    string Reason
);

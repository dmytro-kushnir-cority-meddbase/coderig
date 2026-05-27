namespace Rig.Analysis;

public sealed record InvocationObservationInfo(
    string ContainingMethodSymbol,
    string TargetSymbol,
    string TargetDisplayName,
    string FilePath,
    int Line,
    string Confidence,
    string Basis,
    string Reason);
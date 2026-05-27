namespace Rig.Analysis;

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
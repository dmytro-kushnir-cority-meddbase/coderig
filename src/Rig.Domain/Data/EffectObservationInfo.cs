namespace Rig.Analysis;

public sealed record EffectObservationInfo(
    string Type,
    string Context,
    string Detail,
    string Confidence,
    string Basis,
    string Reason);
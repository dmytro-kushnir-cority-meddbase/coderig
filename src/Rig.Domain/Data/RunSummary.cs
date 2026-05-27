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
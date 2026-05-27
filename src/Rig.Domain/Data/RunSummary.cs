namespace Rig.Domain.Data;

public record RunSummary(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string SolutionPath,
    int EntryPointCount,
    int EffectCount,
    int DiRegistrationCount,
    int MethodObservationCount,
    int InvocationObservationCount
);

namespace Rig.Domain.Data;

public record RunSummary(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string SolutionPath,
    int SymbolCount,
    int ReferenceCount,
    int DiRegistrationCount,
    string? ProjectIdentity = null,
    string? SourceProjectPath = null
);

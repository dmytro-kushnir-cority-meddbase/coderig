namespace Rig.Domain.Data;

public sealed record AnalysisResult(
    string SolutionPath,
    IReadOnlyList<SourceFileInfo> SourceFiles,
    IReadOnlyList<DiRegistrationInfo> DiRegistrations,
    string? ProjectIdentity = null,
    string? SourceProjectPath = null,
    IReadOnlyList<SymbolFact>? Symbols = null,
    IReadOnlyList<ReferenceFact>? References = null,
    IReadOnlyList<TypeRelationFact>? TypeRelations = null,
    IReadOnlyList<DispatchFact>? DispatchFacts = null,
    IReadOnlyList<AllocationFact>? AllocationFacts = null
);

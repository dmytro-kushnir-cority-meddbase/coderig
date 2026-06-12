namespace Rig.Domain.Data;

public sealed record AnalysisResult(
    string SolutionPath,
    IReadOnlyList<SourceFileInfo> SourceFiles,
    // DI registrations (code-detected + XML service descriptors + static rule mappings), stored as a
    // run-agnostic fact and surfaced by `rig di`.
    IReadOnlyList<DiRegistrationInfo> DiRegistrations,
    // Optional: stable identity grouping incremental per-project runs of the same solution (provenance).
    string? ProjectIdentity = null,
    // Which specific project was indexed (csproj path).  Null for solution-level runs.
    string? SourceProjectPath = null,
    // Stage-1 rule-agnostic facts (symbol + reference index). See docs/fact-layer-refactor.md.
    IReadOnlyList<SymbolFact>? Symbols = null,
    IReadOnlyList<ReferenceFact>? References = null,
    IReadOnlyList<TypeRelationFact>? TypeRelations = null,
    // Exact Roslyn-mined member-level dispatch edges (override + interface impl) — see DispatchFact.
    IReadOnlyList<DispatchFact>? DispatchFacts = null
);

using Microsoft.CodeAnalysis;
using Rig.Domain.Data;

namespace Rig.Analysis;

internal sealed record SourceFileClassification(string Status, string Confidence, string Basis, string Reason, string Evidence);

internal sealed record SolutionSourceSet(
    IReadOnlyList<SourceFileInfo> SourceFiles,
    IReadOnlyList<SourceModel> IndexedSources,
    IReadOnlyList<string> ProjectDirectories
);

internal sealed record SourceExtractionResult(
    IReadOnlyList<DiRegistrationInfo> DiRegistrations,
    IReadOnlyList<SymbolFact> Symbols,
    IReadOnlyList<ReferenceFact> References,
    IReadOnlyList<TypeRelationFact> TypeRelations,
    IReadOnlyList<DispatchFact> Dispatch
);

internal sealed record SourceModel(string ProjectName, string FilePath, SyntaxTree Tree, SyntaxNode Root, SemanticModel SemanticModel);

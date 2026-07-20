using Microsoft.CodeAnalysis;
using Rig.Domain.Data;

namespace Rig.Analysis;

internal sealed record SourceFileClassification(string Status, string Confidence, string Basis, string Reason, string Evidence);

// Workspace is carried out so the caller can Dispose it the moment extraction is done — it owns the
// AdhocWorkspace's SolutionCompilationState, which (via the incremental source-generator DriverStateTable)
// pins every Compilation + SemanticModel for the rest of the run. Disposing it after extract releases that
// ~multi-GB graph before the save/graph phases instead of holding it to process exit.
internal sealed record SolutionSourceSet(IReadOnlyList<SourceFileInfo> SourceFiles, IReadOnlyList<SourceModel> IndexedSources);

internal sealed record SourceExtractionResult(
    IReadOnlyList<DiRegistrationInfo> DiRegistrations,
    IReadOnlyList<SymbolFact> Symbols,
    IReadOnlyList<ReferenceFact> References,
    IReadOnlyList<TypeRelationFact> TypeRelations,
    IReadOnlyList<DispatchFact> Dispatch,
    IReadOnlyList<AllocationFact> Allocations
);

internal sealed record SourceModel(string ProjectName, string FilePath, SyntaxTree Tree, SyntaxNode Root, SemanticModel SemanticModel);

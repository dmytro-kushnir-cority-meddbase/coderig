using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal sealed record SourceFileClassification(
    string Status,
    string Confidence,
    string Basis,
    string Reason,
    string Evidence);

internal sealed record SolutionSourceSet(
    IReadOnlyList<SourceFileInfo> SourceFiles,
    IReadOnlyList<SourceModel> IndexedSources,
    IReadOnlyList<string> ProjectDirectories);

internal sealed record SourceExtractionResult(
    IReadOnlyList<EntryPointInfo> EntryPoints,
    IReadOnlyList<EffectInfo> Effects,
    IReadOnlyList<DiRegistrationInfo> DiRegistrations,
    IReadOnlyList<MethodObservationInfo> MethodObservations,
    IReadOnlyList<InvocationObservationInfo> InvocationObservations);

internal sealed record SourceModel(
    string ProjectName,
    string FilePath,
    SyntaxTree Tree,
    SyntaxNode Root,
    SemanticModel SemanticModel);

internal sealed record MethodModel(
    string Key,
    string Symbol,
    string FilePath,
    int Line,
    MethodDeclarationSyntax Body,
    SemanticModel SemanticModel,
    IReadOnlyList<EffectInfo> Effects);

internal sealed record ResolvedCall(string Key, string DisplayName, int Line = 0);

internal sealed record ResolvedCallSet(
    IReadOnlyList<ResolvedCall> Application,
    IReadOnlyList<BoundaryCallInfo> Boundary);

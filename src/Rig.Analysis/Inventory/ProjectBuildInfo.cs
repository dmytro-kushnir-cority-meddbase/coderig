using Buildalyzer;

namespace Rig.Analysis.Inventory;

// The slice of a project's design-time build that BuildWorkspaceFromResults actually consumes — the
// resolved references, source files, and MSBuild properties needed to construct a Roslyn project. It is
// deliberately a plain, serializable record (no Buildalyzer types) so the same workspace assembly can be
// driven from EITHER a fresh design-time build (FromAnalyzerResult) OR a cached/replayed result. That
// decoupling is the prerequisite for the design-time-build cache (skip the ~33-53% build phase when a
// project's inputs are unchanged); on its own this type changes no behaviour.
public sealed record ProjectBuildInfo(
    string? ProjectFilePath,
    IReadOnlyList<string> References,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> AnalyzerReferences,
    IReadOnlyList<string> PreprocessorSymbols,
    IReadOnlyDictionary<string, string> Properties
)
{
    // Projects the consumed fields out of Buildalyzer's IAnalyzerResult, normalising nullable
    // collections to empty so downstream code never null-checks.
    public static ProjectBuildInfo FromAnalyzerResult(IAnalyzerResult result) =>
        new(
            ProjectFilePath: result.ProjectFilePath,
            References: result.References?.ToArray() ?? [],
            ProjectReferences: result.ProjectReferences?.ToArray() ?? [],
            SourceFiles: result.SourceFiles?.ToArray() ?? [],
            AnalyzerReferences: result.AnalyzerReferences?.ToArray() ?? [],
            PreprocessorSymbols: result.PreprocessorSymbols?.ToArray() ?? [],
            Properties: result.Properties ?? new Dictionary<string, string>(StringComparer.Ordinal)
        );
}

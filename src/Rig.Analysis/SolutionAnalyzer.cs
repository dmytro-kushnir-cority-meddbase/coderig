using Rig.Analysis.CallGraph;
using Rig.Analysis.Extraction;
using Rig.Analysis.Inventory;
using Rig.Analysis.Rules;
using Rig.Domain.Data;

namespace Rig.Analysis;

public static class SolutionAnalyzer
{
    public static async Task<AnalysisResult> AnalyzeAsync(
        string solutionPath,
        CancellationToken cancellationToken = default,
        Action<string>? progress = null,
        IReadOnlyList<string>? extraRulesPaths = null
    )
    {
        var solutionFullPath = Path.GetFullPath(solutionPath);
        progress?.Invoke("Loading rules");
        var rules = AnalysisRuleSet.LoadForSolution(solutionFullPath, extraRulesPaths);
        progress?.Invoke("Loading solution");
        var sourceSet = await SolutionSourceLoader.LoadAsync(solutionFullPath, rules, cancellationToken, progress);
        progress?.Invoke("Merging project rules");
        rules = rules.MergeWithProjectDirectories(sourceSet.ProjectDirectories);
        var sources = sourceSet.IndexedSources;

        progress?.Invoke($"Extracting observations from {sources.Count} indexed source files");
        var extracted = 0;
        var extractionResults = sources
            .AsParallel()
            .AsOrdered()
            .Select(source =>
            {
                var result = ExtractSource(source, rules);
                var current = Interlocked.Increment(ref extracted);
                if (ShouldReportProgress(current, sources.Count))
                {
                    progress?.Invoke($"Extracted {current}/{sources.Count} source files");
                }
                return result;
            })
            .ToArray();

        progress?.Invoke("Building projections");
        var entryPoints = extractionResults.SelectMany(result => result.EntryPoints).ToArray();
        var effects = extractionResults.SelectMany(result => result.Effects).ToArray();
        var diRegistrations = extractionResults.SelectMany(result => result.DiRegistrations).ToArray();
        var methodObservations = extractionResults
            .SelectMany(result => result.MethodObservations)
            .OrderBy(observation => observation.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(observation => observation.Line)
            .ToArray();
        var invocationObservations = extractionResults
            .SelectMany(result => result.InvocationObservations)
            .OrderBy(observation => observation.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(observation => observation.Line)
            .ToArray();

        progress?.Invoke($"Building callgraphs for {entryPoints.Length} entrypoints");
        var callGraphs = CallGraphBuilder.Build(
            entryPoints,
            sources,
            effects,
            rules.Effects.Where(r => r.TreatAsDispatch).ToArray(),
            diRegistrations
        );

        progress?.Invoke($"Analysis complete: {entryPoints.Length} entrypoints, {effects.Length} effects");
        return new AnalysisResult(
            solutionPath,
            sourceSet.SourceFiles,
            entryPoints,
            effects,
            diRegistrations,
            callGraphs,
            methodObservations,
            invocationObservations
        );
    }

    private static bool ShouldReportProgress(int current, int total)
    {
        return current == 1 || current == total || current % 100 == 0;
    }

    private static SourceExtractionResult ExtractSource(SourceModel source, AnalysisRuleSet rules)
    {
        var entryPoints = EntryPointExtractor
            .FindMinimalApiEntryPoints(source, rules)
            .Concat(EntryPointExtractor.FindMvcEntryPoints(source, rules))
            .Concat(EntryPointExtractor.FindClassInheritanceEntryPoints(source, rules))
            .ToArray();

        return new SourceExtractionResult(
            entryPoints,
            EffectExtractor.FindEffects(source, rules).ToArray(),
            DiRegistrationExtractor.FindDiRegistrations(source, rules).ToArray(),
            RoslynObservationExtractor.FindMethodObservations(source).ToArray(),
            RoslynObservationExtractor.FindInvocationObservations(source).ToArray()
        );
    }
}

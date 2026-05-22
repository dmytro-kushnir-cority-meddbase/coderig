namespace Rig.Analysis;

public static class SolutionAnalyzer
{
    public static async Task<AnalysisResult> AnalyzeAsync(
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        var solutionFullPath = Path.GetFullPath(solutionPath);
        var rules = AnalysisRuleSet.LoadForSolution(solutionFullPath);
        var sourceSet = await SolutionSourceLoader.LoadAsync(solutionFullPath, rules, cancellationToken);
        var sources = sourceSet.IndexedSources;

        var entryPoints = new List<EntryPointInfo>();
        var effects = new List<EffectInfo>();
        var diRegistrations = new List<DiRegistrationInfo>();

        foreach (var source in sources)
        {
            entryPoints.AddRange(EntryPointExtractor.FindMinimalApiEntryPoints(source, rules));
            entryPoints.AddRange(EntryPointExtractor.FindMvcEntryPoints(source, rules));
            effects.AddRange(EffectExtractor.FindEffects(source, rules));
            diRegistrations.AddRange(DiRegistrationExtractor.FindDiRegistrations(source, rules));
        }

        var methodObservations = sources
            .SelectMany(RoslynObservationExtractor.FindMethodObservations)
            .OrderBy(observation => observation.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(observation => observation.Line)
            .ToArray();

        var invocationObservations = sources
            .SelectMany(RoslynObservationExtractor.FindInvocationObservations)
            .OrderBy(observation => observation.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(observation => observation.Line)
            .ToArray();

        var callGraphs = CallGraphBuilder.Build(entryPoints, sources, effects);

        return new AnalysisResult(
            sourceSet.SourceFiles,
            entryPoints,
            effects,
            diRegistrations,
            callGraphs,
            methodObservations,
            invocationObservations);
    }
}

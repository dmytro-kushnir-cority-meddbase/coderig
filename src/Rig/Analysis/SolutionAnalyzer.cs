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
        rules = rules.MergeWithProjectDirectories(sourceSet.ProjectDirectories);
        var sources = sourceSet.IndexedSources;

        var extractionResults = sources
            .AsParallel()
            .AsOrdered()
            .Select(source => ExtractSource(source, rules))
            .ToArray();

        var entryPoints = extractionResults
            .SelectMany(result => result.EntryPoints)
            .ToArray();
        var effects = extractionResults
            .SelectMany(result => result.Effects)
            .ToArray();
        var diRegistrations = extractionResults
            .SelectMany(result => result.DiRegistrations)
            .ToArray();
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

        var callGraphs = CallGraphBuilder.Build(entryPoints, sources, effects, rules.Effects.Where(r => r.TreatAsDispatch).ToArray(), diRegistrations);

        return new AnalysisResult(
            solutionPath,
            sourceSet.SourceFiles,
            entryPoints,
            effects,
            diRegistrations,
            callGraphs,
            methodObservations,
            invocationObservations);
    }

    private static SourceExtractionResult ExtractSource(SourceModel source, AnalysisRuleSet rules)
    {
        var entryPoints = EntryPointExtractor.FindMinimalApiEntryPoints(source, rules)
            .Concat(EntryPointExtractor.FindMvcEntryPoints(source, rules))
            .Concat(EntryPointExtractor.FindClassInheritanceEntryPoints(source, rules))
            .ToArray();

        return new SourceExtractionResult(
            entryPoints,
            EffectExtractor.FindEffects(source, rules).ToArray(),
            DiRegistrationExtractor.FindDiRegistrations(source, rules).ToArray(),
            RoslynObservationExtractor.FindMethodObservations(source).ToArray(),
            RoslynObservationExtractor.FindInvocationObservations(source).ToArray());
    }
}

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
        IReadOnlyList<string>? extraRulesPaths = null,
        string? projectIdentity = null,
        // When non-null, restrict the solution index to this set of project paths (the entry-project
        // closure from `rig index --from`); still ONE cross-project Roslyn workspace / run.
        IReadOnlySet<string>? scopeProjectPaths = null,
        // Max concurrent design-time builds / compilations (null = conservative default).
        int? parallelism = null,
        // Drop test projects (by name convention) from the indexed set — --no-tests.
        bool excludeTests = false
    )
    {
        var solutionFullPath = Path.GetFullPath(solutionPath);
        progress?.Invoke("Loading rules");
        var rules = AnalysisRuleSet.LoadForSolution(solutionFullPath, extraRulesPaths);
        progress?.Invoke("Loading solution");
        var sourceSet = await SolutionSourceLoader.LoadAsync(
            solutionFullPath,
            rules,
            cancellationToken,
            progress,
            scopeProjectPaths,
            parallelism,
            excludeTests
        );
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
        var diRegistrations = extractionResults.SelectMany(result => result.DiRegistrations).ToArray();
        var symbolFacts = extractionResults.SelectMany(result => result.Symbols).ToArray();
        var referenceFacts = extractionResults.SelectMany(result => result.References).ToArray();
        var typeRelationFacts = extractionResults.SelectMany(result => result.TypeRelations).ToArray();
        var dispatchFacts = extractionResults.SelectMany(result => result.Dispatch).ToArray();

        // Mine XML service descriptor files (e.g. App_Data/Common/Xml/Services/*.xml) and
        // any inline static mappings, then merge with code-detected DI registrations.
        var xmlRegistrations = XmlDiMiner.Mine(rules);
        var staticRegistrations = rules.StaticDiMappings.Select(m => new DiRegistrationInfo(
            m.ServiceType,
            m.ImplementationType,
            m.Lifetime,
            m.RegistrationKind,
            string.Empty,
            0,
            "high",
            "rules",
            "static_di_mapping",
            string.Empty
        ));
        var allDiRegistrations = diRegistrations.Concat(xmlRegistrations).Concat(staticRegistrations).ToArray();
        if (xmlRegistrations.Count > 0)
            progress?.Invoke($"XML DI miner: {xmlRegistrations.Count} mappings from {rules.XmlDiFiles.Count} path(s)");

        progress?.Invoke(
            $"Analysis complete: {symbolFacts.Length} symbols, "
                + $"{referenceFacts.Length} references, {allDiRegistrations.Length} di registrations"
        );

        // For project-level indexing, record the specific project path
        var sourceProjectPath =
            solutionFullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || solutionFullPath.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                ? solutionFullPath
                : null;

        return new AnalysisResult(
            solutionPath,
            sourceSet.SourceFiles,
            allDiRegistrations,
            ProjectIdentity: projectIdentity,
            SourceProjectPath: sourceProjectPath,
            Symbols: symbolFacts,
            References: referenceFacts,
            TypeRelations: typeRelationFacts,
            DispatchFacts: dispatchFacts
        );
    }

    private static bool ShouldReportProgress(int current, int total)
    {
        return current == 1 || current == total || current % 100 == 0;
    }

    private static SourceExtractionResult ExtractSource(SourceModel source, AnalysisRuleSet rules)
    {
        var facts = FactExtractor.Extract(source);

        return new SourceExtractionResult(
            DiRegistrationExtractor.FindDiRegistrations(source, rules).ToArray(),
            facts.Symbols,
            facts.References,
            facts.TypeRelations,
            facts.Dispatch
        );
    }
}

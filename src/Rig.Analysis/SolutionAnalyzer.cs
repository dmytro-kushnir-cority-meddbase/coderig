using System.Diagnostics;
using Rig.Analysis.Extraction;
using Rig.Analysis.Inventory;
using Rig.Analysis.Rules;
using Rig.Domain.Data;

namespace Rig.Analysis;

public static class SolutionAnalyzer
{
    public static async Task<AnalysisResult> AnalyzeAsync(
        string solutionPath,
        RuleSet rules,
        CancellationToken cancellationToken = default,
        Action<string>? progress = null,
        string? projectIdentity = null,
        // When non-null, restrict the solution index to this set of project paths (the entry-project
        // closure from `rig index --from`); still ONE cross-project Roslyn workspace / run.
        IReadOnlySet<string>? scopeProjectPaths = null,
        // Max concurrent design-time builds / compilations (null = conservative default).
        int? parallelism = null,
        // Drop test projects (by name convention) from the indexed set (the index default).
        bool excludeTests = false,
        // Optional per-phase timing collector (rig index --time). Records rules-load, extract, and
        // projections here; the loader records workspace-build / wire-generators / compile+read.
        PhaseTimings? timings = null,
        // Directory for the design-time-build cache (rig index --reuse-build-cache). Null = disabled.
        string? buildCacheDir = null,
        // --verify-build-cache: build everything ignoring hits and diff fresh vs cached, reporting mismatches.
        bool verifyBuildCache = false
    )
    {
        var solutionFullPath = Path.GetFullPath(solutionPath);
        var phase = timings is null ? null : Stopwatch.StartNew();

        progress?.Invoke("Loading solution");
        var sourceSet = await SolutionSourceLoader.LoadAsync(
            solutionPath: solutionFullPath,
            rules: rules,
            cancellationToken: cancellationToken,
            progress: progress,
            scopeProjectPaths: scopeProjectPaths,
            parallelism: parallelism,
            excludeTests: excludeTests,
            timings: timings,
            buildCacheDir: buildCacheDir,
            verifyBuildCache: verifyBuildCache
        );
        // Start the extraction clock fresh after the loader's phases so it isn't double-counted.
        phase?.Restart();
        var sources = sourceSet.IndexedSources;

        progress?.Invoke($"Extracting observations from {sources.Count} indexed source files");

        // Parallel.For into pre-allocated slots (NOT AsParallel().AsOrdered()): writing result[i] for
        // source[i] keeps the output deterministic by input position — which the FactIndex surrogate keys
        // depend on — WITHOUT PLINQ's order-preserving merge, which buffers/reorders completed results and
        // added synchronization + retained-memory overhead on the hot extract path. Distinct slots per
        // iteration, so no write races.
        var extracted = 0;
        var extractionResults = new SourceExtractionResult[sources.Count];
        Parallel.For(
            fromInclusive: 0,
            toExclusive: sources.Count,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism ?? Environment.ProcessorCount },
            i =>
            {
                extractionResults[i] = ExtractSource(sources[i], rules);
                var current = Interlocked.Increment(ref extracted);
                if (ShouldReportProgress(current: current, total: sources.Count))
                {
                    progress?.Invoke($"Extracted {current}/{sources.Count} source files");
                }
            }
        );

        if (phase is not null)
        {
            timings!.Record("extract", phase.Elapsed);
            phase.Restart();
        }

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
            ServiceType: m.ServiceType,
            ImplementationType: m.ImplementationType,
            Lifetime: m.Lifetime,
            RegistrationKind: m.RegistrationKind,
            FilePath: string.Empty,
            Line: 0,
            Confidence: "high",
            Basis: "rules",
            Reason: "static_di_mapping",
            Evidence: string.Empty
        ));
        var allDiRegistrations = diRegistrations.Concat(xmlRegistrations).Concat(staticRegistrations).ToArray();
        if (phase is not null)
        {
            timings!.Record("projections+xml-di", phase.Elapsed);
        }

        if (xmlRegistrations.Count > 0)
        {
            progress?.Invoke($"XML DI miner: {xmlRegistrations.Count} mappings from {rules.XmlDiFiles.Count} path(s)");
        }

        progress?.Invoke(
            $"Analysis complete: {symbolFacts.Length} symbols, "
                + $"{referenceFacts.Length} references, {allDiRegistrations.Length} di registrations"
        );

        // Memory-profiling pause (RIG_PROFILE_PAUSE): here the Roslyn workspace, every project's
        // compilation, and every file's SemanticModel are STILL ROOTED via sourceSet.IndexedSources,
        // alongside the just-built fact arrays — the true co-resident peak. A gcdump now shows that
        // whole live set. No-op unless the env var is set.
        ProfilingPause.MaybePause("extract-peak (roslyn live)");

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

    private static SourceExtractionResult ExtractSource(SourceModel source, RuleSet rules)
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

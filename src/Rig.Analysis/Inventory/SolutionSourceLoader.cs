using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Xml.Linq;
using Buildalyzer;
using Buildalyzer.Environment;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Rig.Domain.Data;
using ProjectInfo = Microsoft.CodeAnalysis.ProjectInfo;
using RuleSet = Rig.Domain.Data.RuleSet;
using SolutionInfo = Microsoft.CodeAnalysis.SolutionInfo;

namespace Rig.Analysis.Inventory;

internal static class SolutionSourceLoader
{
    // Default to the full CPU count. The design-time builds run out-of-process (UseSharedCompilation
    // =false) and emit no binaries, and the in-process Roslyn compile/read loops are CPU-bound, so a
    // big solution saturates every core rather than leaving most idle at a fixed cap. Override with
    // --parallelism <n> (e.g. lower it if memory-bound on a very large workspace).
    private static readonly int DefaultParallelism = Math.Max(val1: 1, val2: Environment.ProcessorCount);

    // How many times a 0-source design-time build is retried before the index aborts. 0-source builds are
    // usually a transient race (obj flush, MSBuild server hiccup) that clears on a fresh build; a couple of
    // retries catches that, and anything still degraded after is treated as a hard, deterministic failure.
    private const int DegradedBuildRetries = 2;

    public static async Task<SolutionSourceSet> LoadAsync(
        string solutionPath,
        RuleSet rules,
        CancellationToken cancellationToken,
        Action<string>? progress = null,
        // When non-null, only projects whose normalised full path is in this set are built and
        // indexed — the transitive ProjectReference closure of an entry project (rig index --from).
        // Everything else in the solution (test projects, unrelated tools) is skipped before its
        // expensive design-time build runs. Null = whole solution (the historical behaviour).
        IReadOnlySet<string>? scopeProjectPaths = null,
        // Max concurrent MSBuild design-time builds / Roslyn compilations. Null = DefaultParallelism
        // (the CPU count). The design-time builds run out-of-process with UseSharedCompilation=false
        // and emit no binaries, so concurrency is safe — this is the dominant indexing cost.
        int? parallelism = null,
        // Explicit TFM selected for multi-targeted projects. Null preserves the historical first-declared-TFM default.
        string? framework = null,
        // Drop test projects (by name convention) before their design-time build — --no-tests.
        bool excludeTests = false,
        // Optional per-phase timing collector (rig index --time). Records workspace-build,
        // wire-generators, and the fused compile+read pass. Null = no timing.
        PhaseTimings? timings = null,
        // Directory for the design-time-build cache (rig index --reuse-build-cache). Null = disabled.
        string? buildCacheDir = null,
        // --verify-build-cache: build everything ignoring hits and diff fresh vs cached, reporting mismatches.
        bool verifyBuildCache = false
    )
    {
        var maxParallelism = Math.Max(val1: 1, val2: parallelism ?? DefaultParallelism);
        var phase = timings is null ? null : Stopwatch.StartNew();

        // Buildalyzer invokes MSBuild.exe out-of-process, completely avoiding the
        // System.Collections.Immutable assembly conflict in Roslyn's BuildHost-net472 that
        // causes TypeInitializationException on XMakeElements under VS18/MSBuild18 when
        // loading old-style ToolsVersion="4.0" projects. The crash is specific to the
        // MSBuildWorkspace in-process loader — plain msbuild.exe is unaffected, which is why
        // this out-of-process path works. (Roslyn PR #83477 merged 2026-05-07 for milestone
        // 18.8; NOT in our pinned Microsoft.CodeAnalysis 5.3.0 (~18.3), and still reported in
        // released SDK 10.0.200 — see dotnet/roslyn#82931, dotnet/sdk#53383. Revisit a
        // MSBuildWorkspace switch only after upgrading onto a shipped 18.8+.)
        // addProjectReferences:false loads project-to-project references from their compiled
        // DLLs rather than re-evaluating the source .csproj files.
        ReportProgress(progress, "Loading solution");
        var workspace = BuildWorkspace(
            solutionPath,
            rules,
            progress,
            scopeProjectPaths,
            maxParallelism,
            framework,
            excludeTests,
            timings,
            buildCacheDir,
            verifyBuildCache
        );
        // BuildWorkspace records the finer "design-time-builds" (wall-clock) + "workspace-assembly"
        // sub-phases itself; just reset the clock here for the next phase.
        phase?.Restart();

        // Wire OutputItemType="Analyzer" ProjectReferences (source generators like the ClientPage
        // proxy generator) that Buildalyzer drops: emit each generator project's compilation to a temp
        // DLL and add it as an analyzer reference on the referencing project, so RunSourceGenerators
        // can execute it and the generated types get indexed.
        await WireGeneratorAnalyzersAsync(workspace, progress, cancellationToken);
        if (phase is not null)
        {
            timings!.Record("wire-generators", phase.Elapsed);
            phase.Restart();
        }

        var workspaceCSharpProjects = workspace
            .CurrentSolution.Projects.Where(p => p.Language == LanguageNames.CSharp)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        var excludedByRules = workspaceCSharpProjects.Where(p => rules.IsExcludedProject(p.Name)).Select(p => p.Name).ToArray();
        if (excludedByRules.Length > 0)
        {
            ReportProgress(
                progress,
                $"Excluding {excludedByRules.Length} workspace project(s) by rules (projects.exclude): {FormatProjectList(excludedByRules)}"
            );
        }

        var csharpProjects = workspaceCSharpProjects.Where(p => !rules.IsExcludedProject(p.Name)).ToArray();

        ReportProgress(progress, $"Loaded {csharpProjects.Length} C# project(s) to index");

        // Empty closure guard: a `--from <csproj>` whose entry-closure resolves to 0 buildable C# projects
        // (or whose only projects are excluded) would otherwise crash at `csharpProjects[0]` below with an
        // unhandled IndexOutOfRangeException. Fail cleanly instead.
        EnsureIndexableProjects(csharpProjects.Length);

        // ONE per-project pass: compile, collect error diagnostics, and read the project's sources
        // against that SAME compilation — held alive (GC.KeepAlive) for the whole task so Roslyn's
        // bounded compilation cache can't evict it between the diagnostics bind and the per-document
        // semantic models. The old split compile-then-read passes traversed all projects twice; on a
        // large closure (MedDBase: 123 projects) the first pass's compilations are evicted before the
        // second pass reaches them, forcing a rebuild — the read pass measured ~as costly as the compile
        // pass for exactly this reason. Fusing makes it build-once and drops the barrier between passes.
        var compilationErrors = new ConcurrentBag<string>();
        var projectResults = new ConcurrentBag<ProjectSourceLoadResult>();
        var analyzedProjects = 0;
        // Per-project compile/diagnostics/read seconds (only when timing). Σ far above wall/parallelism, or a
        // few projects dominating getCompilation, is the fingerprint of shared-dependency compilations being
        // evicted and REBUILT across the parallel traversal — reported after the phase, like the build summary.
        var perProjectCompile = timings is null
            ? null
            : new ConcurrentBag<(string Name, double CompileSec, double DiagSec, double ReadSec)>();

        var first = csharpProjects[0];
        var rest = csharpProjects.Skip(1);

        // let roslyn bootstrap without races
        await ProcessProject(first, cancellationToken);

        await Parallel.ForEachAsync(
            rest,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = parallelism ?? DefaultParallelism },
            ProcessProject
        );

        if (phase is not null)
        {
            var compileReadWall = phase.Elapsed;
            timings!.Record("compile+read", compileReadWall);
            ReportCompileSummary(progress, perProjectCompile!, compileReadWall, parallelism ?? DefaultParallelism);
        }

        var compilationErrorList = compilationErrors.OrderBy(e => e, StringComparer.Ordinal).ToArray();

        if (compilationErrorList.Length > 0)
        {
            // Report compilation errors as warnings and continue with partial analysis.
            // Design-time builds commonly miss code-generated types (proxy generators, T4 templates,
            // source generators).  The semantic model is still valid for code that doesn't reference
            // the missing types, so entry point and effect extraction can proceed.
            var errorCount = compilationErrorList.Length;
            ReportProgress(progress, $"Warning: {errorCount} compilation error(s) — analysis will be partial for affected files");

            foreach (var error in compilationErrorList.Take(10))
            {
                ReportProgress(progress, $"  {error}");
            }

            if (errorCount > 10)
            {
                ReportProgress(progress, $"  ... and {errorCount - 10} more (set --verbose to see all)");
            }
        }

        return new SolutionSourceSet(
            projectResults.SelectMany(r => r.SourceFiles).OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).ToList(),
            projectResults.SelectMany(r => r.Sources).OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase).ToList()
        );

        async ValueTask ProcessProject(Project project, CancellationToken ct)
        {
            // One reused stopwatch split across the three sub-steps (only when timing) — getCompilation is
            // the rebuild-prone one (its cost balloons if a shared dependency's compilation was evicted).
            var watch = perProjectCompile is null ? null : Stopwatch.StartNew();
            var compilation = await project.GetCompilationAsync(ct);
            var compileSec = watch?.Elapsed.TotalSeconds ?? 0;
            if (compilation is null)
            {
                compilationErrors.Add($"{project.Name}: compilation unavailable");
                return; // no semantic model possible — nothing to read for this project
            }

            watch?.Restart();
            foreach (var diagnostic in compilation.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                compilationErrors.Add($"{project.Name}: {diagnostic}");
                Console.WriteLine($"{project.Name}: {diagnostic}");
            }

            var diagSec = watch?.Elapsed.TotalSeconds ?? 0;
            watch?.Restart();
            projectResults.Add(
                await LoadProjectSourcesAsync(
                    solutionPath: solutionPath,
                    project: project,
                    compilation: compilation,
                    rules: rules,
                    cancellationToken: ct
                )
            );
            perProjectCompile?.Add((project.Name, compileSec, diagSec, watch?.Elapsed.TotalSeconds ?? 0));

            var current = Interlocked.Increment(ref analyzedProjects);
            ReportProgress(progress, $"Analyzed project {current}/{csharpProjects.Length}: {project.Name}");
        }
    }

    private static AdhocWorkspace BuildWorkspace(
        string solutionPath,
        RuleSet rules,
        Action<string>? progress,
        IReadOnlySet<string>? scopeProjectPaths,
        int parallelism,
        string? framework,
        bool excludeTests,
        PhaseTimings? timings = null,
        string? buildCacheDir = null,
        // --verify-build-cache: build EVERY project (ignore hits) and diff fresh vs cached ProjectBuildInfo,
        // reporting mismatches. The completeness guardrail; requires buildCacheDir to be set.
        bool verifyBuildCache = false
    )
    {
        var logWriter = progress is null ? null : new ProgressLogWriter(progress);
        var options = new AnalyzerManagerOptions { LogWriter = logWriter };

        // Design-time-build cache (rig index --reuse-build-cache). On a fingerprint hit the (dominant)
        // out-of-process build is skipped and the cached ProjectBuildInfo is replayed. Null = disabled.
        var cache = buildCacheDir is null ? null : new BuildResultCache(buildCacheDir, framework);
        var cacheHits = 0;
        var cacheMisses = 0;

        // --verify-build-cache tallies: a project a HIT would have served whose fresh build MATCHED the cached
        // output, one that MISMATCHED (a latent stale-hit — the fingerprint is under-specified), and one with
        // no matching sidecar to check against (cold/changed — nothing to verify this run).
        var verifyMatches = 0;
        var verifyMismatches = 0;
        var verifyNoBaseline = 0;

        // Per project: fingerprint → cache hit (skip the build) or miss (build, convert, store). With the
        // cache off it's just build + convert. The fingerprint reads no file contents (see
        // BuildInputFingerprint), so this is cheap relative to the build it may skip.
        // Functional-core/imperative-shell over the design-time-build cache, per project:
        //   PREPARE (impure): compute the input fingerprint (Gather→Of) + read the sidecar (Load).
        //   DECIDE  (pure):   BuildCacheDecision.Decide — hit (replay) or miss (rebuild under this fingerprint).
        //   COMMIT  (impure): on hit return the cached output; on miss build (BuildChecked) and Store.
        // Cache off → straight to BuildChecked, no fingerprint, no sidecar.
        ProjectBuildInfo? BuildOrLoad(string projectFilePath, Func<IAnalyzerResult?> build)
        {
            if (cache is null)
            {
                return BuildChecked(projectFilePath, build);
            }

            // VERIFY: build EVERY project (never trust a hit) and diff fresh vs what a hit would have replayed.
            // Catches an under-specified fingerprint that no unit test can — then refresh the sidecar.
            if (verifyBuildCache)
            {
                return VerifyAndBuild(projectFilePath, build);
            }

            // PREPARE
            var fingerprint = BuildInputFingerprint.Compute(projectFilePath);
            var stored = cache.Load(projectFilePath);

            // DECIDE
            var decision = BuildCacheDecision.Decide(currentFingerprint: fingerprint, stored: stored);

            // COMMIT
            if (decision is BuildCacheDecision.Hit hit)
            {
                Interlocked.Increment(ref cacheHits);
                return hit.Info;
            }

            var info = BuildChecked(projectFilePath, build);
            if (info is null)
            {
                return null;
            }

            cache.Store(projectFilePath: projectFilePath, fingerprint: fingerprint, info: info);
            Interlocked.Increment(ref cacheMisses);
            return info;
        }

        // The build EFFECT (impure): run the design-time build, convert, and retry a degraded (0-source-file)
        // build a bounded number of times before failing the index — a degraded build would drop the project's
        // types and corrupt dependents. Shared by the cache-miss and cache-off paths.
        ProjectBuildInfo? BuildChecked(string projectFilePath, Func<IAnalyzerResult?> build)
        {
            var built = build();
            if (built is null)
            {
                return null;
            }

            var info = ProjectBuildInfo.FromAnalyzerResult(built);
            var name = Path.GetFileNameWithoutExtension(projectFilePath);

            for (var attempt = 1; IsDegradedBuild(info) && attempt <= DegradedBuildRetries; attempt++)
            {
                ReportProgress(
                    progress,
                    $"WARN: '{name}' design-time build produced 0 source files — retrying ({attempt}/{DegradedBuildRetries})"
                );
                var retried = build();
                if (retried is null)
                {
                    break;
                }

                info = ProjectBuildInfo.FromAnalyzerResult(retried);
            }

            if (IsDegradedBuild(info))
            {
                throw new DegradedBuildException(
                    $"'{name}' design-time build produced 0 source files after {DegradedBuildRetries + 1} attempt(s) — "
                        + "its types would be absent and dependents would fail to bind, corrupting the index. "
                        + $"Re-run `dotnet restore` / `dotnet build` for {name}, then re-index."
                );
            }

            return info;
        }

        // --verify-build-cache (impure): build fresh regardless, then — if a hit WOULD have been served — diff
        // the fresh output against the cached one and tally match / mismatch; refresh the sidecar either way.
        // A mismatch is the signal that matters: the fingerprint missed a build-affecting input (latent stale).
        ProjectBuildInfo? VerifyAndBuild(string projectFilePath, Func<IAnalyzerResult?> build)
        {
            var fingerprint = BuildInputFingerprint.Compute(projectFilePath);
            var stored = cache!.Load(projectFilePath);
            var fresh = BuildChecked(projectFilePath, build);
            if (fresh is null)
            {
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(projectFilePath);
            if (BuildCacheDecision.Decide(currentFingerprint: fingerprint, stored: stored) is BuildCacheDecision.Hit hit)
            {
                var comparison = BuildInfoEquivalence.Compare(fresh: fresh, cached: hit.Info);
                if (comparison.IsEquivalent)
                {
                    Interlocked.Increment(ref verifyMatches);
                }
                else
                {
                    Interlocked.Increment(ref verifyMismatches);
                    ReportProgress(progress, $"BUILD-CACHE VERIFY MISMATCH: '{name}': {comparison.Summary}");
                }
            }
            else
            {
                Interlocked.Increment(ref verifyNoBaseline); // no matching sidecar (cold/changed) — nothing to verify
            }

            cache.Store(projectFilePath: projectFilePath, fingerprint: fingerprint, info: fresh);
            return fresh;
        }

        List<ProjectBuildInfo> results;
        if (IsProjectFile(solutionPath))
        {
            results = BuildSingleProjectResults(solutionPath, progress, options, timings, framework, BuildOrLoad);
        }
        else
        {
            results = BuildSolutionProjectResults(
                solutionPath,
                rules,
                progress,
                options,
                scopeProjectPaths,
                parallelism,
                framework,
                excludeTests,
                timings,
                BuildOrLoad
            );
        }

        ReportCacheStats(
            progress,
            results,
            cache,
            verifyBuildCache,
            verifyMatches,
            verifyMismatches,
            verifyNoBaseline,
            cacheHits,
            cacheMisses
        );

        ReportProgress(progress, $"Assembling workspace from {results.Count} project(s)");
        var assemblyWatch = timings is null ? null : Stopwatch.StartNew();
        var workspace = BuildWorkspaceFromResults(results, parallelism);
        if (assemblyWatch is not null)
        {
            timings!.Record("workspace-assembly", assemblyWatch.Elapsed);
        }

        return workspace;
    }

    // Runs the design-time build for a single .csproj / .fsproj path and returns its one result.
    // Used when `rig index` is given a project file directly rather than a solution.
    private static List<ProjectBuildInfo> BuildSingleProjectResults(
        string projectFilePath,
        Action<string>? progress,
        AnalyzerManagerOptions options,
        PhaseTimings? timings,
        string? framework,
        Func<string, Func<IAnalyzerResult?>, ProjectBuildInfo?> buildOrLoad
    )
    {
#pragma warning disable CS0618
        var manager = new AnalyzerManager(options);
        var analyzer = manager.GetProject(projectFilePath);
#pragma warning restore CS0618
        progress?.Invoke($"MSBuild: running design-time build for {Path.GetFileNameWithoutExtension(projectFilePath)}");
        analyzer!.SetGlobalProperty(key: "DesignTimeBuild", value: "true");
        analyzer.SetGlobalProperty(key: "BuildingInsideVisualStudio", value: "true");
        // Prevent the MSBuild compiler server from being shared across parallel processes —
        // concurrent Buildalyzer calls can corrupt each other's bin/ output if they share
        // compilation state.
        analyzer.SetGlobalProperty(key: "UseSharedCompilation", value: "false");
        var singleWatch = timings is null ? null : Stopwatch.StartNew();
        var info =
            buildOrLoad(Path.GetFullPath(projectFilePath), () => BuildCompileOnly(analyzer, framework))
            ?? throw new InvalidOperationException($"Buildalyzer produced no build results for '{projectFilePath}'.");
        if (singleWatch is not null)
        {
            timings!.Record("design-time-builds", singleWatch.Elapsed);
        }

        return [info];
    }

    // Runs parallel design-time builds for all in-scope C# projects in a solution file.
    // Filters by scope/test-exclusion before launching builds, then surfaces any DegradedBuildException
    // unwrapped from the AggregateException Parallel.ForEach produces.
    private static List<ProjectBuildInfo> BuildSolutionProjectResults(
        string solutionPath,
        RuleSet rules,
        Action<string>? progress,
        AnalyzerManagerOptions options,
        IReadOnlySet<string>? scopeProjectPaths,
        int parallelism,
        string? framework,
        bool excludeTests,
        PhaseTimings? timings,
        Func<string, Func<IAnalyzerResult?>, ProjectBuildInfo?> buildOrLoad
    )
    {
#pragma warning disable CS0618
        var manager = new AnalyzerManager(solutionPath, options);
#pragma warning restore CS0618
        var candidates = manager
            .Projects.Values.Where(pa =>
                string.Equals(Path.GetExtension(pa.ProjectFile.Path.ToString()), ".csproj", StringComparison.OrdinalIgnoreCase)
            )
            .Where(pa => scopeProjectPaths is null || scopeProjectPaths.Contains(Path.GetFullPath(pa.ProjectFile.Path.ToString())))
            .Where(pa => !excludeTests || !IsTestProjectPath(pa.ProjectFile.Path.ToString()))
            .ToList();

        var rulesExcluded = candidates
            .Select(pa => Path.GetFileNameWithoutExtension(pa.ProjectFile.Path.ToString()))
            .Where(rules.IsExcludedProject)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (rulesExcluded.Length > 0)
        {
            ReportProgress(
                progress,
                $"Excluding {rulesExcluded.Length} project(s) by rules (projects.exclude): {FormatProjectList(rulesExcluded)}"
            );
        }

        var toBuild = candidates
            .Where(pa => !rules.IsExcludedProject(Path.GetFileNameWithoutExtension(pa.ProjectFile.Path.ToString())))
            .ToList();

        ReportScopeProgress(progress, manager, toBuild, scopeProjectPaths, excludeTests);

        // Design-time builds run out-of-process (MSBuild.exe) with UseSharedCompilation=false and
        // build only the `Compile` target (CompileOnlyOptions — non-destructive, see below), so they
        // parallelise safely — and this is the dominant indexing cost, historically run serially.
        // Parallel.ForEach bounds the concurrent MSBuild processes.
        var resultsBag = new ConcurrentBag<ProjectBuildInfo>();
        // Per-project build durations (only when timing). Their SUM is CPU-seconds of building, which
        // is NOT the phase wall-clock — the builds run in parallel, so wall ≈ sum / effective-parallelism.
        // The phase metric stays the wall-clock stopwatch below; this is the separate work/distribution view.
        // (On a cache hit a project's "build" is just the fingerprint + sidecar read, so it shows ~0.)
        var perProject = timings is null ? null : new ConcurrentBag<(string Name, double Seconds)>();
        var done = 0;
        var total = toBuild.Count;
        var buildsWatch = Stopwatch.StartNew();
        try
        {
            Parallel.ForEach(
                toBuild,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(val1: 1, val2: parallelism) },
                projectAnalyzer =>
                {
                    var projectName = projectAnalyzer.ProjectFile.Name;
                    var current = Interlocked.Increment(ref done);
                    if (current == 1 || current == total || current % 10 == 0)
                    {
                        ReportProgress(progress, $"MSBuild: design-time build {current}/{total}: {projectName}");
                    }

                    projectAnalyzer.SetGlobalProperty(key: "DesignTimeBuild", value: "true");
                    projectAnalyzer.SetGlobalProperty(key: "UseSharedCompilation", value: "false");
                    projectAnalyzer.SetGlobalProperty(key: "BuildingInsideVisualStudio", value: "true");
                    var projectWatch = perProject is null ? null : Stopwatch.StartNew();
                    try
                    {
                        var info = buildOrLoad(
                            Path.GetFullPath(projectAnalyzer.ProjectFile.Path.ToString()),
                            () => BuildCompileOnly(projectAnalyzer, framework)
                        );
                        if (info is not null)
                        {
                            resultsBag.Add(info);
                        }
                    }
                    catch (Exception ex) when (ex is not DegradedBuildException and not FrameworkSelectionException)
                    {
                        // A per-project build failure is non-fatal: skip it and carry on. A DegradedBuildException
                        // (0 sources after retries) is the EXCEPTION — it's filtered out here so it propagates.
                        ReportProgress(progress, $"MSBuild: skipping {projectName} — build failed: {ex.Message.Split('\n')[0].Trim()}");
                    }
                    finally
                    {
                        if (projectWatch is not null)
                        {
                            perProject!.Add((projectName, projectWatch.Elapsed.TotalSeconds));
                        }
                    }
                }
            );
        }
        catch (AggregateException aggregate)
        {
            // Parallel.ForEach wraps a thrown DegradedBuildException; surface it UNWRAPPED so the index
            // aborts with the specific "0 source files" message instead of a generic AggregateException.
            var fatal = aggregate.Flatten().InnerExceptions.OfType<DegradedBuildException>().FirstOrDefault();
            if (fatal is not null)
            {
                throw fatal;
            }

            var frameworkFailure = aggregate.Flatten().InnerExceptions.OfType<FrameworkSelectionException>().FirstOrDefault();
            if (frameworkFailure is not null)
            {
                throw frameworkFailure;
            }

            throw;
        }
        buildsWatch.Stop();
        if (timings is not null)
        {
            timings.Record("design-time-builds", buildsWatch.Elapsed); // WALL-CLOCK of the parallel batch
            ReportBuildSummary(progress, perProject!, buildsWatch.Elapsed, parallelism);
        }

        return resultsBag.ToList();
    }

    // Reports scope filtering decisions to the progress sink: how many projects were narrowed by the
    // entry-closure scope or by --no-tests, so the user can see what was excluded before builds ran.
    private static void ReportScopeProgress(
        Action<string>? progress,
        AnalyzerManager manager,
        IReadOnlyList<IProjectAnalyzer> toBuild,
        IReadOnlySet<string>? scopeProjectPaths,
        bool excludeTests
    )
    {
        if (scopeProjectPaths is not null)
        {
            progress?.Invoke(
                $"Scoped to {toBuild.Count} project(s) in the entry closure "
                    + $"(skipping {manager.Projects.Count - toBuild.Count} out-of-scope / non-C# project(s))"
            );
        }
        else if (excludeTests)
        {
            var testCount = manager.Projects.Values.Count(pa => IsTestProjectPath(pa.ProjectFile.Path.ToString()));
            progress?.Invoke($"Excluding {testCount} test project(s) (--no-tests)");
        }
    }

    // Emits the cache-hit/miss or verify summary line after all builds complete.
    private static void ReportCacheStats(
        Action<string>? progress,
        IReadOnlyList<ProjectBuildInfo> results,
        BuildResultCache? cache,
        bool verifyBuildCache,
        int verifyMatches,
        int verifyMismatches,
        int verifyNoBaseline,
        int cacheHits,
        int cacheMisses
    )
    {
        if (cache is not null && verifyBuildCache)
        {
            var verdict =
                verifyMismatches == 0
                    ? "OK — fingerprint captures all build inputs"
                    : "MISMATCH — fingerprint is under-specified (see above)";
            ReportProgress(
                progress,
                $"build-cache verify: {verifyMatches} match, {verifyMismatches} MISMATCH, {verifyNoBaseline} no-baseline of {results.Count} project(s) — {verdict}"
            );
        }
        else if (cache is not null)
        {
            ReportProgress(progress, $"build cache: {cacheHits} hit(s), {cacheMisses} miss(es) of {results.Count} project(s)");
        }
    }

    // Per-project design-time-build distribution for --time. The wall-clock (the phase metric) is passed
    // in; this reports the SEPARATE work view: Σ build-seconds (≠ wall — builds run in parallel), the
    // effective parallelism achieved (Σ/wall vs the cap), the per-project spread, and the slowest few.
    private static void ReportBuildSummary(
        Action<string>? progress,
        ConcurrentBag<(string Name, double Seconds)> perProject,
        TimeSpan wall,
        int parallelism
    )
    {
        if (progress is null || perProject.IsEmpty)
        {
            return;
        }

        var sorted = perProject.OrderBy(p => p.Seconds).ToArray();
        var seconds = sorted.Select(p => p.Seconds).ToArray();
        var sum = seconds.Sum();
        double Quantile(double q) => seconds[Math.Clamp(value: (int)Math.Round(q * (seconds.Length - 1)), min: 0, max: seconds.Length - 1)];
        var effective = wall.TotalSeconds > 0 ? sum / wall.TotalSeconds : 0;
        var slowest = string.Join(", ", perProject.OrderByDescending(p => p.Seconds).Take(5).Select(p => $"{p.Name} {p.Seconds:0.0}s"));

        progress(
            $"design-time builds: {perProject.Count} project(s) | wall {wall.TotalSeconds:0.0}s | "
                + $"Σcpu {sum:0.0}s (≠ wall; ~{effective:0.0}x parallel @ cap {parallelism}) | "
                + $"per-proj min {seconds[0]:0.0}s / median {Quantile(0.5):0.0}s / p95 {Quantile(0.95):0.0}s / max {seconds[^1]:0.0}s"
        );
        progress($"  slowest: {slowest}");
    }

    // Per-project compile+read distribution for --time: getCompilation / diagnostics / document-read seconds
    // per project. Σcpu far above wall × parallelism, or a few projects dominating getCompilation, is the
    // fingerprint of shared-dependency compilations being evicted and REBUILT across the parallel traversal
    // (the "are we re-binding the graph per project?" question). The phase wall is passed in; the rest is the
    // separate work view, same shape as ReportBuildSummary.
    private static void ReportCompileSummary(
        Action<string>? progress,
        ConcurrentBag<(string Name, double CompileSec, double DiagSec, double ReadSec)> perProject,
        TimeSpan wall,
        int parallelism
    )
    {
        if (progress is null || perProject.IsEmpty)
        {
            return;
        }

        var items = perProject.ToArray();
        var compile = items.Sum(p => p.CompileSec);
        var diag = items.Sum(p => p.DiagSec);
        var read = items.Sum(p => p.ReadSec);
        var sumCpu = compile + diag + read;
        var effective = wall.TotalSeconds > 0 ? sumCpu / wall.TotalSeconds : 0;
        var slowest = string.Join(", ", items.OrderByDescending(p => p.CompileSec).Take(5).Select(p => $"{p.Name} {p.CompileSec:0.0}s"));

        progress(
            $"compile+read: {items.Length} project(s) | wall {wall.TotalSeconds:0.0}s | "
                + $"Σcpu {sumCpu:0.0}s (≠ wall; ~{effective:0.0}x parallel @ cap {parallelism}) | "
                + $"Σ getCompilation {compile:0.0}s / Σ diagnostics {diag:0.0}s / Σ read {read:0.0}s"
        );
        progress($"  slowest getCompilation: {slowest}");
    }

    // Empty-closure guard (extracted from LoadAsync so it is unit-testable without a real workspace): a
    // `--from <csproj>` whose entry-closure resolves to 0 buildable C# projects — or whose only projects are
    // excluded (tests / IsExcludedProject) — must fail cleanly rather than crash at `csharpProjects[0]` with an
    // unhandled IndexOutOfRangeException. IndexCommands catches this InvalidOperationException and renders a
    // "Failed to load" diagnostic with a non-zero exit.
    internal static void EnsureIndexableProjects(int csharpProjectCount)
    {
        if (csharpProjectCount == 0)
        {
            throw new InvalidOperationException(
                "Nothing to index: the entry closure resolved to 0 buildable C# projects (after project excludes). "
                    + "Check the target/--from path and that its reference closure includes at least one C# project."
            );
        }
    }

    internal static string FormatProjectList(IReadOnlyList<string> names)
    {
        const int MaxShown = 10;
        return names.Count <= MaxShown
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(MaxShown)) + $", … (+{names.Count - MaxShown} more)";
    }

    // A project is a test project by name convention (matches the CLI's --from closure heuristic):
    // *.Tests / *.UnitTests / *.IntegrationTests, or a ".Tests." path segment. Excluded under --no-tests
    // so test methods don't surface as entry points and test-only references don't inflate the graph.
    internal static bool IsTestProjectPath(string projectPath)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        return name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("UnitTests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("IntegrationTests", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".Tests.", StringComparison.OrdinalIgnoreCase);
    }

    private static AdhocWorkspace BuildWorkspaceFromResults(IReadOnlyList<ProjectBuildInfo> projects, int parallelism)
    {
        var workspace = new AdhocWorkspace();

        // Pass 1 — assign stable ProjectIds keyed by normalised project path so cross-project
        // references can be resolved in pass 2 without depending on ordering.
        var projectIdByPath = projects
            .Where(r => r.ProjectFilePath is not null)
            .ToDictionary(
                r => Path.GetFullPath(r.ProjectFilePath!),
                r => ProjectId.CreateNewId(r.ProjectFilePath),
                StringComparer.OrdinalIgnoreCase
            );

        // Direct in-set project references per project (normalised paths), for the transitive-closure
        // computation below.
        var directInSetRefsByPath = projects
            .Where(r => r.ProjectFilePath is not null)
            .ToDictionary(
                r => Path.GetFullPath(r.ProjectFilePath!),
                r => r.ProjectReferences.Select(Path.GetFullPath).Where(projectIdByPath.ContainsKey).ToArray(),
                StringComparer.OrdinalIgnoreCase
            );

        var assemblyNameByPath = projects
            .Where(r => r.ProjectFilePath is not null)
            .ToDictionary(
                keySelector: r => Path.GetFullPath(r.ProjectFilePath!),
                elementSelector: r =>
                    r.Properties.TryGetValue(key: "AssemblyName", value: out var n)
                        ? n
                        : Path.GetFileNameWithoutExtension(r.ProjectFilePath!),
                comparer: StringComparer.OrdinalIgnoreCase
            );

        var metadataCache = new ConcurrentDictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var existsCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var fsharpDllCache = new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        bool Exists(string path) => existsCache.GetOrAdd(path, File.Exists);
        MetadataReference Meta(string path) =>
            metadataCache.GetOrAdd(path, p => AssemblyMetadata.CreateFromFile(p).GetReference(filePath: p));
        string[] NonCSharpDlls(string projectPath) => fsharpDllCache.GetOrAdd(projectPath, p => NonCSharpProjectReferenceDlls(p).ToArray());

        // Pass 2 — build each project, converting dep project refs to Roslyn ProjectReferences
        // when the dependency is also in the indexed set.  This gives the semantic model a
        // connected type graph so HasBaseType can traverse across project boundaries.
        // Each ProjectInfo is a pure function of its ProjectBuildInfo plus the read-only id/ref maps and
        // the thread-safe caches above, so the bodies run in parallel; AddProject folds them in serially.
        ProjectInfo BuildProjectInfo(ProjectBuildInfo result)
        {
            var projectId = result.ProjectFilePath is not null
                ? projectIdByPath.GetValueOrDefault(Path.GetFullPath(result.ProjectFilePath!), ProjectId.CreateNewId())
                : ProjectId.CreateNewId();

            var parseOptions = BuildParseOptions(result);
            var compilationOptions = BuildCompilationOptions(result);
            var (metadataRefs, projectRefs) = ResolveReferences(
                result,
                projectIdByPath,
                directInSetRefsByPath,
                assemblyNameByPath,
                Exists,
                Meta,
                NonCSharpDlls
            );
            var analyzerRefs = BuildAnalyzerReferences(result, Exists);
            var documents = BuildDocumentInfos(result, projectId, Exists);

            var projectName = result.ProjectFilePath is not null ? Path.GetFileNameWithoutExtension(result.ProjectFilePath) : "Unknown";
            var assemblyName = result.Properties.GetValueOrDefault("AssemblyName", defaultValue: projectName);

            return ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name: projectName,
                assemblyName: assemblyName,
                language: LanguageNames.CSharp,
                filePath: result.ProjectFilePath,
                compilationOptions: compilationOptions,
                parseOptions: parseOptions,
                metadataReferences: metadataRefs,
                projectReferences: projectRefs,
                analyzerReferences: analyzerRefs,
                documents: documents
            );
        }

        // Build the ProjectInfos in parallel (the heavy, disk-bound metadata reads); writing each into its
        // own slot preserves input order so the assembled solution is deterministic. AddProject then folds
        // them in serially — a Solution is an immutable snapshot rebuilt on each call, not thread-safe to
        // accumulate concurrently.
        var infos = new ProjectInfo[projects.Count];

        Parallel.For(
            fromInclusive: 0,
            toExclusive: projects.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) },
            i => infos[i] = BuildProjectInfo(projects[i])
        );

        var solution = workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), projects: infos));

        workspace.TryApplyChanges(solution);
        return workspace;
    }

    // Builds the CSharpParseOptions for a project: language version from MSBuild LangVersion property so
    // the parser handles modern C# syntax, preprocessor symbols, and DocumentationMode.None to skip XML
    // doc comment trivia that adds churn without affecting fact extraction.
    private static CSharpParseOptions BuildParseOptions(ProjectBuildInfo result)
    {
        // Language version: read from MSBuild LangVersion property so the parser
        // handles modern C# syntax (primary constructors, collection expressions, etc.).
        // Falls back to LanguageVersion.Default if unset or unparseable.
        LanguageVersion langVersion = LanguageVersion.Default;
        if (result.Properties.TryGetValue(key: "LangVersion", value: out var lv) && lv is not null)
        {
            LanguageVersionFacts.TryParse(lv, out langVersion);
        }

        // DocumentationMode.None: don't parse `///` XML doc comments into structured trivia and don't
        // bind/validate them. Fact extraction is doc-comment-AGNOSTIC — symbols, DocIDs
        // (GetDocumentationCommentId derives from symbol STRUCTURE, not the `///` text), references,
        // type-relations and dispatch are identical either way; only doc-comment diagnostics depend on
        // it, and those are discarded. The default (Parse) makes Roslyn build doc-comment trivia and,
        // via GetDiagnostics, run DocumentationCommentCompiler (~hundreds of MB of churn on MedDBase).
        // Same principled-and-strictly-less-work rationale as the nullable-off compilation option below.
        return new CSharpParseOptions(
            languageVersion: langVersion,
            preprocessorSymbols: result.PreprocessorSymbols,
            documentationMode: DocumentationMode.None
        );
    }

    // Builds the CSharpCompilationOptions for a project: OutputKind from OutputType property,
    // AllowUnsafe from AllowUnsafeBlocks, and nullable context forced off (fact extraction is
    // nullable-agnostic; skipping NullableWalker is principled and strictly less work).
    private static CSharpCompilationOptions BuildCompilationOptions(ProjectBuildInfo result)
    {
        // Compilation options: OutputKind must be Library for class library / web projects
        // so the compiler doesn't require a Main method (CS5001).  AllowUnsafe and Nullable
        // are also propagated from the MSBuild properties so method resolution succeeds.
        var outputType = result.Properties.TryGetValue(key: "OutputType", value: out var ot) ? ot : "Library";
        var outputKind =
            outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)
                ? OutputKind.ConsoleApplication
                : OutputKind.DynamicallyLinkedLibrary;
        var allowUnsafe =
            result.Properties.TryGetValue(key: "AllowUnsafeBlocks", value: out var unsafeStr)
            && bool.TryParse(unsafeStr, out var unsafeBool)
            && unsafeBool;
        // Force nullable context OFF regardless of the project's <Nullable> setting: fact extraction is
        // nullable-AGNOSTIC (symbol resolution, DocIDs, references, type-relations and dispatch are
        // identical with or without it; nullable context only governs warnings, which we discard), so
        // skipping NullableWalker flow analysis is free of facts. NB: measured benefit on MedDBase was
        // only marginal (~3s / 0.3 GB peak) — a dotnet-trace gc-verbose profile showed the big
        // `TypeWithAnnotations` churn is Roslyn's UNIVERSAL internal type representation (all overload
        // resolution / type construction), not nullable-flow-specific, so it allocates either way.
        // Kept because it's principled and strictly less work, not because it's a major win.
        const NullableContextOptions nullableContext = NullableContextOptions.Disable;
        return new CSharpCompilationOptions(outputKind, allowUnsafe: allowUnsafe, nullableContextOptions: nullableContext);
    }

    // Resolves a project's metadata references and in-workspace project references.
    // The transitive in-set closure of ProjectReferences is added as live Roslyn ProjectReferences (one
    // identity per type); their DLLs are dropped from metadata. net452/netstandard2.0 sibling DLLs and
    // Non-C# (F#/VB) project-output DLLs are added to keep the type graph complete.
    private static (MetadataReference[] MetadataRefs, ProjectReference[] ProjectRefs) ResolveReferences(
        ProjectBuildInfo result,
        IReadOnlyDictionary<string, ProjectId> projectIdByPath,
        IReadOnlyDictionary<string, string[]> directInSetRefsByPath,
        IReadOnlyDictionary<string, string> assemblyNameByPath,
        Func<string, bool> exists,
        Func<string, MetadataReference> meta,
        Func<string, string[]> nonCSharpDlls
    )
    {
        var allRefs = result.References;

        // When a net48 project (like MedDBase.Pages) references a netstandard2.0 library
        // (like MedDBase.DataAccessTier.dll) that was compiled against the netstandard2.0
        // build of a package (e.g. LLBLGen), but Buildalyzer resolves the net452 build for
        // the net48 TFM, the base-type chain inside the netstandard2.0 DLL is unresolvable.
        // To fix this: for every net452 reference we have, also add the netstandard2.0 sibling
        // if it exists, so both assembly identities are in the compilation.
        var siblingRefs = allRefs
            .Where(exists)
            .Select(r =>
            {
                var ns20 = r.Replace(
                    Path.DirectorySeparatorChar + "net452" + Path.DirectorySeparatorChar,
                    Path.DirectorySeparatorChar + "netstandard2.0" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                );
                return ns20 != r && exists(ns20) ? ns20 : null;
            })
            .Where(r => r is not null)
            .Select(r => r!);

        // Project-to-project references within the indexed set become Roslyn ProjectReferences
        // so the semantic model can cross project boundaries (e.g. Pages → MMS.Web.UI).
        // DLL references whose output path matches an indexed project are dropped — Roslyn
        // uses the live compilation of the referenced project instead.
        //
        // The closure is TRANSITIVE, not just this project's direct refs: Roslyn project
        // references do not flow transitively, so a project that USES a transitively-referenced
        // project's types (e.g. Rig.Cli using Rig.Domain via Rig.Storage) would otherwise see
        // those types only through the metadata DLL — a SECOND assembly identity alongside the live
        // transitive compilation. That duplicate identity makes any call whose signature mentions
        // such a type fail to bind, silently dropping the call edge (a recall gap that inflates
        // dead-code/false-unreachable results). Pulling the whole in-set closure in as live
        // ProjectReferences (and dropping their DLLs from metadata below) gives one identity.
        var inWorkspaceProjectPaths = TransitiveInSetClosure(result.ProjectFilePath, directInSetRefsByPath);

        var inWorkspaceAssemblyNames = new HashSet<string>(
            inWorkspaceProjectPaths.Select(p => assemblyNameByPath[p]),
            StringComparer.OrdinalIgnoreCase
        );

        // Non-C# (F#/VB) project references can't be loaded as live C# ProjectReferences (the
        // workspace only compiles C#), so a C# project that uses their types otherwise hits
        // CS0012 ("type is defined in an assembly that is not referenced"). Roslyn CAN consume the
        // referenced project's BUILT OUTPUT DLL as metadata — resolve it and add it. Gathered over
        // the transitive in-set closure too: e.g. DataServer reaches the F# MedDBase.Pathways.DSL via
        // the C# MedDBase.Pathways, and Roslyn project refs don't flow metadata transitively.
        var fsharpRefDlls = inWorkspaceProjectPaths
            .Append(result.ProjectFilePath is null ? null : Path.GetFullPath(result.ProjectFilePath))
            .Where(p => p is not null)
            .SelectMany(p => nonCSharpDlls(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var metadataRefs = allRefs
            .Concat(siblingRefs)
            .Concat(fsharpRefDlls)
            .Where(exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Skip DLLs whose assembly is provided by a live project reference
            .Where(path => !inWorkspaceAssemblyNames.Contains(Path.GetFileNameWithoutExtension(path)))
            .Select(meta)
            .ToArray();

        var projectRefs = inWorkspaceProjectPaths.Select(p => new ProjectReference(projectIdByPath[p])).ToArray();

        return (metadataRefs, projectRefs);
    }

    // Resolves the project's Buildalyzer-reported analyzer/generator references as Roslyn AnalyzerFileReferences.
    // OutputItemType="Analyzer" ProjectReferences are NOT included here (Buildalyzer drops them); they are
    // wired separately after the workspace is built (WireGeneratorAnalyzers).
    private static AnalyzerReference[] BuildAnalyzerReferences(ProjectBuildInfo result, Func<string, bool> exists)
    {
        // Wire up Roslyn source generators/analyzers (e.g. proxy code-gen for ClientPage
        // subclasses).  Without these the compilation is missing generated types and semantic
        // analysis fails for files that reference them.
        // Buildalyzer-reported analyzer refs (package analyzers/generators). The project's
        // OutputItemType="Analyzer" ProjectReferences (e.g. the ClientPage proxy generator) are NOT
        // reported here — Buildalyzer drops them — so they're wired separately AFTER the workspace
        // is built (WireGeneratorAnalyzers), by emitting each generator project's compilation.
        return result
            .AnalyzerReferences.Where(exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (AnalyzerReference)new AnalyzerFileReference(path, HostRedirectingAnalyzerLoader.Instance))
            .ToArray();
    }

    // Builds Roslyn DocumentInfo records for each existing C# source file in the project.
    private static DocumentInfo[] BuildDocumentInfos(ProjectBuildInfo result, ProjectId projectId, Func<string, bool> exists)
    {
        return result
            .SourceFiles.Where(exists)
            .Select(filePath =>
                DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(filePath),
                    filePath: filePath,
                    loader: new FileTextLoader(filePath, null)
                )
            )
            .ToArray();
    }

    // Normalised full paths of a project's OutputItemType="Analyzer" ProjectReferences (source
    // generators / analyzers wired the way MedDBase.Pages references RequestResponseProxyGenerator).
    // Parsed from the csproj XML — Buildalyzer's design-time build omits these from AnalyzerReferences.
    private static IEnumerable<string> AnalyzerProjectReferencePaths(string projectFilePath)
    {
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? "";
        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath);
        }
        catch
        {
            yield break;
        }

        foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var outputItemType =
                reference.Elements().FirstOrDefault(c => c.Name.LocalName == "OutputItemType")?.Value
                ?? reference.Attribute("OutputItemType")?.Value;
            if (!string.Equals(outputItemType, "Analyzer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var include = reference.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include))
            {
                continue;
            }

            yield return Path.GetFullPath(Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar)));
        }
    }

    // Built-output DLLs of a project's NON-C# (F#/VB) <ProjectReference>s. The C# workspace can't compile
    // those projects, so their types are invisible unless we add their compiled assembly as metadata
    // (otherwise CS0012). Parsed from the csproj XML; the referenced project's output DLL is resolved
    // best-effort from its bin/. Buildalyzer's project-references-off design-time build doesn't reliably
    // surface these in result.References, so we add them explicitly.
    internal static IEnumerable<string> NonCSharpProjectReferenceDlls(string projectFilePath)
    {
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? "";
        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath);
        }
        catch
        {
            yield break;
        }

        foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = reference.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include))
            {
                continue;
            }

            var refPath = Path.GetFullPath(Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar)));
            var ext = Path.GetExtension(refPath);
            if (!ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ResolveBuiltOutputDll(refPath) is { } dll)
            {
                yield return dll;
            }
        }
    }

    // Best-effort path to a project's built output DLL under bin/. Prefers a Release build, then the
    // most-recently-written match. AssemblyName defaults to the project filename unless <AssemblyName> is set.
    internal static string? ResolveBuiltOutputDll(string projectFilePath)
    {
        var projectDir = Path.GetDirectoryName(projectFilePath);
        if (projectDir is null)
        {
            return null;
        }

        var assemblyName = Path.GetFileNameWithoutExtension(projectFilePath);
        try
        {
            var declared = XDocument.Load(projectFilePath).Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value;
            if (!string.IsNullOrWhiteSpace(declared))
            {
                assemblyName = declared;
            }
        }
        catch
        {
            // fall back to the filename-derived assembly name
        }

        var bin = Path.Combine(projectDir, "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(path: bin, searchPattern: assemblyName + ".dll", searchOption: SearchOption.AllDirectories)
            .OrderByDescending(path => path.Contains("Release", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    // Transitive closure of a project's in-set project references (excluding the project itself),
    // over the direct-in-set-refs adjacency. Returned paths are normalised (Path.GetFullPath).
    private static HashSet<string> TransitiveInSetClosure(
        string? projectFilePath,
        IReadOnlyDictionary<string, string[]> directInSetRefsByPath
    )
    {
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (projectFilePath is null)
        {
            return closure;
        }

        var start = Path.GetFullPath(projectFilePath);
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!directInSetRefsByPath.TryGetValue(current, out var refs))
            {
                continue;
            }

            foreach (var dep in refs)
            {
                if (closure.Add(dep)) // first time we've seen this dependency
                {
                    stack.Push(dep);
                }
            }
        }
        closure.Remove(start); // a self-cycle must not make a project reference itself
        return closure;
    }

    // `compilation` is the project's compilation, passed in (not re-fetched from `project`) so the SAME
    // instance the caller bound diagnostics against is reused AND kept rooted across this whole method —
    // Roslyn holds final compilations recoverably, so without an explicit reference the bind cache could be
    // evicted mid-read and silently rebuilt (the fused compile+read pass exists precisely to avoid that).
    private static async Task<ProjectSourceLoadResult> LoadProjectSourcesAsync(
        string solutionPath,
        Project project,
        Compilation compilation,
        RuleSet rules,
        CancellationToken cancellationToken
    )
    {
        var sources = new List<SourceModel>();
        var sourceFiles = new List<SourceFileInfo>();

        foreach (
            var document in project
                .Documents.Where(d => d.FilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
        )
        {
            if (document.FilePath is null)
            {
                continue;
            }

            var classification = SourceFileClassifier.Classify(
                solutionPath: solutionPath,
                project: project,
                filePath: document.FilePath,
                rules: rules
            );

            sourceFiles.Add(
                new SourceFileInfo(
                    ProjectName: project.Name,
                    FilePath: document.FilePath,
                    Status: classification.Status,
                    Confidence: classification.Confidence,
                    Basis: classification.Basis,
                    Reason: classification.Reason,
                    Evidence: classification.Evidence
                )
            );

            if (classification.Status != "indexed")
            {
                continue;
            }

            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            var root = tree is null ? null : await tree.GetRootAsync(cancellationToken);

            if (tree is null || root is null)
            {
                continue;
            }

            // Bind through the passed-in compilation (not document.GetSemanticModelAsync) so the model is
            // built over the SAME instance ProcessProject warmed via GetDiagnostics — same-tree binding hits
            // that warmed cache instead of triggering a recompile. The document's tree is one of this
            // compilation's syntax trees (same project snapshot), so GetSemanticModel resolves it directly.
            var semanticModel = compilation.GetSemanticModel(tree);

            sources.Add(
                new SourceModel(
                    ProjectName: project.Name,
                    FilePath: document.FilePath,
                    Tree: tree,
                    Root: root,
                    SemanticModel: semanticModel
                )
            );
        }

        // Also index SOURCE-GENERATED documents (Roslyn source generators wired as analyzer refs,
        // e.g. RequestResponseProxyGenerator emitting <Page>Proxy : ProxyBase). These are NOT in
        // project.Documents, and AdhocWorkspace.GetSourceGeneratedDocumentsAsync does not execute
        // generators in this design-time-build setup (it returns nothing). So drive the generators
        // explicitly with a CSharpGeneratorDriver over the project compilation and index the trees it
        // produces — that's what makes the generated proxy base-type facts (the clientpage_proxy
        // effect gate's discriminator) exist.
        foreach (var generated in await RunSourceGeneratorsAsync(project, compilation, cancellationToken))
        {
            sourceFiles.Add(
                new SourceFileInfo(
                    ProjectName: project.Name,
                    FilePath: generated.FilePath,
                    Status: "indexed",
                    Confidence: "high",
                    Basis: "generated",
                    Reason: "source_generator",
                    Evidence: ""
                )
            );
            sources.Add(generated);
        }

        return new ProjectSourceLoadResult(sourceFiles, sources);
    }

    // Wires source-generator ProjectReferences (OutputItemType="Analyzer") onto each referencing
    // project. Buildalyzer's design-time build omits these from AnalyzerReferences, and design-time
    // builds emit no DLL, so we EMIT each generator project's own (in-workspace) compilation to a temp
    // assembly and add it as an analyzer reference via the host-redirecting loader. Idempotent emit per
    // generator project; only references that actually expose generators are added. Best-effort: a
    // generator project that can't emit or load is skipped (it just won't contribute generated types).
    private static async Task WireGeneratorAnalyzersAsync(
        AdhocWorkspace workspace,
        Action<string>? progress,
        CancellationToken cancellationToken
    )
    {
        var solution = workspace.CurrentSolution;
        var projectByPath = solution
            .Projects.Where(p => p.FilePath is not null)
            .GroupBy(p => Path.GetFullPath(p.FilePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var emittedDllByGeneratorPath = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var project in solution.Projects.ToArray())
        {
            if (project.FilePath is null)
            {
                continue;
            }

            foreach (var generatorProjectPath in AnalyzerProjectReferencePaths(project.FilePath))
            {
                if (!emittedDllByGeneratorPath.TryGetValue(key: generatorProjectPath, value: out var dllPath))
                {
                    dllPath = projectByPath.TryGetValue(generatorProjectPath, out var generatorId)
                        ? await EmitCompilationToTempAsync(solution.GetProject(generatorId)!, cancellationToken)
                        : null;
                    emittedDllByGeneratorPath[generatorProjectPath] = dllPath;
                }

                if (dllPath is null)
                {
                    continue;
                }

                var reference = new AnalyzerFileReference(dllPath, HostRedirectingAnalyzerLoader.Instance);
                if (!reference.GetGenerators(LanguageNames.CSharp).Any())
                {
                    continue;
                }

                solution = solution.AddAnalyzerReference(project.Id, reference);
                changed = true;
                ReportProgress(
                    progress,
                    $"Wired source generator {Path.GetFileNameWithoutExtension(generatorProjectPath)} -> {project.Name}"
                );
            }
        }

        if (changed)
        {
            workspace.TryApplyChanges(solution);
        }
    }

    // Emits a project's compilation to a temp DLL so its source generators can be loaded as an analyzer
    // reference. Returns null when the compilation is unavailable or fails to emit (then the generator
    // is simply not wired). The temp file is left for the process lifetime (OS temp cleanup).
    private static async Task<string?> EmitCompilationToTempAsync(Project project, CancellationToken cancellationToken)
    {
        try
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                return null;
            }

            var tempDll = Path.Combine(Path.GetTempPath(), $"rig-gen-{project.AssemblyName}-{Guid.NewGuid():N}.dll");
            await using var stream = File.Create(tempDll);
            var emitResult = compilation.Emit(stream, cancellationToken: cancellationToken);
            return emitResult.Success ? tempDll : null;
        }
        catch
        {
            return null;
        }
    }

    // Executes the project's Roslyn source generators (from its analyzer references) against its
    // compilation and returns a SourceModel per generated syntax tree, with a semantic model bound to
    // the generator-updated compilation. Projects with no generators (the common case) return empty
    // after a cheap check. Generator failures are swallowed (best-effort) so one bad generator can't
    // fail the whole index.
    private static async Task<IReadOnlyList<SourceModel>> RunSourceGeneratorsAsync(
        Project project,
        Compilation compilation,
        CancellationToken cancellationToken
    )
    {
        var generators = project.AnalyzerReferences.SelectMany(ar => ar.GetGenerators(LanguageNames.CSharp)).ToArray();
        if (generators.Length == 0)
        {
            return [];
        }

        try
        {
            var parseOptions = project.ParseOptions as CSharpParseOptions;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: parseOptions);
            driver.RunGeneratorsAndUpdateCompilation(
                compilation: compilation,
                outputCompilation: out var generatedCompilation,
                diagnostics: out _,
                cancellationToken: cancellationToken
            );

            var originalTrees = new HashSet<SyntaxTree>(compilation.SyntaxTrees);
            var results = new List<SourceModel>();
            foreach (var tree in generatedCompilation.SyntaxTrees)
            {
                if (originalTrees.Contains(tree))
                {
                    continue;
                }

                var root = await tree.GetRootAsync(cancellationToken);
                var semanticModel = generatedCompilation.GetSemanticModel(tree);
                // Generated trees carry a generator hint-name path; fall back to a synthetic one.
                var generatedPath = string.IsNullOrEmpty(tree.FilePath)
                    ? $"<generated>/{project.Name}/{results.Count}.g.cs"
                    : tree.FilePath;
                results.Add(
                    new SourceModel(
                        ProjectName: project.Name,
                        FilePath: generatedPath,
                        Tree: tree,
                        Root: root,
                        SemanticModel: semanticModel
                    )
                );
            }
            return results;
        }
        catch (Exception)
        {
            // Best-effort: a misbehaving generator must not abort indexing of the real source.
            return [];
        }
    }

    // Progress is reported concurrently from the parallel build/compile/read loops. The contract is
    // that a non-null sink is itself thread-safe: the CLI passes Console.Out (a synchronized
    // SyncTextWriter, atomic per WriteLine), and tests pass no sink at all (null). So no app-level
    // lock is needed — each Invoke writes one whole line without interleaving.
    private static void ReportProgress(Action<string>? progress, string message) => progress?.Invoke(message);

    private static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

    // A multi-targeted outer project has no Compile target. Build one inner TFM explicitly;
    // the first declared TFM preserves historical behavior when the caller does not choose one.
    // Indexing a single TFM remains lossy under conditional compilation; see the union-extraction backlog.
    private static IAnalyzerResult? BuildCompileOnly(IProjectAnalyzer analyzer, string? framework)
    {
        var targetFrameworks = analyzer.ProjectFile.TargetFrameworks;
        var selectedFramework = SelectFramework(analyzer.ProjectFile.Name, targetFrameworks, framework);
        var results = selectedFramework is not null
            ? analyzer.Build(selectedFramework, CompileOnlyOptions(forExplicitFramework: framework is not null))
            : analyzer.Build(CompileOnlyOptions());
        return PreferredResult(results);
    }

    internal static string? SelectFramework(string projectName, IReadOnlyList<string>? targetFrameworks, string? requestedFramework)
    {
        if (targetFrameworks is not { Count: > 1 })
        {
            return null;
        }

        if (requestedFramework is null)
        {
            return targetFrameworks[0];
        }

        var selected = targetFrameworks.FirstOrDefault(tfm => tfm.Equals(requestedFramework, StringComparison.OrdinalIgnoreCase));
        return selected
            ?? throw new FrameworkSelectionException(
                $"Project '{projectName}' does not target requested framework '{requestedFramework}'. "
                    + $"Available target frameworks: {string.Join(", ", targetFrameworks)}."
            );
    }

    // Cross-targeting results can include a sourceless outer-project result before the usable inner result.
    private static IAnalyzerResult? PreferredResult(IAnalyzerResults results) =>
        results.FirstOrDefault(r => r.SourceFiles is { Length: > 0 }) ?? results.FirstOrDefault();

    // Build ONLY the `Compile` target instead of Buildalyzer's default ["Clean", "Build"]. The default
    // runs `Clean` first — which DELETES the project's bin/obj — and then a design-time `Build`
    // (DesignTimeBuild=true) that emits nothing, so a plain Build() DESTROYS any pre-built output
    // (msbuild/CI binaries, dependency-DLL copies). That's the open Buildalyzer#105. `Compile` still runs
    // ResolveAssemblyReferences + Csc — so the resolved references and the command-line args Buildalyzer
    // parses are still captured (DesignTimeBuild makes CoreCompile fire its command-line event without a
    // real emit, so the Clean+NonExistentFile force-recompile trick isn't needed) — but skips Clean,
    // CopyFilesToOutputDirectory, and Before/AfterBuild. Non-destructive, and a 30-50% cold-load speedup
    // with identical reference/document counts (Buildalyzer#344). A fresh instance per call: the parallel
    // build loop must not share one mutable options object across threads.
    private static EnvironmentOptions CompileOnlyOptions(bool forExplicitFramework = false)
    {
        var options = new EnvironmentOptions();
        options.TargetsToBuild.Clear();
        options.TargetsToBuild.Add("Compile");
        if (forExplicitFramework)
        {
            // A restore warning persisted as an error can prevent Buildalyzer from producing sources.
            // Refresh the selected TFM's assets without weakening Roslyn compilation diagnostics.
            options.GlobalProperties["RestoreForceEvaluate"] = "true";
            options.GlobalProperties["TreatWarningsAsErrors"] = "false";
        }

        return options;
    }

    // A design-time build is degraded if it produced NO source files. A healthy C# project build always
    // yields at least the generated AssemblyInfo / GlobalUsings; zero means the build aborted before
    // CoreCompile (transient failure or a racing obj flush), so the project compiles to nothing and its
    // dependents fail to bind. Such a result must never be cached (see BuildOrLoad).
    private static bool IsDegradedBuild(ProjectBuildInfo info) => info.SourceFiles.Count == 0;

    // Thrown when a project's design-time build yields 0 source files even after DegradedBuildRetries — a
    // deterministic failure that would corrupt the index. Derives from InvalidOperationException so the
    // index command's existing load-failure handler catches it (clean exit, no raw stack trace), and is a
    // distinct type so the per-project "skip on build failure" catch can let it through to abort the run.
    private sealed class DegradedBuildException(string message) : InvalidOperationException(message);

    private sealed class FrameworkSelectionException(string message) : InvalidOperationException(message);

    private sealed record ProjectSourceLoadResult(IReadOnlyList<SourceFileInfo> SourceFiles, IReadOnlyList<SourceModel> Sources);

    // Loads source-generator/analyzer DLLs and — crucially — REDIRECTS their Microsoft.CodeAnalysis*
    // (+ Immutable/Metadata) references to the HOST's already-loaded copies. A generator compiled
    // against an older Roslyn (e.g. 4.8) otherwise implements that version's ISourceGenerator, which
    // our 5.x host's GetGenerators() won't recognize (different assembly identity) → 0 generators.
    // Redirecting to the host's assemblies unifies the identity so the generator binds to OUR Roslyn —
    // the same thing Roslyn's own analyzer assembly loader does (see dotnet/roslyn#60702).
    private sealed class HostRedirectingAnalyzerLoader : IAnalyzerAssemblyLoader
    {
        internal static readonly HostRedirectingAnalyzerLoader Instance = new();
        private static int _hooked;

        public void AddDependencyLocation(string fullPath) { }

        public Assembly LoadFromPath(string fullPath)
        {
            EnsureRedirectHook();
            return Assembly.LoadFrom(fullPath);
        }

        private static void EnsureRedirectHook()
        {
            if (Interlocked.Exchange(location1: ref _hooked, value: 1) != 0)
            {
                return;
            }

            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                if (name.Name is null)
                {
                    return null;
                }

                var redirect =
                    name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                    || name.Name is "System.Collections.Immutable" or "System.Reflection.Metadata";
                if (!redirect)
                {
                    return null;
                }

                return AppDomain
                    .CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name, StringComparison.Ordinal));
            };
        }
    }

    private sealed class ProgressLogWriter(Action<string> progress) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            // Only surface high-signal lines from MSBuild output to avoid noise
            if (
                value.Contains("error", StringComparison.OrdinalIgnoreCase)
                || value.Contains("warning", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Build succeeded", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Build FAILED", StringComparison.OrdinalIgnoreCase)
            )
            {
                progress($"MSBuild: {value.Trim()}");
            }
        }
    }
}

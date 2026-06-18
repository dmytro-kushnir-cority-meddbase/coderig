using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using Buildalyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Rig.Analysis.Rules;
using Rig.Domain.Data;

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
        AnalysisRuleSet rules,
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
        // Drop test projects (by name convention) before their design-time build — --no-tests.
        bool excludeTests = false,
        // Optional per-phase timing collector (rig index --time). Records workspace-build,
        // wire-generators, and the fused compile+read pass. Null = no timing.
        PhaseTimings? timings = null,
        // Directory for the design-time-build cache (rig index --reuse-build-cache). Null = disabled.
        string? buildCacheDir = null
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
        var workspace = await Task.Run(
            () => BuildWorkspace(solutionPath, progress, scopeProjectPaths, maxParallelism, excludeTests, timings, buildCacheDir),
            cancellationToken
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

        var csharpProjects = workspace
            .CurrentSolution.Projects.Where(p => p.Language == LanguageNames.CSharp)
            .Where(p => !rules.IsExcludedProject(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        ReportProgress(progress, $"Loaded {csharpProjects.Length} C# project(s) to index");

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
            timings!.Record("compile+read", phase.Elapsed);
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

        var projectDirectories = csharpProjects
            .Select(p => p.FilePath)
            .Where(path => path is not null)
            .Select(path => Path.GetDirectoryName(path) ?? string.Empty)
            .Where(dir => dir.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SolutionSourceSet(
            projectResults.SelectMany(r => r.SourceFiles).OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).ToList(),
            projectResults.SelectMany(r => r.Sources).OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase).ToList(),
            projectDirectories
        );

        async ValueTask ProcessProject(Project project, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref analyzedProjects);
            // Log EVERY project at the start of its analysis (not throttled): the next line, GetCompilationAsync,
            // is where Roslyn binds + runs source generators — the dominant, otherwise-silent per-project cost. A
            // per-project breadcrumb makes a slow project/generator visible by name. These interleave under the
            // parallel loop (several run at once); the atomic `current` keeps the N/total meaningful regardless.
            ReportProgress(progress, $"Analyzing project {current}/{csharpProjects.Length}: {project.Name}");

            // Build the compilation ONCE; the diagnostics bind warms the per-document semantic
            // models that LoadProjectSourcesAsync reuses.
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                compilationErrors.Add($"{project.Name}: compilation unavailable");
                return; // no semantic model possible — nothing to read for this project
            }

            foreach (var diagnostic in compilation.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                compilationErrors.Add($"{project.Name}: {diagnostic}");
                Console.WriteLine($"{project.Name}: {diagnostic}");
            }

            projectResults.Add(await LoadProjectSourcesAsync(solutionPath, project, rules, ct));
        }
    }

    private static AdhocWorkspace BuildWorkspace(
        string solutionPath,
        Action<string>? progress,
        IReadOnlySet<string>? scopeProjectPaths,
        int parallelism,
        bool excludeTests,
        PhaseTimings? timings = null,
        string? buildCacheDir = null
    )
    {
        var logWriter = progress is null ? null : new ProgressLogWriter(progress);
        var options = new AnalyzerManagerOptions { LogWriter = logWriter };

        // Design-time-build cache (rig index --reuse-build-cache). On a fingerprint hit the (dominant)
        // out-of-process build is skipped and the cached ProjectBuildInfo is replayed. Null = disabled.
        var cache = buildCacheDir is null ? null : new BuildResultCache(buildCacheDir);
        var cacheHits = 0;
        var cacheMisses = 0;

        // Per project: fingerprint → cache hit (skip the build) or miss (build, convert, store). With the
        // cache off it's just build + convert. The fingerprint reads no file contents (see
        // BuildInputFingerprint), so this is cheap relative to the build it may skip.
        ProjectBuildInfo? BuildOrLoad(string projectFilePath, Func<IAnalyzerResult?> build)
        {
            if (
                cache is not null
                && cache.TryLoad(
                    projectFilePath: projectFilePath,
                    fingerprint: BuildInputFingerprint.Compute(projectFilePath),
                    info: out var cached
                )
            )
            {
                Interlocked.Increment(ref cacheHits);
                return cached;
            }

            var built = build();
            if (built is null)
            {
                return null;
            }

            var info = ProjectBuildInfo.FromAnalyzerResult(built);
            var name = Path.GetFileNameWithoutExtension(projectFilePath);

            // Fail-safe health check on the design-time build OUTPUT. A healthy build of a C# project always
            // yields source files (the real .cs plus generated AssemblyInfo / GlobalUsings); ZERO sources
            // means the build aborted before CoreCompile — usually a transient failure or a racing obj flush —
            // so the project's types are absent and its dependents won't bind (the MedDBase Import.TAL
            // "0 sources → 6694 CS0246" cascade). RETRY: a transient failure normally clears on a fresh build.
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

            // Still degraded after the retries → DIE. A degraded compilation silently emits WRONG facts (its
            // own types missing, every dependent unbound), so a hard abort beats shipping a quietly-corrupt
            // index. DegradedBuildException is filtered OUT of the per-project "skip on build failure" catch in
            // the parallel build loop, so it propagates and aborts the whole index. Never cached either way.
            if (IsDegradedBuild(info))
            {
                throw new DegradedBuildException(
                    $"'{name}' design-time build produced 0 source files after {DegradedBuildRetries + 1} attempt(s) — "
                        + "its types would be absent and dependents would fail to bind, corrupting the index. "
                        + $"Re-run `dotnet restore` / `dotnet build` for {name}, then re-index."
                );
            }

            if (cache is not null)
            {
                // Re-fingerprint AFTER the build and store THAT. The csproj + manifests are content-hashed and
                // the build never rewrites them, so for THOSE inputs pre- and post-build are identical. The
                // input that DOES settle during the build is the *.cs path SET: projects with build-time
                // codegen into the project tree (T4 with TransformOnBuild — MedDBase's ImportLayer2 /
                // ServiceLayer .tt files emit *.g.cs NEXT TO the .tt, not into obj) gain those source paths
                // only after CoreCompile. Storing the SETTLED post-build set means the next index — which
                // sees the generated files — matches and hits in ONE run; storing the pre-build set would
                // miss once and converge over two. (Cheap: the content-hash memo makes re-hashing the
                // unchanged manifests almost free, so the only real post-build work is the path re-walk.)
                cache.Store(projectFilePath: projectFilePath, fingerprint: BuildInputFingerprint.Compute(projectFilePath), info: info);
                Interlocked.Increment(ref cacheMisses);
            }

            return info;
        }

        List<ProjectBuildInfo> results;
        if (IsProjectFile(solutionPath))
        {
#pragma warning disable CS0618
            var manager = new AnalyzerManager(options);
            var analyzer = manager.GetProject(solutionPath);
#pragma warning restore CS0618
            progress?.Invoke($"MSBuild: running design-time build for {Path.GetFileNameWithoutExtension(solutionPath)}");
            analyzer!.SetGlobalProperty(key: "DesignTimeBuild", value: "true");
            analyzer.SetGlobalProperty(key: "BuildingInsideVisualStudio", value: "true");
            // Prevent the MSBuild compiler server from being shared across parallel processes —
            // concurrent Buildalyzer calls can corrupt each other's bin/ output if they share
            // compilation state.
            analyzer.SetGlobalProperty(key: "UseSharedCompilation", value: "false");
            var singleWatch = timings is null ? null : Stopwatch.StartNew();
            var info =
                BuildOrLoad(Path.GetFullPath(solutionPath), () => analyzer.Build(CompileOnlyOptions()).FirstOrDefault())
                ?? throw new InvalidOperationException($"Buildalyzer produced no build results for '{solutionPath}'.");
            if (singleWatch is not null)
            {
                timings!.Record("design-time-builds", singleWatch.Elapsed);
            }

            results = [info];
        }
        else
        {
#pragma warning disable CS0618
            var manager = new AnalyzerManager(solutionPath, options);
#pragma warning restore CS0618
            // Select the C# projects to build: skip non-C# projects (sqlproj/fsproj fail to parse)
            // and, when an entry-closure scope is given, everything outside it (test projects,
            // unrelated tools) — BEFORE paying for their design-time builds.
            var toBuild = manager
                .Projects.Values.Where(pa =>
                    string.Equals(Path.GetExtension(pa.ProjectFile.Path.ToString()), ".csproj", StringComparison.OrdinalIgnoreCase)
                )
                .Where(pa => scopeProjectPaths is null || scopeProjectPaths.Contains(Path.GetFullPath(pa.ProjectFile.Path.ToString())))
                .Where(pa => !excludeTests || !IsTestProjectPath(pa.ProjectFile.Path.ToString()))
                .ToList();

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
                            var info = BuildOrLoad(
                                Path.GetFullPath(projectAnalyzer.ProjectFile.Path.ToString()),
                                () => projectAnalyzer.Build(CompileOnlyOptions()).FirstOrDefault()
                            );
                            if (info is not null)
                            {
                                resultsBag.Add(info);
                            }
                        }
                        catch (Exception ex) when (ex is not DegradedBuildException)
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

                throw;
            }
            buildsWatch.Stop();
            if (timings is not null)
            {
                timings.Record("design-time-builds", buildsWatch.Elapsed); // WALL-CLOCK of the parallel batch
                ReportBuildSummary(progress, perProject!, buildsWatch.Elapsed, parallelism);
            }

            results = resultsBag.ToList();
        }

        if (cache is not null)
        {
            ReportProgress(progress, $"build cache: {cacheHits} hit(s), {cacheMisses} miss(es) of {results.Count} project(s)");
        }

        // Assembling the in-memory workspace from the (built/cached) project results: Roslyn loads all
        // projects + their reference closures into one MSBuildWorkspace. This is single-shot with no per-item
        // callback, so it's the otherwise-silent multi-minute stretch right after the build-cache line — name
        // it so the pause reads as a known phase, not a hang. (Source generators wire/run just after this.)
        ReportProgress(progress, $"Assembling workspace from {results.Count} project(s) (no per-item progress — this is the slow step)…");
        var assemblyWatch = timings is null ? null : Stopwatch.StartNew();
        var workspace = BuildWorkspaceFromResults(results, parallelism, progress);
        if (assemblyWatch is not null)
        {
            timings!.Record("workspace-assembly", assemblyWatch.Elapsed);
        }

        return workspace;
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

    private static AdhocWorkspace BuildWorkspaceFromResults(
        IReadOnlyList<ProjectBuildInfo> projects,
        int parallelism,
        Action<string>? progress = null
    )
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(Microsoft.CodeAnalysis.SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create()));

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

        // Shared caches across ALL projects. The same framework/package DLL is referenced by dozens of
        // projects; parsing its metadata once per *file* (here) instead of once per *referencing project*
        // (the old MetadataReference.CreateFromFile-in-the-loop) is the dominant workspace-assembly win —
        // and reusing the SAME MetadataReference instance lets Roslyn share the parsed AssemblyMetadata.
        // File.Exists probes and csproj XML loads are deduped likewise. All three are read concurrently by
        // the parallel project-build below, so they're ConcurrentDictionary. (A factory racing on the same
        // key parses twice and discards one — wasteful but correct; no lock needed.)
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
        Microsoft.CodeAnalysis.ProjectInfo BuildProjectInfo(ProjectBuildInfo result)
        {
            var projectId = result.ProjectFilePath is not null
                ? projectIdByPath.GetValueOrDefault(Path.GetFullPath(result.ProjectFilePath!), ProjectId.CreateNewId())
                : ProjectId.CreateNewId();

            // Language version: read from MSBuild LangVersion property so the parser
            // handles modern C# syntax (primary constructors, collection expressions, etc.).
            // Falls back to LanguageVersion.Default if unset or unparseable.
            LanguageVersion langVersion = LanguageVersion.Default;
            if (result.Properties.TryGetValue(key: "LangVersion", value: out var lv) && lv is not null)
            {
                LanguageVersionFacts.TryParse(lv, out langVersion);
            }

            var parseOptions = new CSharpParseOptions(languageVersion: langVersion, preprocessorSymbols: result.PreprocessorSymbols);

            // Compilation options: OutputKind must be Library for class library / web projects
            // so the compiler doesn't require a Main method (CS5001).  AllowUnsafe and Nullable
            // are also propagated from the MSBuild properties so method resolution succeeds.
            var outputType = result.Properties.TryGetValue(key: "OutputType", value: out var ot) ? ot : "Library";
            var outputKind =
                outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase)
                || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)
                    ? OutputKind.ConsoleApplication
                    : OutputKind.DynamicallyLinkedLibrary;
            var allowUnsafe =
                result.Properties.TryGetValue(key: "AllowUnsafeBlocks", value: out var unsafeStr)
                && bool.TryParse(unsafeStr, out var unsafeBool)
                && unsafeBool;
            var nullableContext =
                result.Properties.TryGetValue(key: "Nullable", value: out var nullableStr)
                && nullableStr?.Equals("enable", StringComparison.OrdinalIgnoreCase) == true
                    ? NullableContextOptions.Enable
                    : NullableContextOptions.Disable;
            var compilationOptions = new CSharpCompilationOptions(
                outputKind,
                allowUnsafe: allowUnsafe,
                nullableContextOptions: nullableContext
            );

            var allRefs = result.References;

            // When a net48 project (like MedDBase.Pages) references a netstandard2.0 library
            // (like MedDBase.DataAccessTier.dll) that was compiled against the netstandard2.0
            // build of a package (e.g. LLBLGen), but Buildalyzer resolves the net452 build for
            // the net48 TFM, the base-type chain inside the netstandard2.0 DLL is unresolvable.
            // To fix this: for every net452 reference we have, also add the netstandard2.0 sibling
            // if it exists, so both assembly identities are in the compilation.
            var siblingRefs = allRefs
                .Where(Exists)
                .Select(r =>
                {
                    var ns20 = r.Replace(
                        Path.DirectorySeparatorChar + "net452" + Path.DirectorySeparatorChar,
                        Path.DirectorySeparatorChar + "netstandard2.0" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    );
                    return ns20 != r && Exists(ns20) ? ns20 : null;
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
                projects
                    .Where(r => r.ProjectFilePath is not null && inWorkspaceProjectPaths.Contains(Path.GetFullPath(r.ProjectFilePath!)))
                    .Select(r =>
                        r.Properties.TryGetValue(key: "AssemblyName", value: out var n)
                            ? n
                            : Path.GetFileNameWithoutExtension(r.ProjectFilePath!)
                    ),
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
                .SelectMany(p => NonCSharpDlls(p!))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var metadataRefs = allRefs
                .Concat(siblingRefs)
                .Concat(fsharpRefDlls)
                .Where(Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // Skip DLLs whose assembly is provided by a live project reference
                .Where(path => !inWorkspaceAssemblyNames.Contains(Path.GetFileNameWithoutExtension(path)))
                .Select(Meta)
                .ToArray();

            var projectRefs = inWorkspaceProjectPaths.Select(p => new ProjectReference(projectIdByPath[p])).ToArray();

            // Wire up Roslyn source generators/analyzers (e.g. proxy code-gen for ClientPage
            // subclasses).  Without these the compilation is missing generated types and semantic
            // analysis fails for files that reference them.
            // Buildalyzer-reported analyzer refs (package analyzers/generators). The project's
            // OutputItemType="Analyzer" ProjectReferences (e.g. the ClientPage proxy generator) are NOT
            // reported here — Buildalyzer drops them — so they're wired separately AFTER the workspace
            // is built (WireGeneratorAnalyzers), by emitting each generator project's compilation.
            var analyzerRefs = result
                .AnalyzerReferences.Where(Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => (AnalyzerReference)new AnalyzerFileReference(path, HostRedirectingAnalyzerLoader.Instance))
                .ToArray();

            var documents = result
                .SourceFiles.Where(Exists)
                .Select(filePath =>
                    DocumentInfo.Create(
                        DocumentId.CreateNewId(projectId),
                        Path.GetFileName(filePath),
                        filePath: filePath,
                        loader: new FileTextLoader(filePath, null)
                    )
                )
                .ToArray();

            var projectName = result.ProjectFilePath is not null ? Path.GetFileNameWithoutExtension(result.ProjectFilePath) : "Unknown";
            var assemblyName = result.Properties.TryGetValue(key: "AssemblyName", value: out var a) ? a : projectName;

            var projectInfo = Microsoft.CodeAnalysis.ProjectInfo.Create(
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

            return projectInfo;
        }

        // Build the ProjectInfos in parallel (the heavy, disk-bound metadata reads); writing each into its
        // own slot preserves input order so the assembled solution is deterministic. AddProject then folds
        // them in serially — a Solution is an immutable snapshot rebuilt on each call, not thread-safe to
        // accumulate concurrently.
        var infos = new Microsoft.CodeAnalysis.ProjectInfo[projects.Count];
        Parallel.For(
            fromInclusive: 0,
            toExclusive: projects.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(val1: 1, val2: parallelism) },
            i => infos[i] = BuildProjectInfo(projects[i])
        );

        foreach (var projectInfo in infos)
        {
            solution = solution.AddProject(projectInfo);
        }

        workspace.TryApplyChanges(solution);
        return workspace;
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

    private static async Task<ProjectSourceLoadResult> LoadProjectSourcesAsync(
        string solutionPath,
        Project project,
        AnalysisRuleSet rules,
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
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

            if (tree is null || root is null || semanticModel is null)
            {
                continue;
            }

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
        foreach (var generated in await RunSourceGeneratorsAsync(project, cancellationToken))
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
    private static async Task<IReadOnlyList<SourceModel>> RunSourceGeneratorsAsync(Project project, CancellationToken cancellationToken)
    {
        var generators = project.AnalyzerReferences.SelectMany(ar => ar.GetGenerators(LanguageNames.CSharp)).ToArray();
        if (generators.Length == 0)
        {
            return [];
        }

        try
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                return [];
            }

            var parseOptions = project.ParseOptions as CSharpParseOptions;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: parseOptions);
            driver = driver.RunGeneratorsAndUpdateCompilation(
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
    private static Buildalyzer.Environment.EnvironmentOptions CompileOnlyOptions()
    {
        var options = new Buildalyzer.Environment.EnvironmentOptions();
        options.TargetsToBuild.Clear();
        options.TargetsToBuild.Add("Compile");
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

            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
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
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

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

using System.Collections.Concurrent;
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
    private static readonly int DefaultParallelism = Math.Max(1, Environment.ProcessorCount);

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
        int? parallelism = null
    )
    {
        var maxParallelism = Math.Max(1, parallelism ?? DefaultParallelism);

        // Buildalyzer invokes MSBuild.exe out-of-process, completely avoiding the
        // System.Collections.Immutable assembly conflict in Roslyn's BuildHost-net472 that
        // causes TypeInitializationException on XMakeElements under VS18/MSBuild18 when
        // loading old-style ToolsVersion="4.0" projects.  (Roslyn PR #83477, not yet released
        // as of May 2026.)  addProjectReferences:false loads project-to-project references
        // from their compiled DLLs rather than re-evaluating the source .csproj files.
        ReportProgress(progress, "Loading solution");
        var workspace = await Task.Run(() => BuildWorkspace(solutionPath, progress, scopeProjectPaths, maxParallelism), cancellationToken)
            .ConfigureAwait(false);

        // Wire OutputItemType="Analyzer" ProjectReferences (source generators like the ClientPage
        // proxy generator) that Buildalyzer drops: emit each generator project's compilation to a temp
        // DLL and add it as an analyzer reference on the referencing project, so RunSourceGenerators
        // can execute it and the generated types get indexed.
        await WireGeneratorAnalyzersAsync(workspace, progress, cancellationToken).ConfigureAwait(false);

        var csharpProjects = workspace
            .CurrentSolution.Projects.Where(p => p.Language == LanguageNames.CSharp)
            .Where(p => !rules.IsExcludedProject(p.Name))
            .ToArray();

        ReportProgress(progress, $"Loaded {csharpProjects.Length} C# project(s) to index");

        var compilationErrors = new ConcurrentBag<string>();
        var compiledProjects = 0;
        var compileSemaphore = new SemaphoreSlim(maxParallelism);
        await Task.WhenAll(
                csharpProjects.Select(async project =>
                {
                    await compileSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var current = Interlocked.Increment(ref compiledProjects);
                        if (ShouldReportProgress(current, csharpProjects.Length))
                            ReportProgress(progress, $"Compiling project {current}/{csharpProjects.Length}: {project.Name}");

                        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                        if (compilation is null)
                        {
                            compilationErrors.Add($"{project.Name}: compilation unavailable");
                            return;
                        }

                        foreach (
                            var diagnostic in compilation
                                .GetDiagnostics(cancellationToken)
                                .Where(d => d.Severity == DiagnosticSeverity.Error)
                        )
                        {
                            compilationErrors.Add($"{project.Name}: {diagnostic}");
                        }
                    }
                    finally
                    {
                        compileSemaphore.Release();
                    }
                })
            )
            .ConfigureAwait(false);

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
                ReportProgress(progress, $"  {error}");
            if (errorCount > 10)
                ReportProgress(progress, $"  ... and {errorCount - 10} more (set --verbose to see all)");
        }

        var projectResults = new ConcurrentBag<ProjectSourceLoadResult>();
        var readProjects = 0;
        var readSemaphore = new SemaphoreSlim(maxParallelism);
        await Task.WhenAll(
                csharpProjects.Select(async project =>
                {
                    await readSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var current = Interlocked.Increment(ref readProjects);
                        if (ShouldReportProgress(current, csharpProjects.Length))
                            ReportProgress(progress, $"Reading source project {current}/{csharpProjects.Length}: {project.Name}");

                        projectResults.Add(
                            await LoadProjectSourcesAsync(solutionPath, project, rules, cancellationToken).ConfigureAwait(false)
                        );
                    }
                    finally
                    {
                        readSemaphore.Release();
                    }
                })
            )
            .ConfigureAwait(false);

        var projectDirectories = csharpProjects
            .Select(p => p.FilePath)
            .Where(path => path is not null)
            .Select(path => Path.GetDirectoryName(path) ?? string.Empty)
            .Where(dir => dir.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SolutionSourceSet(
            projectResults.SelectMany(r => r.SourceFiles).OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            projectResults.SelectMany(r => r.Sources).OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            projectDirectories
        );
    }

    private static AdhocWorkspace BuildWorkspace(
        string solutionPath,
        Action<string>? progress,
        IReadOnlySet<string>? scopeProjectPaths,
        int parallelism
    )
    {
        var logWriter = progress is null ? null : new ProgressLogWriter(progress);
        var options = new AnalyzerManagerOptions { LogWriter = logWriter };

        List<IAnalyzerResult> results;
        if (IsProjectFile(solutionPath))
        {
#pragma warning disable CS0618
            var manager = new AnalyzerManager(options);
            var analyzer = manager.GetProject(solutionPath);
#pragma warning restore CS0618
            progress?.Invoke($"MSBuild: running design-time build for {Path.GetFileNameWithoutExtension(solutionPath)}");
            analyzer!.SetGlobalProperty("DesignTimeBuild", "true");
            analyzer.SetGlobalProperty("BuildingInsideVisualStudio", "true");
            // Prevent the MSBuild compiler server from being shared across parallel processes —
            // concurrent Buildalyzer calls can corrupt each other's bin/ output if they share
            // compilation state.
            analyzer.SetGlobalProperty("UseSharedCompilation", "false");
            var built =
                analyzer.Build().FirstOrDefault()
                ?? throw new InvalidOperationException($"Buildalyzer produced no build results for '{solutionPath}'.");
            results = [built];
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
                .ToArray();

            if (scopeProjectPaths is not null)
                progress?.Invoke(
                    $"Scoped to {toBuild.Length} project(s) in the entry closure "
                        + $"(skipping {manager.Projects.Count - toBuild.Length} out-of-scope / non-C# project(s))"
                );

            // Design-time builds run out-of-process (MSBuild.exe) with UseSharedCompilation=false and
            // emit no binaries, so they parallelise safely — and this is the dominant indexing cost,
            // historically run serially. Parallel.ForEach bounds the concurrent MSBuild processes.
            var resultsBag = new ConcurrentBag<IAnalyzerResult>();
            var done = 0;
            var total = toBuild.Length;
            Parallel.ForEach(
                toBuild,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) },
                projectAnalyzer =>
                {
                    var projectName = projectAnalyzer.ProjectFile.Name;
                    var current = Interlocked.Increment(ref done);
                    if (current == 1 || current == total || current % 10 == 0)
                        ReportProgress(progress, $"MSBuild: design-time build {current}/{total}: {projectName}");
                    projectAnalyzer.SetGlobalProperty("DesignTimeBuild", "true");
                    projectAnalyzer.SetGlobalProperty("UseSharedCompilation", "false");
                    projectAnalyzer.SetGlobalProperty("BuildingInsideVisualStudio", "true");
                    try
                    {
                        var built = projectAnalyzer.Build().FirstOrDefault();
                        if (built is not null)
                            resultsBag.Add(built);
                    }
                    catch (Exception ex)
                    {
                        ReportProgress(progress, $"MSBuild: skipping {projectName} — build failed: {ex.Message.Split('\n')[0].Trim()}");
                    }
                }
            );
            results = resultsBag.ToList();
        }

        return BuildWorkspaceFromResults(results, progress);
    }

    private static AdhocWorkspace BuildWorkspaceFromResults(IReadOnlyList<IAnalyzerResult> analyzerResults, Action<string>? progress = null)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(Microsoft.CodeAnalysis.SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create()));

        // Pass 1 — assign stable ProjectIds keyed by normalised project path so cross-project
        // references can be resolved in pass 2 without depending on ordering.
        var projectIdByPath = analyzerResults
            .Where(r => r.ProjectFilePath is not null)
            .ToDictionary(
                r => Path.GetFullPath(r.ProjectFilePath!),
                r => ProjectId.CreateNewId(r.ProjectFilePath),
                StringComparer.OrdinalIgnoreCase
            );

        // Direct in-set project references per project (normalised paths), for the transitive-closure
        // computation below.
        var directInSetRefsByPath = analyzerResults
            .Where(r => r.ProjectFilePath is not null)
            .ToDictionary(
                r => Path.GetFullPath(r.ProjectFilePath!),
                r => (r.ProjectReferences ?? []).Select(Path.GetFullPath).Where(projectIdByPath.ContainsKey).ToArray(),
                StringComparer.OrdinalIgnoreCase
            );

        // Pass 2 — build each project, converting dep project refs to Roslyn ProjectReferences
        // when the dependency is also in the indexed set.  This gives the semantic model a
        // connected type graph so HasBaseType can traverse across project boundaries.
        foreach (var result in analyzerResults)
        {
            var projectId = result.ProjectFilePath is not null
                ? projectIdByPath.GetValueOrDefault(Path.GetFullPath(result.ProjectFilePath!), ProjectId.CreateNewId())
                : ProjectId.CreateNewId();

            // Language version: read from MSBuild LangVersion property so the parser
            // handles modern C# syntax (primary constructors, collection expressions, etc.).
            // Falls back to LanguageVersion.Default if unset or unparseable.
            LanguageVersion langVersion = LanguageVersion.Default;
            if (result.Properties.TryGetValue("LangVersion", out var lv) && lv is not null)
                Microsoft.CodeAnalysis.CSharp.LanguageVersionFacts.TryParse(lv, out langVersion);
            var parseOptions = new CSharpParseOptions(languageVersion: langVersion, preprocessorSymbols: result.PreprocessorSymbols ?? []);

            // Compilation options: OutputKind must be Library for class library / web projects
            // so the compiler doesn't require a Main method (CS5001).  AllowUnsafe and Nullable
            // are also propagated from the MSBuild properties so method resolution succeeds.
            var outputType = result.Properties.TryGetValue("OutputType", out var ot) ? ot : "Library";
            var outputKind =
                outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase)
                || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)
                    ? OutputKind.ConsoleApplication
                    : OutputKind.DynamicallyLinkedLibrary;
            var allowUnsafe =
                result.Properties.TryGetValue("AllowUnsafeBlocks", out var unsafeStr)
                && bool.TryParse(unsafeStr, out var unsafeBool)
                && unsafeBool;
            var nullableContext =
                result.Properties.TryGetValue("Nullable", out var nullableStr)
                && nullableStr?.Equals("enable", StringComparison.OrdinalIgnoreCase) == true
                    ? NullableContextOptions.Enable
                    : NullableContextOptions.Disable;
            var compilationOptions = new CSharpCompilationOptions(
                outputKind,
                allowUnsafe: allowUnsafe,
                nullableContextOptions: nullableContext
            );

            var allRefs = result.References ?? [];

            // When a net48 project (like MedDBase.Pages) references a netstandard2.0 library
            // (like MedDBase.DataAccessTier.dll) that was compiled against the netstandard2.0
            // build of a package (e.g. LLBLGen), but Buildalyzer resolves the net452 build for
            // the net48 TFM, the base-type chain inside the netstandard2.0 DLL is unresolvable.
            // To fix this: for every net452 reference we have, also add the netstandard2.0 sibling
            // if it exists, so both assembly identities are in the compilation.
            var siblingRefs = allRefs
                .Where(File.Exists)
                .Select(r =>
                {
                    var ns20 = r.Replace(
                        Path.DirectorySeparatorChar + "net452" + Path.DirectorySeparatorChar,
                        Path.DirectorySeparatorChar + "netstandard2.0" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    );
                    return ns20 != r && File.Exists(ns20) ? ns20 : null;
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
                analyzerResults
                    .Where(r => r.ProjectFilePath is not null && inWorkspaceProjectPaths.Contains(Path.GetFullPath(r.ProjectFilePath!)))
                    .Select(r =>
                        r.Properties.TryGetValue("AssemblyName", out var n) ? n : Path.GetFileNameWithoutExtension(r.ProjectFilePath!)
                    ),
                StringComparer.OrdinalIgnoreCase
            );

            var metadataRefs = allRefs
                .Concat(siblingRefs)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // Skip DLLs whose assembly is provided by a live project reference
                .Where(path => !inWorkspaceAssemblyNames.Contains(Path.GetFileNameWithoutExtension(path)))
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToArray();

            var projectRefs = inWorkspaceProjectPaths.Select(p => new ProjectReference(projectIdByPath[p])).ToArray();

            // Wire up Roslyn source generators/analyzers (e.g. proxy code-gen for ClientPage
            // subclasses).  Without these the compilation is missing generated types and semantic
            // analysis fails for files that reference them.
            // Buildalyzer-reported analyzer refs (package analyzers/generators). The project's
            // OutputItemType="Analyzer" ProjectReferences (e.g. the ClientPage proxy generator) are NOT
            // reported here — Buildalyzer drops them — so they're wired separately AFTER the workspace
            // is built (WireGeneratorAnalyzers), by emitting each generator project's compilation.
            var analyzerRefs = (result.AnalyzerReferences ?? [])
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => (AnalyzerReference)new AnalyzerFileReference(path, HostRedirectingAnalyzerLoader.Instance))
                .ToArray();

            var documents = (result.SourceFiles ?? [])
                .Where(File.Exists)
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
            var assemblyName = result.Properties.TryGetValue("AssemblyName", out var a) ? a : projectName;

            var projectInfo = Microsoft.CodeAnalysis.ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                assemblyName,
                LanguageNames.CSharp,
                filePath: result.ProjectFilePath,
                compilationOptions: compilationOptions,
                parseOptions: parseOptions,
                metadataReferences: metadataRefs,
                projectReferences: projectRefs,
                analyzerReferences: analyzerRefs,
                documents: documents
            );

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
                continue;
            var include = reference.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include))
                continue;
            yield return Path.GetFullPath(Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar)));
        }
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
            return closure;

        var start = Path.GetFullPath(projectFilePath);
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!directInSetRefsByPath.TryGetValue(current, out var refs))
                continue;
            foreach (var dep in refs)
                if (closure.Add(dep)) // first time we've seen this dependency
                    stack.Push(dep);
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
                continue;

            var classification = SourceFileClassifier.Classify(solutionPath, project, document.FilePath, rules);
            sourceFiles.Add(
                new SourceFileInfo(
                    project.Name,
                    document.FilePath,
                    classification.Status,
                    classification.Confidence,
                    classification.Basis,
                    classification.Reason,
                    classification.Evidence
                )
            );

            if (classification.Status != "indexed")
                continue;

            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            var root = tree is null ? null : await tree.GetRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

            if (tree is null || root is null || semanticModel is null)
                continue;

            sources.Add(new SourceModel(project.Name, document.FilePath, tree, root, semanticModel));
        }

        // Also index SOURCE-GENERATED documents (Roslyn source generators wired as analyzer refs,
        // e.g. RequestResponseProxyGenerator emitting <Page>Proxy : ProxyBase). These are NOT in
        // project.Documents, and AdhocWorkspace.GetSourceGeneratedDocumentsAsync does not execute
        // generators in this design-time-build setup (it returns nothing). So drive the generators
        // explicitly with a CSharpGeneratorDriver over the project compilation and index the trees it
        // produces — that's what makes the generated proxy base-type facts (the clientpage_proxy
        // effect gate's discriminator) exist.
        foreach (var generated in await RunSourceGeneratorsAsync(project, cancellationToken).ConfigureAwait(false))
        {
            sourceFiles.Add(new SourceFileInfo(project.Name, generated.FilePath, "indexed", "high", "generated", "source_generator", ""));
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
                continue;

            foreach (var generatorProjectPath in AnalyzerProjectReferencePaths(project.FilePath))
            {
                if (!emittedDllByGeneratorPath.TryGetValue(generatorProjectPath, out var dllPath))
                {
                    dllPath = projectByPath.TryGetValue(generatorProjectPath, out var generatorId)
                        ? await EmitCompilationToTempAsync(solution.GetProject(generatorId)!, cancellationToken).ConfigureAwait(false)
                        : null;
                    emittedDllByGeneratorPath[generatorProjectPath] = dllPath;
                }

                if (dllPath is null)
                    continue;

                var reference = new AnalyzerFileReference(dllPath, HostRedirectingAnalyzerLoader.Instance);
                if (!reference.GetGenerators(LanguageNames.CSharp).Any())
                    continue;

                solution = solution.AddAnalyzerReference(project.Id, reference);
                changed = true;
                ReportProgress(
                    progress,
                    $"Wired source generator {Path.GetFileNameWithoutExtension(generatorProjectPath)} -> {project.Name}"
                );
            }
        }

        if (changed)
            workspace.TryApplyChanges(solution);
    }

    // Emits a project's compilation to a temp DLL so its source generators can be loaded as an analyzer
    // reference. Returns null when the compilation is unavailable or fails to emit (then the generator
    // is simply not wired). The temp file is left for the process lifetime (OS temp cleanup).
    private static async Task<string?> EmitCompilationToTempAsync(Project project, CancellationToken cancellationToken)
    {
        try
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                return null;
            var tempDll = Path.Combine(Path.GetTempPath(), $"rig-gen-{project.AssemblyName}-{Guid.NewGuid():N}.dll");
            using var stream = File.Create(tempDll);
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
            return [];

        try
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                return [];

            var parseOptions = project.ParseOptions as CSharpParseOptions;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: parseOptions);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var generatedCompilation, out _, cancellationToken);

            var originalTrees = new HashSet<SyntaxTree>(compilation.SyntaxTrees);
            var results = new List<SourceModel>();
            foreach (var tree in generatedCompilation.SyntaxTrees)
            {
                if (originalTrees.Contains(tree))
                    continue;
                var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                var semanticModel = generatedCompilation.GetSemanticModel(tree);
                // Generated trees carry a generator hint-name path; fall back to a synthetic one.
                var generatedPath = string.IsNullOrEmpty(tree.FilePath)
                    ? $"<generated>/{project.Name}/{results.Count}.g.cs"
                    : tree.FilePath;
                results.Add(new SourceModel(project.Name, generatedPath, tree, root, semanticModel));
            }
            return results;
        }
        catch (Exception)
        {
            // Best-effort: a misbehaving generator must not abort indexing of the real source.
            return [];
        }
    }

    private static bool ShouldReportProgress(int current, int total) => current == 1 || current == total || current % 10 == 0;

    // Progress is reported concurrently from the parallel build/compile/read loops. The contract is
    // that a non-null sink is itself thread-safe: the CLI passes Console.Out (a synchronized
    // SyncTextWriter, atomic per WriteLine), and tests pass no sink at all (null). So no app-level
    // lock is needed — each Invoke writes one whole line without interleaving.
    private static void ReportProgress(Action<string>? progress, string message) => progress?.Invoke(message);

    private static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

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
            if (Interlocked.Exchange(ref _hooked, 1) != 0)
                return;
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                if (name.Name is null)
                    return null;
                var redirect =
                    name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                    || name.Name is "System.Collections.Immutable" or "System.Reflection.Metadata";
                if (!redirect)
                    return null;
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
                return;
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

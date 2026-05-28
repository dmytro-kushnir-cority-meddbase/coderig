using System.Collections.Concurrent;
using Buildalyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Rig.Analysis.Rules;
using Rig.Domain.Data;

namespace Rig.Analysis.Inventory;

internal static class SolutionSourceLoader
{
    private static readonly object ProgressLock = new();
    private static readonly int MaxParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

    public static async Task<SolutionSourceSet> LoadAsync(
        string solutionPath,
        AnalysisRuleSet rules,
        CancellationToken cancellationToken,
        Action<string>? progress = null
    )
    {

        // Buildalyzer invokes MSBuild.exe out-of-process, completely avoiding the
        // System.Collections.Immutable assembly conflict in Roslyn's BuildHost-net472 that
        // causes TypeInitializationException on XMakeElements under VS18/MSBuild18 when
        // loading old-style ToolsVersion="4.0" projects.  (Roslyn PR #83477, not yet released
        // as of May 2026.)  addProjectReferences:false loads project-to-project references
        // from their compiled DLLs rather than re-evaluating the source .csproj files.
        ReportProgress(progress, "Loading solution");
        var workspace = await Task.Run(() => BuildWorkspace(solutionPath, progress), cancellationToken)
            .ConfigureAwait(false);

        var csharpProjects = workspace.CurrentSolution.Projects
            .Where(p => p.Language == LanguageNames.CSharp)
            .Where(p => !rules.IsExcludedProject(p.Name))
            .ToArray();

        ReportProgress(progress, $"Loaded {csharpProjects.Length} C# project(s) to index");

        var compilationErrors = new ConcurrentBag<string>();
        var compiledProjects = 0;
        var compileSemaphore = new SemaphoreSlim(MaxParallelism);
        await Task.WhenAll(csharpProjects.Select(async project =>
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

                foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken)
                    .Where(d => d.Severity == DiagnosticSeverity.Error))
                {
                    compilationErrors.Add($"{project.Name}: {diagnostic}");
                }
            }
            finally
            {
                compileSemaphore.Release();
            }
        })).ConfigureAwait(false);

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
        var readSemaphore = new SemaphoreSlim(MaxParallelism);
        await Task.WhenAll(csharpProjects.Select(async project =>
        {
            await readSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = Interlocked.Increment(ref readProjects);
                if (ShouldReportProgress(current, csharpProjects.Length))
                    ReportProgress(progress, $"Reading source project {current}/{csharpProjects.Length}: {project.Name}");

                projectResults.Add(await LoadProjectSourcesAsync(solutionPath, project, rules, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                readSemaphore.Release();
            }
        })).ConfigureAwait(false);

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

    private static AdhocWorkspace BuildWorkspace(string solutionPath, Action<string>? progress)
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
            var built = analyzer.Build().FirstOrDefault()
                ?? throw new InvalidOperationException($"Buildalyzer produced no build results for '{solutionPath}'.");
            results = [built];
        }
        else
        {
#pragma warning disable CS0618
            var manager = new AnalyzerManager(solutionPath, options);
#pragma warning restore CS0618
            results = [];
            foreach (var projectAnalyzer in manager.Projects.Values)
            {
                progress?.Invoke($"MSBuild: running design-time build for {projectAnalyzer.ProjectFile.Name}");
                projectAnalyzer.SetGlobalProperty("DesignTimeBuild", "true");
                projectAnalyzer.SetGlobalProperty("BuildingInsideVisualStudio", "true");
                var built = projectAnalyzer.Build().FirstOrDefault();
                if (built is not null) results.Add(built);
            }
        }

        return BuildWorkspaceFromResults(results);
    }

    private static AdhocWorkspace BuildWorkspaceFromResults(IReadOnlyList<IAnalyzerResult> analyzerResults)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(Microsoft.CodeAnalysis.SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create()));

        foreach (var result in analyzerResults)
        {
            var projectId = ProjectId.CreateNewId(result.ProjectFilePath);
            var parseOptions = new CSharpParseOptions(
                preprocessorSymbols: result.PreprocessorSymbols ?? []);

            var metadataRefs = (result.References ?? [])
                .Where(File.Exists)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToArray();

            var documents = (result.SourceFiles ?? [])
                .Where(File.Exists)
                .Select(filePath => DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(filePath),
                    filePath: filePath,
                    loader: new FileTextLoader(filePath, null)))
                .ToArray();

            var projectName = result.ProjectFilePath is not null
                ? Path.GetFileNameWithoutExtension(result.ProjectFilePath)
                : "Unknown";
            var assemblyName = result.Properties.TryGetValue("AssemblyName", out var a) ? a : projectName;

            var projectInfo = Microsoft.CodeAnalysis.ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                assemblyName,
                LanguageNames.CSharp,
                filePath: result.ProjectFilePath,
                metadataReferences: metadataRefs,
                documents: documents,
                parseOptions: parseOptions);

            solution = solution.AddProject(projectInfo);
        }

        workspace.TryApplyChanges(solution);
        return workspace;
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

        foreach (var document in project.Documents
            .Where(d => d.FilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (document.FilePath is null) continue;

            var classification = SourceFileClassifier.Classify(solutionPath, project, document.FilePath, rules);
            sourceFiles.Add(new SourceFileInfo(
                project.Name, document.FilePath,
                classification.Status, classification.Confidence,
                classification.Basis, classification.Reason, classification.Evidence));

            if (classification.Status != "indexed") continue;

            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            var root = tree is null ? null : await tree.GetRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

            if (tree is null || root is null || semanticModel is null) continue;

            sources.Add(new SourceModel(project.Name, document.FilePath, tree, root, semanticModel));
        }

        return new ProjectSourceLoadResult(sourceFiles, sources);
    }

    private static bool ShouldReportProgress(int current, int total)
        => current == 1 || current == total || current % 10 == 0;

    private static void ReportProgress(Action<string>? progress, string message)
    {
        if (progress is null) return;
        lock (ProgressLock) { progress(message); }
    }

    private static bool IsProjectFile(string path)
        => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

    private sealed record ProjectSourceLoadResult(IReadOnlyList<SourceFileInfo> SourceFiles, IReadOnlyList<SourceModel> Sources);

    private sealed class ProgressLogWriter(Action<string> progress) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            // Only surface high-signal lines from MSBuild output to avoid noise
            if (value.Contains("error", StringComparison.OrdinalIgnoreCase)
                || value.Contains("warning", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Build succeeded", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Build FAILED", StringComparison.OrdinalIgnoreCase))
            {
                progress($"MSBuild: {value.Trim()}");
            }
        }
    }
}

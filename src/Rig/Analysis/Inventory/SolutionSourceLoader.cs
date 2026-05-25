using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Rig.Analysis;

internal static class SolutionSourceLoader
{
    private static readonly object MSBuildRegistrationLock = new();
    private static readonly object ProgressLock = new();
    private static readonly int MaxParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

    public static async Task<SolutionSourceSet> LoadAsync(
        string solutionPath,
        AnalysisRuleSet rules,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        RegisterMSBuild();

        var workspace = MSBuildWorkspace.Create();
        var loadProgress = progress is null
            ? null
            : new InlineProgress<ProjectLoadProgress>(load => ReportProgress(progress, FormatProjectLoadProgress(load)));
        var solution = await workspace.OpenSolutionAsync(solutionPath, loadProgress, cancellationToken);

        var workspaceFailures = workspace.Diagnostics
            .Where(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            .Select(diagnostic => diagnostic.Message)
            .ToArray();
        if (workspaceFailures.Length > 0)
        {
            throw new InvalidOperationException("Roslyn workspace failed to load the solution:" + Environment.NewLine +
                string.Join(Environment.NewLine, workspaceFailures));
        }

        var csharpProjects = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp)
            .Where(p => !rules.IsExcludedProject(p.Name))
            .ToArray();

        ReportProgress(progress, $"Loaded {solution.Projects.Count()} projects; indexing {csharpProjects.Length} C# projects");

        var compilationErrors = new ConcurrentBag<string>();
        var compiledProjects = 0;
        await Parallel.ForEachAsync(
            csharpProjects,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxParallelism
            },
            async (project, ct) =>
            {
                var current = Interlocked.Increment(ref compiledProjects);
                if (ShouldReportProgress(current, csharpProjects.Length))
                {
                    ReportProgress(progress, $"Compiling project {current}/{csharpProjects.Length}: {project.Name}");
                }

                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null)
                {
                    compilationErrors.Add($"{project.Name}: compilation unavailable");
                    return;
                }

                foreach (var diagnostic in compilation.GetDiagnostics(ct)
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    compilationErrors.Add($"{project.Name}: {diagnostic}");
                }
            });

        var compilationErrorList = compilationErrors
            .OrderBy(error => error, StringComparer.Ordinal)
            .ToArray();
        if (compilationErrorList.Length > 0)
        {
            throw new InvalidOperationException("Compilation failed for indexed projects:" + Environment.NewLine +
                string.Join(Environment.NewLine, compilationErrorList));
        }

        var projectResults = new ConcurrentBag<ProjectSourceLoadResult>();
        var readProjects = 0;
        await Parallel.ForEachAsync(
            csharpProjects,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxParallelism
            },
            async (project, ct) =>
            {
                var current = Interlocked.Increment(ref readProjects);
                if (ShouldReportProgress(current, csharpProjects.Length))
                {
                    ReportProgress(progress, $"Reading source project {current}/{csharpProjects.Length}: {project.Name}");
                }

                projectResults.Add(await LoadProjectSourcesAsync(solutionPath, project, rules, ct));
            });

        var projectDirectories = csharpProjects
            .Select(p => p.FilePath)
            .Where(path => path is not null)
            .Select(path => Path.GetDirectoryName(path!)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SolutionSourceSet(
            projectResults
                .SelectMany(result => result.SourceFiles)
                .OrderBy(sourceFile => sourceFile.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            projectResults
                .SelectMany(result => result.Sources)
                .OrderBy(source => source.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            projectDirectories);
    }

    private static async Task<ProjectSourceLoadResult> LoadProjectSourcesAsync(
        string solutionPath,
        Project project,
        AnalysisRuleSet rules,
        CancellationToken cancellationToken)
    {
        var sources = new List<SourceModel>();
        var sourceFiles = new List<SourceFileInfo>();

        foreach (var document in project.Documents
            .Where(document => document.FilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (document.FilePath is null)
            {
                continue;
            }

            var classification = ClassifySourceFile(solutionPath, project, document.FilePath, rules);
            sourceFiles.Add(new SourceFileInfo(
                project.Name,
                document.FilePath,
                classification.Status,
                classification.Confidence,
                classification.Basis,
                classification.Reason,
                classification.Evidence));

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

            sources.Add(new SourceModel(project.Name, document.FilePath, tree, root, semanticModel));
        }

        return new ProjectSourceLoadResult(sourceFiles, sources);
    }

    private static bool ShouldReportProgress(int current, int total)
    {
        return current == 1 || current == total || current % 10 == 0;
    }

    private static string FormatProjectLoadProgress(ProjectLoadProgress progress)
    {
        var projectName = Path.GetFileNameWithoutExtension(progress.FilePath);
        var targetFramework = string.IsNullOrWhiteSpace(progress.TargetFramework)
            ? ""
            : $" {progress.TargetFramework}";
        return $"MSBuild {progress.Operation}: {projectName}{targetFramework} ({progress.ElapsedTime.TotalSeconds:n1}s)";
    }

    private static void ReportProgress(Action<string>? progress, string message)
    {
        if (progress is null)
        {
            return;
        }

        lock (ProgressLock)
        {
            progress(message);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }

    private sealed record ProjectSourceLoadResult(
        IReadOnlyList<SourceFileInfo> SourceFiles,
        IReadOnlyList<SourceModel> Sources);

    private static SourceFileClassification ClassifySourceFile(
        string solutionPath,
        Project project,
        string filePath,
        AnalysisRuleSet rules)
    {
        var relativePath = GetRelativePath(solutionPath, filePath);
        var excludedByRule = rules.FindExcludedFile(relativePath);
        if (excludedByRule is not null)
        {
            return new SourceFileClassification(
                "skipped",
                "high",
                "profile",
                excludedByRule.Reason,
                excludedByRule.Id);
        }

        var includedByRule = rules.FindIncludedFile(relativePath);
        if (includedByRule is not null)
        {
            return new SourceFileClassification(
                "indexed",
                "high",
                "profile",
                includedByRule.Reason,
                includedByRule.Id);
        }

        if (rules.IsTestProject(project.Name))
        {
            return new SourceFileClassification(
                "skipped",
                "medium",
                "convention",
                "test_source",
                project.Name);
        }

        return new SourceFileClassification(
            "indexed",
            "high",
            "compilation",
            "project_document",
            relativePath);
    }

    private static string GetRelativePath(string solutionPath, string filePath)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();
        return Path.GetRelativePath(solutionDirectory, filePath).Replace('\\', '/');
    }

    private static void RegisterMSBuild()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        lock (MSBuildRegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }
}

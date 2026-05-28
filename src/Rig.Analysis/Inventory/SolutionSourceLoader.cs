using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Rig.Analysis.Rules;
using Rig.Domain.Data;

namespace Rig.Analysis.Inventory;

internal static class SolutionSourceLoader
{
    private static readonly object MSBuildRegistrationLock = new();
    private static readonly object ProgressLock = new();
    private static readonly int MaxParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

    public static async Task<SolutionSourceSet> LoadAsync(
        string solutionPath,
        AnalysisRuleSet rules,
        CancellationToken cancellationToken,
        Action<string>? progress = null
    )
    {
        var msbuildInstance = RegisterMSBuild();

        // DesignTimeBuild skips actual build execution (codegen, resource compilation)
        // so MSBuild only evaluates project properties and item lists — which is all we need.
        // BuildingInsideVisualStudio suppresses project-system-specific targets that may fail
        // when invoked outside VS.
        var workspaceProperties = new Dictionary<string, string>
        {
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true",
        };
        if (msbuildInstance is not null)
            workspaceProperties["MSBuildExtensionsPath"] = msbuildInstance.MSBuildPath;

        var workspace = MSBuildWorkspace.Create(workspaceProperties);
        var loadProgress = progress is null
            ? null
            : new InlineProgress<ProjectLoadProgress>(load => ReportProgress(progress, FormatProjectLoadProgress(load)));

        Project[] csharpProjects;
        if (IsProjectFile(solutionPath))
        {
            var project = await workspace.OpenProjectAsync(solutionPath, loadProgress, cancellationToken);
            csharpProjects = project.Language == LanguageNames.CSharp && !rules.IsExcludedProject(project.Name)
                ? [project]
                : [];
        }
        else
        {
            var solution = await workspace.OpenSolutionAsync(solutionPath, loadProgress, cancellationToken);
            csharpProjects = solution
                .Projects.Where(p => p.Language == LanguageNames.CSharp)
                .Where(p => !rules.IsExcludedProject(p.Name))
                .ToArray();
        }

        var workspaceFailures = workspace
            .Diagnostics.Where(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            .Where(diagnostic => !IsExcludedProjectDiagnostic(diagnostic.Message, rules))
            .Select(diagnostic => diagnostic.Message)
            .ToArray();
        if (workspaceFailures.Length > 0)
        {
            throw new InvalidOperationException(
                "Roslyn workspace failed to load the solution:" + Environment.NewLine + string.Join(Environment.NewLine, workspaceFailures)
            );
        }

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
                {
                    ReportProgress(progress, $"Compiling project {current}/{csharpProjects.Length}: {project.Name}");
                }

                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    compilationErrors.Add($"{project.Name}: compilation unavailable");
                    return;
                }

                foreach (
                    var diagnostic in compilation.GetDiagnostics(cancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                )
                {
                    compilationErrors.Add($"{project.Name}: {diagnostic}");
                }
            }
            finally
            {
                compileSemaphore.Release();
            }
        })).ConfigureAwait(false);

        var compilationErrorList = compilationErrors.OrderBy(error => error, StringComparer.Ordinal).ToArray();
        if (compilationErrorList.Length > 0)
        {
            throw new InvalidOperationException(
                "Compilation failed for indexed projects:" + Environment.NewLine + string.Join(Environment.NewLine, compilationErrorList)
            );
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
                {
                    ReportProgress(progress, $"Reading source project {current}/{csharpProjects.Length}: {project.Name}");
                }

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
            projectResults
                .SelectMany(result => result.SourceFiles)
                .OrderBy(sourceFile => sourceFile.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            projectResults
                .SelectMany(result => result.Sources)
                .OrderBy(source => source.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            projectDirectories
        );
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
                .Documents.Where(document => document.FilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase)
        )
        {
            if (document.FilePath is null)
            {
                continue;
            }

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
        var targetFramework = string.IsNullOrWhiteSpace(progress.TargetFramework) ? "" : $" {progress.TargetFramework}";
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

    private sealed record ProjectSourceLoadResult(IReadOnlyList<SourceFileInfo> SourceFiles, IReadOnlyList<SourceModel> Sources);

    private static bool IsProjectFile(string path)
    {
        return path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedProjectDiagnostic(string diagnosticMessage, AnalysisRuleSet rules)
    {
        // Diagnostic messages contain the .csproj path; extract the project name and check exclusion rules.
        // Example: "Msbuild failed when processing the file 'C:\...\Foo.AppHost.csproj' with message: ..."
        var match = System.Text.RegularExpressions.Regex.Match(diagnosticMessage, @"'([^']+\.csproj)'");
        if (!match.Success)
            return false;

        var projectName = Path.GetFileNameWithoutExtension(match.Groups[1].Value);
        return rules.IsExcludedProject(projectName);
    }

    private static VisualStudioInstance? _registeredInstance;

    private static VisualStudioInstance? RegisterMSBuild()
    {
        if (MSBuildLocator.IsRegistered)
            return _registeredInstance;

        lock (MSBuildRegistrationLock)
        {
            if (MSBuildLocator.IsRegistered)
                return _registeredInstance;

            // Prefer a VS2022+ instance found via VS Setup API; fall back to RegisterDefaults.
            var instance = MSBuildLocator
                .QueryVisualStudioInstances()
                .Where(i => i.DiscoveryType == DiscoveryType.VisualStudioSetup)
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();

            if (instance is not null)
            {
                // Expose the MSBuild.exe path so the BuildHost-net472 subprocess launched by
                // Roslyn finds the same MSBuild DLLs as the main process.  Without this the
                // BuildHost may fail to initialise Microsoft.Build.Shared.XMakeElements on
                // machines where MSBUILD_EXE_PATH is not set in the shell environment.
                var msbuildExe = Path.Combine(instance.MSBuildPath, "MSBuild.exe");
                if (File.Exists(msbuildExe))
                    Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", msbuildExe);

                MSBuildLocator.RegisterInstance(instance);
                _registeredInstance = instance;
            }
            else
            {
                MSBuildLocator.RegisterDefaults();
            }

            return _registeredInstance;
        }
    }
}

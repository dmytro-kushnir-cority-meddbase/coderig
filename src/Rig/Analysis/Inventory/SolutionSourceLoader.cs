using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Rig.Analysis;

internal static class SolutionSourceLoader
{
    private static readonly object MSBuildRegistrationLock = new();

    public static async Task<SolutionSourceSet> LoadAsync(
        string solutionPath,
        AnalysisRuleSet rules,
        CancellationToken cancellationToken)
    {
        RegisterMSBuild();

        var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);

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
            .ToArray();

        var compilationErrors = new List<string>();
        foreach (var project in csharpProjects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                compilationErrors.Add($"{project.Name}: compilation unavailable");
                continue;
            }

            compilationErrors.AddRange(compilation.GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => $"{project.Name}: {diagnostic}"));
        }

        if (compilationErrors.Count > 0)
        {
            throw new InvalidOperationException("Compilation failed for indexed projects:" + Environment.NewLine +
                string.Join(Environment.NewLine, compilationErrors));
        }

        var sources = new List<SourceModel>();
        var sourceFiles = new List<SourceFileInfo>();
        foreach (var project in csharpProjects)
        {
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
        }

        var projectDirectories = csharpProjects
            .Select(p => p.FilePath)
            .Where(path => path is not null)
            .Select(path => Path.GetDirectoryName(path!)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SolutionSourceSet(
            sourceFiles
                .OrderBy(sourceFile => sourceFile.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            sources
                .OrderBy(source => source.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            projectDirectories);
    }

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

using Microsoft.CodeAnalysis;

namespace Rig.Analysis;

internal static class SourceFileClassifier
{
    public static SourceFileClassification Classify(
        string solutionPath,
        Project project,
        string filePath,
        AnalysisRuleSet rules)
    {
        return Classify(solutionPath, project.Name, filePath, rules);
    }

    public static SourceFileClassification Classify(
        string solutionPath,
        string projectName,
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

        if (rules.IsTestProject(projectName))
        {
            return new SourceFileClassification(
                "skipped",
                "medium",
                "convention",
                "test_source",
                projectName);
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
}

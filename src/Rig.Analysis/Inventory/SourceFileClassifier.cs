using Microsoft.CodeAnalysis;
using Rig.Analysis.Rules;

namespace Rig.Analysis.Inventory;

internal static class SourceFileClassifier
{
    public static SourceFileClassification Classify(string solutionPath, Project project, string filePath, AnalysisRuleSet rules)
    {
        return Classify(solutionPath: solutionPath, projectName: project.Name, filePath: filePath, rules: rules);
    }

    public static SourceFileClassification Classify(string solutionPath, string projectName, string filePath, AnalysisRuleSet rules)
    {
        var relativePath = GetRelativePath(solutionPath, filePath);
        var excludedByRule = rules.FindExcludedFile(relativePath);
        if (excludedByRule is not null)
        {
            return new SourceFileClassification(
                Status: "skipped",
                Confidence: "high",
                Basis: "profile",
                Reason: excludedByRule.Reason,
                Evidence: excludedByRule.Id
            );
        }

        var includedByRule = rules.FindIncludedFile(relativePath);
        if (includedByRule is not null)
        {
            return new SourceFileClassification(
                Status: "indexed",
                Confidence: "high",
                Basis: "profile",
                Reason: includedByRule.Reason,
                Evidence: includedByRule.Id
            );
        }

        if (rules.IsTestProject(projectName))
        {
            return new SourceFileClassification(
                Status: "skipped",
                Confidence: "medium",
                Basis: "convention",
                Reason: "test_source",
                Evidence: projectName
            );
        }

        return new SourceFileClassification(
            Status: "indexed",
            Confidence: "high",
            Basis: "compilation",
            Reason: "project_document",
            Evidence: relativePath
        );
    }

    private static string GetRelativePath(string solutionPath, string filePath)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();
        return Path.GetRelativePath(relativeTo: solutionDirectory, path: filePath).Replace(oldChar: '\\', newChar: '/');
    }
}

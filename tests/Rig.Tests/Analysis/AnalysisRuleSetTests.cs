using Rig.Analysis;
using Rig.Analysis.Rules;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Analysis;

public sealed class AnalysisRuleSetTests
{
    [Test]
    public void LoadForSolution_rejects_file_rules_without_id()
    {
        using var workspace = TempRulesWorkspace.Create(
            """
            {
              "files": {
                "exclude": [{ "glob": "**/*.g.cs", "reason": "generated" }]
              }
            }
            """
        );

        var exception = Should.Throw<InvalidOperationException>(() => AnalysisRuleSet.LoadForSolution(workspace.SolutionPath));

        exception.Message.ShouldContain("File rule in `exclude` is missing `id`.");
    }

    [Test]
    public void LoadForSolution_rejects_file_rules_without_glob()
    {
        using var workspace = TempRulesWorkspace.Create(
            """
            {
              "files": {
                "include": [{ "id": "include-contract", "reason": "contract_fixture" }]
              }
            }
            """
        );

        var exception = Should.Throw<InvalidOperationException>(() => AnalysisRuleSet.LoadForSolution(workspace.SolutionPath));

        exception.Message.ShouldContain("File rule `include-contract` is missing `glob`.");
    }

    [Test]
    public void LoadForSolution_merges_solution_and_extra_rules()
    {
        using var workspace = TempRulesWorkspace.Create(
            solutionRulesJson: """
            {
              "files": {
                "testProjectPatterns": ["*.Tests"]
              }
            }
            """,
            extraRulesJson: """
            {
              "projects": {
                "exclude": ["*.AppHost"]
              }
            }
            """
        );

        var rules = AnalysisRuleSet.LoadForSolution(workspace.SolutionPath, [workspace.ExtraRulesPath]);

        rules.IsTestProject("Rig.Tests").ShouldBeTrue();
        rules.IsExcludedProject("Sample.AppHost").ShouldBeTrue();
    }

    private sealed class TempRulesWorkspace : IDisposable
    {
        private TempRulesWorkspace(string directory, string solutionPath, string extraRulesPath)
        {
            DirectoryPath = directory;
            SolutionPath = solutionPath;
            ExtraRulesPath = extraRulesPath;
        }

        public string DirectoryPath { get; }
        public string SolutionPath { get; }
        public string ExtraRulesPath { get; }

        public static TempRulesWorkspace Create(string solutionRulesJson, string? extraRulesJson = null)
        {
            var directory = Directory.CreateTempSubdirectory("rig-rules-").FullName;
            var solutionPath = Path.Combine(directory, "Sample.slnx");
            var extraRulesPath = Path.Combine(directory, "extra.rules.json");

            File.WriteAllText(solutionPath, "<Solution />");
            File.WriteAllText(Path.Combine(directory, "rig.rules.json"), solutionRulesJson);
            File.WriteAllText(extraRulesPath, extraRulesJson ?? "{}");

            return new TempRulesWorkspace(directory, solutionPath, extraRulesPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}

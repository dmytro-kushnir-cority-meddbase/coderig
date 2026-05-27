using System.Text.Json;
using Rig.Analysis;
using Rig.Analysis.Analysis.Inventory;
using Rig.Analysis.Analysis.Rules;
using Shouldly;

namespace Rig.Tests.Analysis;

public sealed class SourceFileClassifierTests
{
    [Fact]
    public void Exclude_rules_take_precedence_over_include_rules()
    {
        var rules = Rules(
            """
            {
              "files": {
                "include": [{ "id": "include-generated", "glob": "**/Generated/*.g.cs", "reason": "explicit_include" }],
                "exclude": [{ "id": "exclude-generated", "glob": "**/Generated/*.g.cs", "reason": "generated_fixture" }]
              }
            }
            """
        );

        var classification = SourceFileClassifier.Classify(
            SolutionPath(),
            "EntryPointEffects.Api",
            Path.Combine(
                SolutionDirectory(),
                "EntryPointEffects.Api",
                "Generated",
                "Endpoint.g.cs"
            ),
            rules
        );

        classification.Status.ShouldBe("skipped");
        classification.Basis.ShouldBe("profile");
        classification.Reason.ShouldBe("generated_fixture");
        classification.Evidence.ShouldBe("exclude-generated");
    }

    [Fact]
    public void Include_rules_can_force_test_project_source_to_index()
    {
        var rules = Rules(
            """
            {
              "files": {
                "include": [{ "id": "include-contract", "glob": "**/ContractFixture.cs", "reason": "contract_fixture" }],
                "testProjectPatterns": ["*.Tests"]
              }
            }
            """
        );

        var classification = SourceFileClassifier.Classify(
            SolutionPath(),
            "Rig.Tests",
            Path.Combine(SolutionDirectory(), "Rig.Tests", "ContractFixture.cs"),
            rules
        );

        classification.Status.ShouldBe("indexed");
        classification.Basis.ShouldBe("profile");
        classification.Reason.ShouldBe("contract_fixture");
        classification.Evidence.ShouldBe("include-contract");
    }

    [Fact]
    public void Test_project_patterns_skip_sources_by_convention()
    {
        var rules = Rules(
            """
            {
              "files": {
                "testProjectPatterns": ["*.Tests"]
              }
            }
            """
        );

        var classification = SourceFileClassifier.Classify(
            SolutionPath(),
            "Rig.Tests",
            Path.Combine(SolutionDirectory(), "Rig.Tests", "SomeTest.cs"),
            rules
        );

        classification.Status.ShouldBe("skipped");
        classification.Confidence.ShouldBe("medium");
        classification.Basis.ShouldBe("convention");
        classification.Reason.ShouldBe("test_source");
        classification.Evidence.ShouldBe("Rig.Tests");
    }

    [Fact]
    public void Normal_source_files_are_indexed_with_relative_path_evidence()
    {
        var classification = SourceFileClassifier.Classify(
            SolutionPath(),
            "Rig",
            Path.Combine(SolutionDirectory(), "src", "Rig", "Program.cs"),
            Rules("{}")
        );

        classification.Status.ShouldBe("indexed");
        classification.Basis.ShouldBe("compilation");
        classification.Reason.ShouldBe("project_document");
        classification.Evidence.ShouldBe("src/Rig/Program.cs");
    }

    private static AnalysisRuleSet Rules(string json)
    {
        var document = JsonSerializer.Deserialize<AnalysisRulesDocument>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );
        document.ShouldNotBeNull();

        return new AnalysisRuleSet(
            [],
            [],
            [],
            [],
            [],
            document.Files?.Include?.Select(rule => rule.ToFileRule("include")).ToArray() ?? [],
            document.Files?.Exclude?.Select(rule => rule.ToFileRule("exclude")).ToArray() ?? [],
            document.Files?.TestProjectPatterns ?? [],
            document.Projects?.Exclude ?? []
        );
    }

    private static string SolutionPath()
    {
        return Path.Combine(SolutionDirectory(), "Test.slnx");
    }

    private static string SolutionDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "rig-classifier-tests");
    }
}

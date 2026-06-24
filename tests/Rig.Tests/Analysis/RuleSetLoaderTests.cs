using Rig.Analysis.Rules;
using Shouldly;

namespace Rig.Tests.Analysis;

public sealed class RuleSetLoaderTests
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

        var exception = Should.Throw<InvalidOperationException>(() => RuleSetLoader.LoadForSolution(workspace.SolutionPath));

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

        var exception = Should.Throw<InvalidOperationException>(() => RuleSetLoader.LoadForSolution(workspace.SolutionPath));

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

        var rules = RuleSetLoader.LoadForSolution(workspace.SolutionPath, [workspace.ExtraRulesPath]);

        rules.IsTestProject("Rig.Tests").ShouldBeTrue();
        rules.IsExcludedProject("Sample.AppHost").ShouldBeTrue();
    }

    [Test]
    public void TypeEntryPoints_requires_is_parsed_and_projected_to_the_fact_rule()
    {
        using var workspace = TempRulesWorkspace.Create(
            """
            {
              "entrypoints": {
                "typeEntryPoints": [
                  { "id": "t1", "kind": "page", "baseTypes": ["App.PageBase"], "namespacePrefix": "App.Pages.", "requires": ["FrontEnd", "BackEnd"] }
                ]
              }
            }
            """
        );

        var projected = RuleSetLoader.Load(workspace.DirectoryPath).EntryPoints;

        projected.ShouldHaveSingleItem().Requires.ShouldBe(["FrontEnd", "BackEnd"]);
    }

    [Test]
    public void PageModel_is_a_back_compat_alias_for_typeEntryPoints()
    {
        // The framework-specific `pageModel` key was generalised to `typeEntryPoints`; existing configs
        // using the old key must keep loading (merged into the same collection).
        using var workspace = TempRulesWorkspace.Create(
            """
            {
              "entrypoints": {
                "pageModel": [
                  { "id": "legacy", "kind": "page", "baseTypes": ["App.PageBase"], "namespacePrefix": "App.Pages." }
                ]
              }
            }
            """
        );

        var rules = RuleSetLoader.LoadForSolution(workspace.SolutionPath);

        rules.EntryPoints.ShouldHaveSingleItem().Id.ShouldBe("legacy");
    }

    [Test]
    public void HandoffDispatcher_requires_is_parsed_and_projected()
    {
        using var workspace = TempRulesWorkspace.Create(
            """
            {
              "handoffDispatchers": [
                { "id": "bg", "kind": "background", "consumerPatterns": ["Schedule.#ctor"], "requires": ["FrontEnd"] }
              ]
            }
            """
        );

        var projected = RuleSetLoader.Load(workspace.DirectoryPath).Handoff;

        projected.ShouldHaveSingleItem().Requires.ShouldBe(["FrontEnd"]);
    }

    [Test]
    public void DeliveryRules_round_trip_into_RuleSet_Delivery()
    {
        using var workspace = TempRulesWorkspace.Create(
            """
            {
              "deliveryRules": [
                {
                  "id": "echo-actor",
                  "tag": "actor_tell",
                  "confidence": "heuristic",
                  "producer": {
                    "source": "arg", "resolve": "path", "argumentIndex": 0,
                    "methods": ["tell", "ask"], "declaringTypes": ["Echo.Process"]
                  },
                  "registration": {
                    "source": "arg", "resolve": "path", "argumentIndex": 0,
                    "methods": ["spawn", "register"], "declaringTypes": ["Echo.Process"]
                  }
                }
              ]
            }
            """
        );

        // The cascade merges the builtin deliveryRules first, then the workspace's; assert on the
        // workspace's `echo-actor` overlaid copy (the LAST one in load order carries the test's method lists).
        var delivery = RuleSetLoader.Load(workspace.DirectoryPath).Delivery;

        var rule = delivery.Last(r => r.Id == "echo-actor");
        rule.Tag.ShouldBe("actor_tell");
        rule.Confidence.ShouldBe("heuristic");
        rule.Producer.Source.ShouldBe("arg");
        rule.Producer.Resolve.ShouldBe("path");
        rule.Producer.Methods.ShouldBe(["tell", "ask"]);
        rule.Producer.DeclaringTypes.ShouldBe(["Echo.Process"]);
        rule.Registration.Methods.ShouldBe(["spawn", "register"]);
    }

    [Test]
    public void RedirectRules_round_trip_into_RuleSet_Redirect_through_the_cascade_merge()
    {
        // Regression: the cascade Merge must carry the local `redirectRules` section into RuleSet.Redirect.
        // It was initially omitted from Merge, so a colocated rule silently vanished — caught only on the real
        // store (the suite missed it because tests that construct rules directly bypass the loader cascade).
        using var workspace = TempRulesWorkspace.Create(
            """
            {
              "redirectRules": [
                { "method": "M:Ext.EntityBase.Save", "redirectTo": "M:Ext.EntityBase.Save(Ext.IPredicate,System.Boolean)" }
              ]
            }
            """
        );

        var redirect = RuleSetLoader.Load(workspace.DirectoryPath).Redirect;

        var rule = redirect.ShouldHaveSingleItem();
        rule.Method.ShouldBe("M:Ext.EntityBase.Save");
        rule.RedirectTo.ShouldBe("M:Ext.EntityBase.Save(Ext.IPredicate,System.Boolean)");
    }

    [Test]
    public void CacheCoherence_round_trips_into_RuleSet_through_the_cascade_merge()
    {
        // Regression mirror of the redirectRules test: the cascade Merge must carry the local `cacheCoherence`
        // section (a single object, last-writer-wins) into RuleSet.CacheCoherence. A section omitted from Merge
        // silently vanishes from the cascade.
        using var workspace = TempRulesWorkspace.Create(
            """
            {
              "cacheCoherence": {
                "cachedEntities": ["Account", "Person"],
                "excludeEnclosingNamespaceSuffix": ["CollectionClasses", "DaoClasses"]
              }
            }
            """
        );

        var cacheCoherence = RuleSetLoader.Load(workspace.DirectoryPath).CacheCoherence;

        cacheCoherence.ShouldNotBeNull();
        cacheCoherence!.CachedEntities.ShouldBe(["Account", "Person"]);
        cacheCoherence.ExcludeEnclosingNamespaceSuffix.ShouldBe(["CollectionClasses", "DaoClasses"]);
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
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}

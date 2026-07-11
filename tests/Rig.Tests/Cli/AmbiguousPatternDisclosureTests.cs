using Rig.Cli.CommandLine;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Cli;

// Ambiguity disclosure for pattern arguments: a substring pattern resolving to MULTIPLE distinct
// symbols (same method name on unrelated types — the silent wrong-tree `BuildIndex` case) must be
// disclosed on stderr; the answer already spans all of them. Distinctness is by param-free FQN, so
// overloads never count as ambiguity, and contained lambdas of a matched container are dropped the
// same way BuildTree root selection drops them.
public sealed class AmbiguousPatternDisclosureTests
{
    [Test]
    public void Same_named_methods_on_different_types_are_distinct_targets()
    {
        var nodes = new[] { "M:App.FactPathFinder.BuildIndex(App.Graph)", "M:App.IndexCommands.BuildIndex(System.String)" };

        var targets = FactPathFinder.DistinctMatchTargets(nodes, "BuildIndex");

        targets.ShouldBe(["App.FactPathFinder.BuildIndex", "App.IndexCommands.BuildIndex"]);
    }

    [Test]
    public void Overloads_collapse_to_one_target()
    {
        var nodes = new[] { "M:App.Svc.Save(System.String)", "M:App.Svc.Save(System.IO.Stream)", "M:App.Svc.Save" };

        FactPathFinder.DistinctMatchTargets(nodes, "Save").ShouldHaveSingleItem().ShouldBe("App.Svc.Save");
    }

    [Test]
    public void A_contained_lambda_of_a_matched_container_is_not_an_extra_target()
    {
        var nodes = new[] { "M:App.Svc.Run", "M:App.Svc.Run~λ0" };

        FactPathFinder.DistinctMatchTargets(nodes, "Run").ShouldHaveSingleItem().ShouldBe("App.Svc.Run");
    }

    [Test]
    public void An_exact_fqn_pattern_is_never_ambiguous_with_its_prefix_twins()
    {
        // Exact-match-wins in MatchNodes: the full FQN must not also drag in the substring superset.
        var nodes = new[] { "M:App.Svc.Proceed", "M:App.Svc.ProceedToConfirmationScreen" };

        FactPathFinder.DistinctMatchTargets(nodes, "App.Svc.Proceed").ShouldHaveSingleItem().ShouldBe("App.Svc.Proceed");
    }

    [Test]
    public void The_notice_lists_targets_and_goes_to_the_given_writer()
    {
        var error = new StringWriter();

        AmbiguityNotice.WarnIfAmbiguous(error, "BuildIndex", ["App.FactPathFinder.BuildIndex", "App.IndexCommands.BuildIndex"]);

        var notice = error.ToString();
        notice.ShouldContain("matched 2 distinct symbols");
        notice.ShouldContain("App.FactPathFinder.BuildIndex");
        notice.ShouldContain("qualify the pattern");
    }

    [Test]
    public void A_single_target_emits_no_notice()
    {
        var error = new StringWriter();

        AmbiguityNotice.WarnIfAmbiguous(error, "BuildIndex", ["App.FactPathFinder.BuildIndex"]);

        error.ToString().ShouldBeEmpty();
    }

    [Test]
    public void A_long_target_list_is_capped_with_a_more_suffix()
    {
        var error = new StringWriter();
        var targets = Enumerable.Range(0, 8).Select(i => $"App.T{i}.Handle").ToList();

        AmbiguityNotice.WarnIfAmbiguous(error, "Handle", targets);

        var notice = error.ToString();
        notice.ShouldContain("matched 8 distinct symbols");
        notice.ShouldContain("+3 more");
        notice.ShouldNotContain("App.T7.Handle"); // capped at 5 listed
    }
}

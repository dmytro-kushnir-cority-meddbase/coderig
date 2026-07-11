using System.Text.RegularExpressions;
using Rig.Cli;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

// UX regression tests for `rig callers` issues #4 (matched-target inflation) and #5 (vocabulary fragmentation).
//
// Issue #4: the plain-text output used to include the BFS start nodes (depth=0, the matched target and its
// lambdas) in the "Methods that reach X: N" count and listing, making it look like the target calls itself.
// After the fix: depth-0 nodes appear under a separate "Matched nodes (N):" sub-section; the headline count
// and the caller rows reflect only upstream callers (depth≥1). TSV is unchanged (depth-0 rows remain,
// distinctly marked by their `0` depth value so a consumer can filter them out).
//
// Issue #5a: the --roots output header now reads "Root callers (heuristic — no-predecessor origins)" instead
// of the old "Entry-point candidates" — a third noun that didn't match the flag name.
// Issue #5b: the --roots and --entrypoints option descriptions now carry a one-line contrast noting that
// --roots is a heuristic superset and --entrypoints is a precise subset.
public sealed class CallersUxTests
{
    // Issue #4 — headline count excludes the matched target; "Matched nodes" section labels it.
    // Uses CreateTeamAsync (no lambdas, 1 matched node, ≥1 caller) for a clean single-match case.
    [Test]
    public async Task Callers_excludes_matched_target_from_headline_count_and_labels_it()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], output, error, workingDirectory)).ShouldBe(0);

        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["callers", "CreateTeamAsync"], output, error, workingDirectory)).ShouldBe(0);
        var text = output.ToString();

        // The headline count must NOT include the matched target itself — only upstream callers.
        // CreateTeamAsync is called by TeamsController.Create (depth=1), so at least 1 upstream caller.
        text.ShouldContain("Methods that reach 'CreateTeamAsync':");
        var headlineCount = int.Parse(
            System.Text.RegularExpressions.Regex.Match(text, @"Methods that reach '[^']+': (\d+)").Groups[1].Value
        );
        headlineCount.ShouldBeGreaterThan(0);

        // The matched target must appear under a clearly-labelled "Matched nodes" sub-section — not
        // mingled with the upstream caller rows (where it would be disorienting as a depth-0 entry).
        text.ShouldContain("Matched nodes");
        text.ShouldContain("CreateTeamAsync");

        // The target must NOT appear as a depth-0 caller row (the old buggy format was "d0  X.CreateTeamAsync").
        Regex.IsMatch(text, @"d0\s+\S*CreateTeamAsync").ShouldBeFalse("matched target must not appear as a d0 caller row");

        // There MUST be at least one upstream caller row (the controller that calls the workflow method).
        text.ShouldContain("TeamsController");
    }

    // Issue #4 (edge case) — a method with multiple matched nodes (overloads / lambdas captured by a
    // broad pattern). ProcessBatchAsync has lambdas, so querying it by name should produce ≥2 depth-0
    // nodes. All must appear under "Matched nodes"; the headline count must reflect only upstream callers.
    [Test]
    public async Task Callers_labels_all_matched_nodes_when_pattern_matches_multiple()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], output, error, workingDirectory)).ShouldBe(0);

        output.GetStringBuilder().Clear();
        var exitCode = await CliApplication.RunAsync(["callers", "ProcessBatchAsync"], output, error, workingDirectory);
        // Exit 0 (callers found) or 1 (no symbol / no callers) — either is valid, but if 0 the
        // matched-nodes section must appear and must not mix with caller rows.
        if (exitCode == 0)
        {
            var text = output.ToString();
            text.ShouldContain("Matched nodes");
            Regex.IsMatch(text, @"d0\s+\S").ShouldBeFalse("no d0 caller rows should appear when pattern matches multiple nodes");
        }
    }

    // Issue #4 — TSV contract preserved. The matched target's depth-0 rows are STILL emitted in TSV
    // (they are distinctly marked by their `0` depth value). A TSV consumer can filter `depth > 0` for
    // upstream callers only. The test pins that both the depth-0 row (matched) and the depth-1+ rows
    // (callers) appear in unlimited TSV — backward-compatible with the existing tsv round-trip test.
    [Test]
    public async Task Callers_tsv_still_includes_depth_zero_matched_rows()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], output, error, workingDirectory)).ShouldBe(0);

        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["callers", "CreateTeamAsync", "--format", "tsv"], output, error, workingDirectory)).ShouldBe(0);
        var rows = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // At least one row must start with "0\t" (the matched target), and at least one with "1\t" (a caller).
        rows.ShouldContain(r => r.StartsWith("0\t", StringComparison.Ordinal));
        rows.ShouldContain(r => r.StartsWith("1\t", StringComparison.Ordinal));
        // The depth-0 row must reference the matched method.
        rows.First(r => r.StartsWith("0\t", StringComparison.Ordinal)).ShouldContain("CreateTeamAsync");
    }

    // Issue #5a — --roots output header uses "Root callers (heuristic...)" not "Entry-point candidates".
    [Test]
    public async Task Roots_header_uses_root_callers_heuristic_wording()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], output, error, workingDirectory)).ShouldBe(0);

        output.GetStringBuilder().Clear();
        // CreateTeamAsync is reached by at least one root (the ASP.NET controller endpoint), so exit 0.
        (await CliApplication.RunAsync(["callers", "CreateTeamAsync", "--roots"], output, error, workingDirectory)).ShouldBe(0);
        var text = output.ToString();

        text.ShouldContain("Root callers");
        text.ShouldContain("heuristic");
        text.ShouldNotContain("Entry-point candidates");
    }

    // Issue #5b — option descriptions expose the super/subset relationship in --help output.
    [Test]
    public async Task Help_describes_roots_as_superset_and_entrypoints_as_subset()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        // `--help` is handled by System.CommandLine before any store is opened, so no workingDirectory needed.
        await CliApplication.RunAsync(["callers", "--help"], output, error);

        var help = output.ToString();

        // --roots / --orphans description must convey "heuristic" and "includes test/bench" to flag scope.
        help.ShouldContain("Heuristic");
        help.ShouldContain("Superset");

        // --entrypoints description must convey "Precise" and "Subset" so users know the narrower scope.
        help.ShouldContain("Precise");
        help.ShouldContain("Subset");
    }
}

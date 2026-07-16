using System.Text.RegularExpressions;
using Rig.Cli;
using Rig.Cli.Commands;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

// Regression for the `rig callers` display/TSV entry-point cardinality mismatch: the text output headlines and
// lists only the FORWARD-CONFIRMED entry points (reverse-only EPs — in the reverse closure but with no forward
// path — are the reverse-dispatch over-approximation, hidden behind --include-reverse-only), but every --format
// tsv branch used to emit the FULL touching set (confirmed + reverse-only) unconditionally, ignoring
// --include-reverse-only. On the MedDBase store that read 213 confirmed EPs in the display vs 789 tsv rows for
// the same query. The fix routes all three tsv lenses through CallersReverseOnly.VisibleTsvRows so reverse-only
// rows are hidden by default (matching the text output) and surfaced only under --include-reverse-only.
public sealed class CallersReverseOnlyTsvTests
{
    // The pure visibility policy: reverse-only rows dropped by default, all rows kept under --include-reverse-only.
    [Test]
    public void VisibleTsvRows_hides_reverse_only_by_default()
    {
        (string Id, bool Rev)[] rows = [("a", false), ("b", true), ("c", false)];

        CallersReverseOnly
            .VisibleTsvRows(rows, isReverseOnly: r => r.Rev, includeReverseOnly: false)
            .Select(r => r.Id)
            .ShouldBe(["a", "c"]);
    }

    [Test]
    public void VisibleTsvRows_keeps_reverse_only_under_include_reverse_only()
    {
        (string Id, bool Rev)[] rows = [("a", false), ("b", true), ("c", false)];

        CallersReverseOnly
            .VisibleTsvRows(rows, isReverseOnly: r => r.Rev, includeReverseOnly: true)
            .Select(r => r.Id)
            .ShouldBe(["a", "b", "c"]);
    }

    // End-to-end contract: the display headline count and the default --format tsv row count must AGREE on the
    // set of entry points (this was the reported divergence). --include-reverse-only may only ADD rows, never
    // remove them — it is the one lens that surfaces the reverse-only over-approximation, and it must do so in
    // tsv now that the flag is honoured across formats (previously a no-op for tsv).
    [Test]
    public async Task Entrypoints_display_headline_and_default_tsv_row_count_agree()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], output, error, workingDirectory)).ShouldBe(0);

        // Display: the headline count is the forward-confirmed answer.
        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["callers", "CreateTeamAsync", "--entrypoints"], output, error, workingDirectory)).ShouldBe(0);
        var display = output.ToString();
        var headline = HeadlineCount(display);
        headline.ShouldBeGreaterThan(0, display);

        // Default tsv: one row per confirmed EP — must equal the display headline (the bug: it emitted more).
        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(["callers", "CreateTeamAsync", "--entrypoints", "--format", "tsv"], output, error, workingDirectory)
        ).ShouldBe(0);
        var defaultRows = TsvRowCount(output.ToString());
        defaultRows.ShouldBe(headline, $"default tsv rows must equal the display headline.\nDISPLAY:\n{display}");

        // --include-reverse-only tsv: never fewer than the default (it only adds the reverse-only remainder).
        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["callers", "CreateTeamAsync", "--entrypoints", "--format", "tsv", "--include-reverse-only"],
                output,
                error,
                workingDirectory
            )
        ).ShouldBe(0);
        TsvRowCount(output.ToString()).ShouldBeGreaterThanOrEqualTo(defaultRows);
    }

    private static int HeadlineCount(string text)
    {
        var m = Regex.Match(text, @"entry points reaching '[^']+': (\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    private static int TsvRowCount(string tsv) => tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
}

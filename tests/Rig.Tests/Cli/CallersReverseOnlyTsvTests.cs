using System.Text.RegularExpressions;
using Rig.Cli;
using Rig.Cli.Commands;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

public sealed class CallersReverseOnlyTsvTests
{
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

    [Test]
    public async Task Entrypoints_display_headline_and_default_tsv_row_count_agree()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], output, error, workingDirectory)).ShouldBe(0);

        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["callers", "CreateTeamAsync", "--entrypoints"], output, error, workingDirectory)).ShouldBe(0);
        var display = output.ToString();
        var headline = HeadlineCount(display);
        headline.ShouldBeGreaterThan(0, display);

        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(["callers", "CreateTeamAsync", "--entrypoints", "--format", "tsv"], output, error, workingDirectory)
        ).ShouldBe(0);
        var defaultRows = TsvRowCount(output.ToString());
        defaultRows.ShouldBe(headline, $"default tsv rows must equal the display headline.\nDISPLAY:\n{display}");

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

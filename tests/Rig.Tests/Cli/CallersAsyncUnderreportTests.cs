using Rig.Cli;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

// BUG-rig-missed-entrypoints-healthcode (Defect 2), non-zero half: `rig callers <m> --entrypoints` defaults to
// SYNC (handoffs cut), so a target reached by an entry point ONLY across a scheduled/async handoff is omitted
// from the sync answer. The 0-EP case already warns ("0 sync — but N via async; re-run with --async"); this
// pins the NON-ZERO case — sync finds some EPs but the async surface finds strictly more — which must surface a
// "+K more … via async" footer so a reviewer doesn't wrongly de-risk a change off the sync count.
//
// Fixture: AuditSink.WriteAuditEntry is reached by TWO controller actions — RecordDirect calls it directly
// (synchronous) and Subscribe reaches it only across an event-subscription handoff (`+= OnSaved`, sync-cut).
// The sync EP answer must be a strict subset of the async one, and the footer must name the gap.
public sealed class CallersAsyncUnderreportTests
{
    [Test]
    public async Task Entrypoints_sync_warns_when_the_async_surface_reaches_more()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], output, error, workingDirectory)).ShouldBe(0);

        // SYNC --entrypoints
        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["callers", "WriteAuditEntry", "--entrypoints"], output, error, workingDirectory)).ShouldBe(0);
        var syncText = output.ToString();

        // --async --entrypoints (the precise superset)
        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(["callers", "WriteAuditEntry", "--entrypoints", "--async"], output, error, workingDirectory)
        ).ShouldBe(0);
        var asyncText = output.ToString();

        var syncCount = HeadlineCount(syncText);
        var asyncCount = HeadlineCount(asyncText);

        // The async surface reaches strictly more EPs (the method-group/Task.Run handoff is sync-cut).
        asyncCount.ShouldBeGreaterThan(syncCount, $"async should reach more EPs than sync.\nSYNC:\n{syncText}\nASYNC:\n{asyncText}");

        // The sync run must NOT silently under-report — it points the reviewer at --async with the gap size.
        syncText.ShouldContain("more entry point(s) reach this via async/scheduled handoff", customMessage: syncText);
        // The async run, which already shows them, must NOT print the footer.
        asyncText.ShouldNotContain("reach this via async/scheduled handoff");
    }

    private static int HeadlineCount(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"entry points reaching '[^']+': (\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }
}

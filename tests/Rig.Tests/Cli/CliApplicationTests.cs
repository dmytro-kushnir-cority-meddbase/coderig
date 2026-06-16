using Rig.Cli;
using Rig.Tests.Fixtures;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Cli;

public sealed class CliApplicationTests
{
    // No command -> System.CommandLine owns it: "Required command was not provided." goes to stderr, the
    // root help (description + the subcommand list) to stdout, exit 1. We assert the help lists the
    // subcommands, not the exact framework chrome.
    [Test]
    public async Task No_arguments_prints_framework_help()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync([], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("Required command was not provided.");
        var help = output.ToString();
        help.ShouldContain("Runtime Intelligence Graph");
        help.ShouldContain("Commands:");
        help.ShouldContain("tree");
        help.ShouldContain("callers");
        help.ShouldContain("reaches");
        help.ShouldContain("derive");
        // `dead` is intentionally NOT registered (disabled until moved onto the one-hop engine — see Root.cs).
        help.ShouldContain("profile");
    }

    // An unrecognized command is a framework parse error (exit 1); it names the bad token and suggests the
    // nearest match on stderr.
    [Test]
    public async Task Unknown_command_fails_with_actionable_error()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["wat"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("Unrecognized command or argument 'wat'");
    }

    // --merge / --include-tests are real Options on `index`, so they parse cleanly; the command then fails
    // later for a different reason (a nonexistent solution: exit 2, "Failed to load") rather than being
    // rejected up front. Guards against a known flag being dropped from the index surface.
    [Test]
    [Arguments("--merge")]
    [Arguments("--include-tests")]
    public async Task Index_does_not_reject_known_flags(string flag)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["index", "C:/does-not-exist.slnx", flag], output, error);

        exitCode.ShouldBe(2);
        error.ToString().ShouldNotContain("Unrecognized command or argument");
        error.ToString().ShouldContain("Failed to load");
    }

    // --durable and --no-tests were removed from the index surface (the former dropped entirely, the
    // latter a redundant no-op alias of the now-default test exclusion). With System.CommandLine they are
    // no longer declared Options, so they are REJECTED up front as parse errors (exit 1) — not silently
    // accepted — and a stale script using them fails loudly.
    [Test]
    [Arguments("--durable")]
    [Arguments("--no-tests")]
    public async Task Index_rejects_removed_flags(string flag)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["index", "C:/does-not-exist.slnx", flag], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain($"Unrecognized command or argument '{flag}'");
    }

    // Mutually-exclusive projection modes are rejected up front (validation runs before any store access,
    // so these fail cleanly without a store).
    [Test]
    public async Task Tree_rejects_conflicting_projection_modes()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--full", "--summary"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("can't be combined");
        error.ToString().ShouldContain("--full");
        error.ToString().ShouldContain("--summary");
    }

    [Test]
    public async Task Callers_rejects_orphans_with_entrypoints()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["callers", "X", "--orphans", "--entrypoints"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("--orphans and --entrypoints can't be combined");
    }

    // A query command run where there is no .rig store (e.g. wrong directory) fails cleanly with exit 2
    // and an actionable message, not an unhandled SqliteException stack trace.
    [Test]
    public async Task Query_command_without_a_store_fails_cleanly()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "rig-no-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = await CliApplication.RunAsync(["tree", "Whatever"], output, error, emptyDir);

            exitCode.ShouldBe(2);
            error.ToString().ShouldContain("No indexed store");
            error.ToString().ShouldNotContain("SqliteException");
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Test]
    public async Task Files_requires_skipped_flag()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["files"], output, error);

        // The required-flag validator fails up front (exit 1); its message goes to stderr, framework help to
        // stdout.
        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("Usage: rig files --skipped");
    }

    [Test]
    public async Task Index_then_fact_commands_roundtrip_over_the_playground()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var solutionPath = playground.SolutionPath;
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["index", solutionPath], output, error, workingDirectory)).ShouldBe(0);
        error.ToString().ShouldBeEmpty();
        output.ToString().ShouldContain("Run:");
        output.ToString().ShouldContain("Symbols:");
        output.ToString().ShouldContain("References:");
        output.ToString().ShouldContain("DiRegistrations:");
        File.Exists(Path.Combine(workingDirectory, ".rig", "rig.db")).ShouldBeTrue();

        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["runs"], output, error, workingDirectory)).ShouldBe(0);
        output.ToString().ShouldContain("Runs");
        output.ToString().ShouldContain("symbols=");
        output.ToString().ShouldContain("references=");
        output.ToString().ShouldContain("di=");

        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["derive"], output, error, workingDirectory)).ShouldBe(0);
        output.ToString().ShouldContain("Effects re-derived from facts:");
        output.ToString().ShouldContain("Entry points re-derived from facts:");

        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["di"], output, error, workingDirectory)).ShouldBe(0);
        output.ToString().ShouldContain("DI Registrations");
        output.ToString().ShouldContain("ITeamRepository");

        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["files", "--skipped"], output, error, workingDirectory)).ShouldBe(0);
        output.ToString().ShouldContain("GeneratedEndpoint.g.cs");

        // `dead` is intentionally disabled (unregistered until moved onto the one-hop engine — see Root.cs),
        // so it is no longer part of this round-trip. The DeadCodeFinder logic is covered by DeadCodeFinderTests.

        var rulesPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "rig.rules.json");
        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["reaches", "PaymentGatewayCaller.Dispatch", "--rules", rulesPath],
                output,
                error,
                workingDirectory
            )
        ).ShouldBe(0);
        var reaches = output.ToString();
        reaches.ShouldContain("gateway_ask ask");
        reaches.ShouldContain("Team");
        reaches.ShouldContain("gateway_tell tell");
        reaches.ShouldContain("PaymentGatewayProcessDns.PaymentService");
    }

    // --format tsv (Tier-1 #10): tree/path/callers emit tab-separated, full-DocID rows with no text chrome,
    // so an agent/CI gate can consume them without scraping. Exercised over the same playground graph.
    [Test]
    public async Task Format_tsv_emits_tab_separated_rows_for_tree_path_callers()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], output, error, workingDirectory)).ShouldBe(0);

        // tree: one DFS row per node — `depth \t docId \t edgeKind \t …`; the matched root sits at depth 0.
        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(["tree", "PaymentGatewayCaller.Dispatch", "--format", "tsv"], output, error, workingDirectory)
        ).ShouldBe(0);
        var tree = output.ToString();
        tree.ShouldContain("\t");
        tree.ShouldContain("PaymentGatewayCaller.Dispatch");
        tree.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].ShouldStartWith("0\t");

        // path: one row per step; the text-only "Fact graph:" banner is suppressed under tsv.
        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["path", "PaymentGatewayCaller.Dispatch", "PaymentGatewayProcess.Tell", "--format", "tsv"],
                output,
                error,
                workingDirectory
            )
        ).ShouldBe(0);
        var path = output.ToString();
        path.ShouldContain("\t");
        path.ShouldContain("PaymentGatewayProcess.Tell");
        path.ShouldNotContain("Fact graph:");

        // callers: `depth \t docId` per reaching method; the dispatcher reaches Tell. Text header suppressed.
        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(["callers", "PaymentGatewayProcess.Tell", "--format", "tsv"], output, error, workingDirectory)
        ).ShouldBe(0);
        var callers = output.ToString();
        callers.ShouldContain("\t");
        callers.ShouldContain("PaymentGatewayCaller.Dispatch");
        callers.ShouldNotContain("Methods that reach");

        // --limit caps the listing (default unbounded). Tell is reached by >1 method (itself + the
        // dispatcher), so --limit 1 yields exactly one tsv row, and the text mode prints a "raise --limit"
        // truncation note rather than silently dropping the rest.
        var unlimited = callers.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        unlimited.ShouldBeGreaterThan(1);
        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["callers", "PaymentGatewayProcess.Tell", "--format", "tsv", "--limit", "1"],
                output,
                error,
                workingDirectory
            )
        ).ShouldBe(0);
        output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1);

        output.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(["callers", "PaymentGatewayProcess.Tell", "--limit", "1"], output, error, workingDirectory)
        ).ShouldBe(0);
        output.ToString().ShouldContain("raise --limit");
    }
}

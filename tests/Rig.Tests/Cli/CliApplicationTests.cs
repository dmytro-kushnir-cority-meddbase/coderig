using Rig.Cli;
using Rig.Tests.Fixtures;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Cli;

public sealed class CliApplicationTests
{
    [Test]
    public async Task No_arguments_prints_human_readable_command_summary()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync([], output, error);

        exitCode.ShouldBe(0);
        error.ToString().ShouldBeEmpty();
        output.ToString().ShouldContain("Runtime Intelligence Graph");
        output.ToString().ShouldContain("Usage:");
        output.ToString().ShouldContain("rig index <solution|project>");
        output.ToString().ShouldContain("rig runs");
        output.ToString().ShouldContain("rig derive");
        output.ToString().ShouldContain("rig reaches");
        output.ToString().ShouldContain("rig tree");
        output.ToString().ShouldContain("rig callers");
        output.ToString().ShouldContain("rig dead");
        output.ToString().ShouldContain("rig di");
        output.ToString().ShouldContain("rig files --skipped");
        output.ToString().ShouldContain("rig profile validate");
    }

    [Test]
    public async Task Unknown_command_fails_with_actionable_error()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["wat"], output, error);

        exitCode.ShouldBe(2);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("Unknown command: wat");
    }

    // Guards the option-whitelist regression: --merge was missing from KnownFlagsByCommand["index"],
    // so `rig index ... --merge` was rejected as an unknown option before it could run. Validation runs
    // before solution load, so a KNOWN flag must NOT trip "Unknown option" (the command fails later for
    // a different reason — a nonexistent solution).
    [Test]
    [Arguments("--merge")]
    [Arguments("--include-tests")]
    public async Task Index_does_not_reject_known_flags(string flag)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["index", "C:/does-not-exist.slnx", flag], output, error);

        // Known flag -> passes validation, then fails cleanly on the nonexistent solution (exit 2,
        // "Failed to load") rather than being rejected up front as an unknown option.
        exitCode.ShouldBe(2);
        error.ToString().ShouldNotContain("Unknown option");
        error.ToString().ShouldContain("Failed to load");
    }

    // --durable and --no-tests were removed from the index surface (the former dropped entirely, the
    // latter a redundant no-op alias of the now-default test exclusion). They must now be REJECTED up
    // front as unknown options — not silently accepted — so a stale script using them fails loudly.
    [Test]
    [Arguments("--durable")]
    [Arguments("--no-tests")]
    public async Task Index_rejects_removed_flags(string flag)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["index", "C:/does-not-exist.slnx", flag], output, error);

        exitCode.ShouldBe(2);
        error.ToString().ShouldContain("Unknown option");
    }

    // Mutually-exclusive projection modes are rejected up front (validation runs before any store access,
    // so these fail cleanly without a store).
    [Test]
    public async Task Tree_rejects_conflicting_projection_modes()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--full", "--summary"], output, error);

        exitCode.ShouldBe(2);
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

        exitCode.ShouldBe(2);
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

        exitCode.ShouldBe(2);
        output.ToString().ShouldBeEmpty();
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

        output.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["dead"], output, error, workingDirectory)).ShouldBe(0);
        output.ToString().ShouldContain("Dead-code candidates:");

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
}

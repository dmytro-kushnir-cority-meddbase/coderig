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

using FluentAssertions;
using Rig.Cli;

namespace Rig.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task No_arguments_prints_human_readable_command_summary()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync([], output, error);

        exitCode.Should().Be(0);
        error.ToString().Should().BeEmpty();
        output.ToString().Should().ContainAll(
            "Runtime Intelligence Graph",
            "Usage:",
            "rig index <solution>",
            "rig runs",
            "rig entrypoints",
            "rig callgraph <entrypoint-id>",
            "rig effects --entrypoint <entrypoint-id>",
            "rig files --skipped",
            "rig profile validate");
    }

    [Fact]
    public async Task Unknown_command_fails_with_actionable_error()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["wat"], output, error);

        exitCode.Should().Be(2);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("Unknown command: wat");
    }

    [Fact]
    public async Task Index_then_entrypoints_and_effects_print_latest_playground_analysis()
    {
        var workingDirectory = Directory.CreateTempSubdirectory("rig-tests-").FullName;
        var solutionPath = PlaygroundSolutionPath();
        var output = new StringWriter();
        var error = new StringWriter();

        var indexExitCode = await CliApplication.RunAsync(["index", solutionPath], output, error, workingDirectory);

        indexExitCode.Should().Be(0);
        error.ToString().Should().BeEmpty();
        output.ToString().Should().ContainAll("Run:", "EntryPoints: 5", "Effects: 19");
        File.Exists(Path.Combine(workingDirectory, ".rig", "rig.db")).Should().BeTrue();

        output.GetStringBuilder().Clear();
        var runsExitCode = await CliApplication.RunAsync(["runs"], output, error, workingDirectory);

        runsExitCode.Should().Be(0);
        output.ToString().Should().ContainAll(
            "Runs",
            solutionPath,
            "entrypoints=5 effects=19",
            "di=");

        output.GetStringBuilder().Clear();
        var entrypointsExitCode = await CliApplication.RunAsync(["entrypoints"], output, error, workingDirectory);

        entrypointsExitCode.Should().Be(0);
        output.ToString().Should().ContainAll(
            "minapi GET /minapi/teams/{id}",
            "mvc POST api/teams",
            "fastendpoint POST /fastendpoints/teams");

        output.GetStringBuilder().Clear();
        var effectsExitCode = await CliApplication.RunAsync(["effects"], output, error, workingDirectory);

        effectsExitCode.Should().Be(0);
        output.ToString().Should().ContainAll(
            "http GET billing.example/invoices/{teamId}",
            "efcore read AppDbContext.Teams",
            "efcore commit AppDbContext",
            "efcore schema AppDbContext.Database",
            "efcore raw_sql AppDbContext.Database",
            "redis read team:{teamId}",
            "redis write team:{name}",
            "smtp send MailKit.Net.Smtp.SmtpClient",
            "mediatr send EntryPointEffects.Api.Services.FixtureCommand",
            "repository write Ardalis.SharedKernel.IRepository<T>",
            "OBS looped_effect ctx=foreach",
            "OBS parallel_fanout ctx=Task.WhenAll");

        output.GetStringBuilder().Clear();
        var filesExitCode = await CliApplication.RunAsync(["files", "--skipped"], output, error, workingDirectory);

        filesExitCode.Should().Be(0);
        output.ToString().Should().ContainAll(
            "Skipped Files",
            "GeneratedEndpoint.g.cs",
            "basis=profile",
            "reason=generated_fixture");

        output.GetStringBuilder().Clear();
        var callgraphExitCode = await CliApplication.RunAsync(["callgraph", "minapi GET /minapi/teams/{id}"], output, error, workingDirectory);

        callgraphExitCode.Should().Be(0);
        output.ToString().Should().ContainAll(
            "Callgraph: minapi GET /minapi/teams/{id}",
            "TeamWorkflow.LoadTeamSummaryAsync",
            "BillingClient.LoadInvoiceAsync",
            "BillingClient.LoadInvoicesAsync",
            "BOUNDARY external HttpClient.GetStringAsync",
            "EFFECT efcore read AppDbContext.Teams",
            "OBS looped_effect ctx=foreach",
            "OBS parallel_fanout ctx=Task.WhenAll");
    }

    private static string PlaygroundSolutionPath()
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "playgrounds",
            "EntryPointEffects",
            "EntryPointEffects.slnx"));
    }
}

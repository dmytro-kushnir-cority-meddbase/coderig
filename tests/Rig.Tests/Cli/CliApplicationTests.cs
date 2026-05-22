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
        output.ToString().Should().ContainAll("EntryPoints: 4", "Effects: 8");

        output.GetStringBuilder().Clear();
        var entrypointsExitCode = await CliApplication.RunAsync(["entrypoints"], output, error, workingDirectory);

        entrypointsExitCode.Should().Be(0);
        output.ToString().Should().ContainAll(
            "minapi GET /minapi/teams/{id}",
            "mvc POST api/teams");

        output.GetStringBuilder().Clear();
        var effectsExitCode = await CliApplication.RunAsync(["effects"], output, error, workingDirectory);

        effectsExitCode.Should().Be(0);
        output.ToString().Should().ContainAll(
            "http GET billing.example/invoices/{teamId}",
            "efcore read AppDbContext.Teams",
            "efcore commit AppDbContext",
            "redis read team:{teamId}",
            "redis write team:{name}",
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

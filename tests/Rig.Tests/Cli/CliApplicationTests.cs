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
}

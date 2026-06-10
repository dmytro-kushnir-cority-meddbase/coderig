using Rig.Cli;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

[Collection(RoslynIntegrationCollection.Name)]
public sealed class CliApplicationTests
{
    [Fact]
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

    [Fact]
    public async Task Unknown_command_fails_with_actionable_error()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["wat"], output, error);

        exitCode.ShouldBe(2);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("Unknown command: wat");
    }

    [Fact]
    public async Task Files_requires_skipped_flag()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["files"], output, error);

        exitCode.ShouldBe(2);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("Usage: rig files --skipped");
    }

    // NOTE: the index -> query CLI roundtrip integration test is re-added fact-centric
    // (index -> derive/reaches/dead/di) at the end of the legacy-removal work, once the
    // index output + fact command surface are stable.
}

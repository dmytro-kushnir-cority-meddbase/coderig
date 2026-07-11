using Rig.Cli;
using Shouldly;

namespace Rig.Tests.Cli;

// Smoke tests: each of the five query commands that gained `--time` in this change must expose the flag
// in their `--help` output. No live store required — System.CommandLine answers `--help` before any store
// is opened.
public sealed class TimeFlagTests
{
    [Test]
    public async Task Callers_help_contains_time_flag()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        await CliApplication.RunAsync(["callers", "--help"], output, error);

        output.ToString().ShouldContain("--time");
    }

    [Test]
    public async Task Reaches_help_contains_time_flag()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        await CliApplication.RunAsync(["reaches", "--help"], output, error);

        output.ToString().ShouldContain("--time");
    }

    [Test]
    public async Task Path_help_contains_time_flag()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        await CliApplication.RunAsync(["path", "--help"], output, error);

        output.ToString().ShouldContain("--time");
    }

    [Test]
    public async Task DispatchFans_help_contains_time_flag()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        await CliApplication.RunAsync(["dispatch-fans", "--help"], output, error);

        output.ToString().ShouldContain("--time");
    }

    [Test]
    public async Task EffectsDiff_help_contains_time_flag()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        await CliApplication.RunAsync(["effects-diff", "--help"], output, error);

        output.ToString().ShouldContain("--time");
    }
}

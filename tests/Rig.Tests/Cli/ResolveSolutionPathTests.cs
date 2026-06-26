using Rig.Cli.Commands;
using Shouldly;

namespace Rig.Tests.Cli;

// Unit tests for IndexCommands.ResolveSolutionPath — the relative→absolute normalisation that fixes the
// "relative .slnx crashes deep in Roslyn" bug. Pure path math: no real workspace, no file needs to exist
// (existence is the loader's job, NOT this helper's — see Missing_path_is_not_guarded_here below).
public sealed class ResolveSolutionPathTests
{
    [Test]
    public void Relative_path_is_resolved_against_workingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rig-test-{Guid.NewGuid():N}");

        var result = IndexCommands.ResolveSolutionPath(target: "My.slnx", workingDirectory: dir);

        result.ShouldBe(Path.Combine(dir, "My.slnx"));
        Path.IsPathRooted(result).ShouldBeTrue();
    }

    [Test]
    public void Absolute_path_is_returned_unchanged()
    {
        var absolutePath = Path.Combine(Path.GetTempPath(), "Solution.slnx");

        IndexCommands.ResolveSolutionPath(target: absolutePath, workingDirectory: "C:\\irrelevant").ShouldBe(absolutePath);
    }

    // The bug: a relative path was resolved against Environment.CurrentDirectory (the process cwd), not
    // workingDirectory — wrong base when `rig` runs from another dir. Prove we anchor at workingDirectory.
    [Test]
    public void Relative_path_resolved_against_workingDirectory_not_process_cwd()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rig-test-{Guid.NewGuid():N}");
        dir.ShouldNotBe(Environment.CurrentDirectory, "test setup: temp dir must differ from process cwd");

        IndexCommands.ResolveSolutionPath(target: "Anchored.slnx", workingDirectory: dir).ShouldBe(Path.Combine(dir, "Anchored.slnx"));
    }

    // A missing path is NOT guarded here — it normalises and flows to the loader, which fails cleanly
    // ("Failed to load" + non-zero exit). The helper must NOT throw (an early throw would bypass that
    // handler, which sits inside the try; see Index_does_not_reject_known_flags).
    [Test]
    public void Missing_path_is_not_guarded_here_it_normalises_without_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rig-test-{Guid.NewGuid():N}"); // never created

        var result = IndexCommands.ResolveSolutionPath(target: "Missing.slnx", workingDirectory: dir);

        result.ShouldBe(Path.Combine(dir, "Missing.slnx"));
    }
}

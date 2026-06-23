using Rig.Cli;
using Shouldly;

namespace Rig.Tests.Cli;

// UX research panel item #12: first-run discoverability. Asserts the two fixes:
// (1) the top-level --help description explains the cwd dependency and shows a quick-start, and
// (2) the "no .rig store" error message names the actual working directory and gives a clear fix,
//     rather than exposing the internal db path deep inside .rig/.
public sealed class FirstRunUxTests
{
    // The root --help output must mention that query commands read the store from the current
    // directory, and must include the quick-start snippet so first-time users know the workflow.
    [Test]
    public async Task Root_help_explains_cwd_dependency_and_quick_start()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        // No command → System.CommandLine renders root help to stdout (exit 1).
        var exitCode = await CliApplication.RunAsync([], output, error);

        exitCode.ShouldBe(1);
        var help = output.ToString();
        // The cwd / .rig/ dependency note.
        help.ShouldContain(".rig");
        help.ShouldContain("current directory");
        // Quick-start commands are present.
        help.ShouldContain("rig index");
        help.ShouldContain("rig runs");
        help.ShouldContain("rig entrypoints");
        help.ShouldContain("rig tree");
    }

    // When a query command is run in a directory with no .rig store the error must name the actual
    // working directory (not the internal db path) and tell the user exactly how to fix it.
    [Test]
    public async Task No_store_error_names_the_cwd_and_gives_actionable_fix()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "rig-first-run-ux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = await CliApplication.RunAsync(["tree", "Whatever"], output, error, emptyDir);

            exitCode.ShouldBe(2);
            var msg = error.ToString();

            // Must name the actual directory the user ran from, not a deep internal path.
            msg.ShouldContain(emptyDir);

            // Must tell the user what command creates the store.
            msg.ShouldContain("rig index");

            // Must tell the user they may need to cd to where .rig/ lives.
            msg.ShouldContain(".rig/");

            // Must NOT expose the raw internal rig.db path (the old confusing wording).
            msg.ShouldNotContain("rig.db");

            // Must NOT leak a raw exception trace.
            msg.ShouldNotContain("SqliteException");
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }
}

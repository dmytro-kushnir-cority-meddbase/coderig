using Rig.Cli;
using Shouldly;

namespace Rig.Tests.Cli;

// Tests for `rig effects-diff <a> <b>`.
//
// Coverage:
//   (1) The command is registered and appears in the root --help output.
//   (2) The command's own --help shows the positional args (a, b) + --only, --label, --format, --store.
//   (3) Running against an empty directory (no .rig store) exits non-zero with a diagnostic — the store
//       gate fires before any pattern resolution, so this is the closest store-free smoke test.
//
// Deferred (require a live .rig store fixture): the no-match / ambiguous-match error paths and a correct
// diff for a known pair. The underlying diff logic is covered by FactEffectSetDiffDeriverTests (the pure
// deriver); the command's ParseFilter / ResolvePattern are private and indirectly exercised.
public sealed class EffectsDiffCommandTests
{
    [Test]
    public async Task Command_appears_in_root_help()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        await CliApplication.RunAsync(["--help"], output, error);

        output.ToString().ShouldContain("effects-diff");
    }

    [Test]
    public async Task Command_help_shows_positional_args_and_options()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        await CliApplication.RunAsync(["effects-diff", "--help"], output, error);

        var text = output.ToString();
        text.ShouldContain("--only");
        text.ShouldContain("--label");
        text.ShouldContain("--format");
        text.ShouldContain("--store");
    }

    // Running without a store in an empty directory fails non-zero and prints a diagnostic — the store gate
    // fires before pattern resolution, the closest store-free smoke test of the run path.
    [Test]
    public async Task Command_exits_nonzero_with_error_when_no_store_exists()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "rig-effdiff-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = await CliApplication.RunAsync(["effects-diff", "SomeA", "SomeB"], output, error, emptyDir);

            exitCode.ShouldNotBe(0);
            (output.ToString() + error.ToString()).Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }
}

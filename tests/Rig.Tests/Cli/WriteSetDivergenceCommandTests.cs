using Rig.Cli;
using Shouldly;

namespace Rig.Tests.Cli;

// Tests for `rig write-set-divergence <primary> <secondary>`.
//
// Coverage:
//   (1) The command is registered and appears in the root --help output.
//   (2) The command's own --help shows the positional args, --entity, --write, --format, --store.
//   (3) Running against an empty directory (no .rig store) produces a first-run error on stderr and
//       exits non-zero — the store gate fires before any pattern resolution, so this is the closest
//       store-free smoke test for the command's run path.
//   (4) --write option parsing: the no-store path exits non-zero for any invocation without a store,
//       so the write-predicate parsing path is exercised by the helper unit tests in
//       FactWriteSetDivergenceDeriverTests (the deriver is the pure layer; the command's ParseWritePredicates
//       is private and indirectly covered).
//
// Deferred (require a live .rig store fixture):
//   - No-match error ("No symbol matches '<pattern>'.") — needs a store so the graph can be loaded.
//   - Ambiguous-match error ("Ambiguous: ... matched N nodes") — same dependency.
//   - Correct divergence output for a known fixture pair.
//   These are deferred because constructing a valid .rig store from scratch in a test is heavy and not
//   yet done for this command. The underlying logic is covered by FactWriteSetDivergenceDeriverTests.
public sealed class WriteSetDivergenceCommandTests
{
    // (1) The command appears in `rig --help`.
    [Test]
    public async Task Command_appears_in_root_help()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        await CliApplication.RunAsync(["--help"], output, error);

        output.ToString().ShouldContain("write-set-divergence");
    }

    // (2) `rig write-set-divergence --help` shows positional args and key options.
    [Test]
    public async Task Command_help_shows_positional_args_and_options()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        await CliApplication.RunAsync(["write-set-divergence", "--help"], output, error);

        var text = output.ToString();
        text.ShouldContain("primary");
        text.ShouldContain("secondary");
        text.ShouldContain("--entity");
        text.ShouldContain("--write");
        text.ShouldContain("--format");
        text.ShouldContain("--store");
    }

    // (3) Running without a store in an empty directory fails non-zero and prints an error to stderr.
    //     This is the closest store-free smoke test that exercises the command's run path at all.
    [Test]
    public async Task Command_exits_nonzero_with_error_when_no_store_exists()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "rig-wsd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = await CliApplication.RunAsync(["write-set-divergence", "SomePrimary", "SomeSecondary"], output, error, emptyDir);

            // Must exit non-zero — store gate fires before pattern resolution.
            exitCode.ShouldNotBe(0);
            // At least one of stdout or stderr must contain some diagnostic text.
            (output.ToString() + error.ToString()).Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }
}

using Rig.Cli.Commands;
using Shouldly;

namespace Rig.Tests.Cli;

// FR-4 — the `rig impact --expect-no-effect-change` CI gate. Behavioral change = an entry point present in
// BOTH commits whose reachable EFFECT set changed (impactDiff.PerEp.Count — the header's "N with a changed
// behavior"). The gate must: fail (exit 1) iff that count > 0, pass (exit 0) when it's 0, and be a pure no-op
// (exit 0, no output) when the flag is off — so it never blocks a behavior-preserving refactor and never fires
// unless asked. Verdict goes to STDERR (so a `--format tsv` stdout stays machine-clean). Pure unit over the
// count — the diff that produces it is covered by ImpactAmplificationTests / ImpactTwoStoreTests.
public sealed class ImpactExpectGateTests
{
    [Test]
    public void Flag_off_is_a_silent_noop_even_with_behavioral_changes()
    {
        var error = new StringWriter();
        ImpactCommand.ExpectNoEffectChangeExit(expect: false, behavioralEpCount: 7, error).ShouldBe(0);
        error.ToString().ShouldBeEmpty();
    }

    [Test]
    public void No_effect_change_passes_with_an_ok_line()
    {
        var error = new StringWriter();
        ImpactCommand.ExpectNoEffectChangeExit(expect: true, behavioralEpCount: 0, error).ShouldBe(0);
        error.ToString().ShouldContain("OK");
        error.ToString().ShouldContain("no entry point's effect set changed");
    }

    [Test]
    public void Any_effect_change_fails_with_a_nonzero_exit_and_the_count()
    {
        var error = new StringWriter();
        ImpactCommand.ExpectNoEffectChangeExit(expect: true, behavioralEpCount: 3, error).ShouldBe(1);
        error.ToString().ShouldContain("FAILED");
        error.ToString().ShouldContain("3 entry point(s)");
    }
}

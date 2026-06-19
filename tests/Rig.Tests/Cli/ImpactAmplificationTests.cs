using Rig.Cli.Commands;
using Shouldly;

namespace Rig.Tests.Cli;

// Feature 1 — effect AMPLIFICATION (cardinality + loop-context) in `impact`. The set-diff (Added/Removed)
// is blind to an effect whose SET membership is UNCHANGED but is now produced MORE (higher reach
// multiplicity) or has MOVED INTO A LOOP. Over the INTERSECTION of an EP's branch/base footprint keys,
// DiffFootprints emits a THIRD category — amplified — flagged for REVIEW (the tool can't tell a harmless
// hot-cache re-read from a real extra cold call). These pin the pure diff over footprint maps that carry
// per-key (Count, InLoop), without touching a store.
public sealed class ImpactAmplificationTests
{
    private static ImpactCommand.EntryPointRef Ep(string kind, string route) => new(kind, route, $"/{route}.cs", 1, null);

    // (provider, operation, resource, enclosing) — the effect key shape used throughout impact.
    private static (string, string, string, string) Key(string enclosing = "N.T.M") => ("llblgen", "write", "Account", enclosing);

    private static Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), ImpactCommand.EffectReach>> Map(
        string kind,
        string route,
        params ((string, string, string, string) Key, int Count, bool InLoop)[] entries
    )
    {
        var inner = new Dictionary<(string, string, string, string), ImpactCommand.EffectReach>();
        foreach (var e in entries)
        {
            inner[e.Key] = new ImpactCommand.EffectReach(Count: e.Count, InLoop: e.InLoop);
        }

        return new Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), ImpactCommand.EffectReach>>
        {
            [(kind, route)] = inner,
        };
    }

    private static IReadOnlyDictionary<(string Kind, string Route), ImpactCommand.EntryPointRef> EpByKey(string kind, string route) =>
        new Dictionary<(string Kind, string Route), ImpactCommand.EntryPointRef> { [(kind, route)] = Ep(kind, route) };

    [Test]
    public void Count_increase_on_a_shared_key_is_flagged_amplified()
    {
        var branch = Map("http", "x", (Key(), 4, false));
        var @base = Map("http", "x", (Key(), 1, false));

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        deltas.Count.ShouldBe(1);
        var amp = deltas[0].Amplified;
        amp.Count.ShouldBe(1);
        amp[0].BaseCount.ShouldBe(1);
        amp[0].BranchCount.ShouldBe(4);
        amp[0].BranchInLoop.ShouldBeFalse();
        // Set membership unchanged → no add/remove.
        deltas[0].Added.ShouldBeEmpty();
        deltas[0].Removed.ShouldBeEmpty();
    }

    [Test]
    public void Loop_entry_on_a_shared_key_is_flagged_amplified_even_when_count_is_unchanged()
    {
        var branch = Map("http", "x", (Key(), 1, true));
        var @base = Map("http", "x", (Key(), 1, false));

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        deltas.Count.ShouldBe(1);
        deltas[0].Amplified.Count.ShouldBe(1);
        deltas[0].Amplified[0].BaseInLoop.ShouldBeFalse();
        deltas[0].Amplified[0].BranchInLoop.ShouldBeTrue();
    }

    [Test]
    public void Unchanged_key_produces_no_amplification_and_no_delta()
    {
        var branch = Map("http", "x", (Key(), 2, true));
        var @base = Map("http", "x", (Key(), 2, true));

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        deltas.ShouldBeEmpty();
    }

    [Test]
    public void A_genuinely_added_key_stays_in_added_not_amplified()
    {
        // The added key exists only on the branch → it's an Added effect, NOT an amplification (which is
        // defined only over the intersection of keys present on BOTH sides).
        var addedKey = ("redis", "read", "Cache", "N.T.M");
        var branch = Map("http", "x", (Key(), 1, false), (addedKey, 3, true));
        var @base = Map("http", "x", (Key(), 1, false));

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        deltas.Count.ShouldBe(1);
        deltas[0].Added.ShouldContain(addedKey);
        deltas[0].Amplified.ShouldBeEmpty();
    }

    [Test]
    public void A_count_decrease_is_not_amplification()
    {
        // Fewer calls is not an amplification regression — only an increase (or loop-entry) flags.
        var branch = Map("http", "x", (Key(), 1, false));
        var @base = Map("http", "x", (Key(), 5, false));

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        deltas.ShouldBeEmpty();
    }

    [Test]
    public void Leaving_a_loop_is_not_amplification()
    {
        var branch = Map("http", "x", (Key(), 1, false));
        var @base = Map("http", "x", (Key(), 1, true));

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        deltas.ShouldBeEmpty();
    }

    [Test]
    public void An_amplified_ep_with_a_stable_set_is_still_reported_as_a_delta()
    {
        // The whole point: an EP whose effect SET is unchanged but has an amplified effect must be ADDED to
        // the per-EP (behavioral) list — it wasn't there under set-diff alone.
        var branch = Map("action", "stable", (Key(), 6, false));
        var @base = Map("action", "stable", (Key(), 2, false));

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("action", "stable"));

        deltas.Count.ShouldBe(1);
        deltas[0].Added.ShouldBeEmpty();
        deltas[0].Removed.ShouldBeEmpty();
        deltas[0].Amplified.ShouldNotBeEmpty();
    }
}

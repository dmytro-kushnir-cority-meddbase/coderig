using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// FR-1 lazy_init_race precision — the LOCK-ENCLOSED tier (calibration 2026-06-26: 10 of 17 FPs were
// textbook double-checked locking). A lazy-init write carrying the lock_held_across_effect span
// observation classifies to the distinct reason `lazy_init_lock_enclosed_verify_dcl` — DISCLOSED, not
// suppressed, because lock-enclosure is a proxy: DCL without `volatile` still hands the lock-free outer
// read a possibly-torn object. Hard-suppression requires BOTH corroborations: the cell is `volatile`
// (volatileCells set) AND the publish is the last lock-held effect in the method (publish-last).
// The safe-DCL / DCL-without-volatile pair below is the regression fixture the card requires.
public sealed class LazyInitLockTierTests
{
    private const string Getter = "M:App.Config.get_Instance";
    private const string Cell = "F:App.Config._instance";

    private static EffectObservationInfo LockSpan() =>
        new(
            Type: "lock_held_across_effect",
            Context: "lock",
            Detail: "System.Object",
            Confidence: "high",
            Basis: "compilation",
            Reason: "effect_inside_held_resource_scope"
        );

    private static DerivedEffect Read(int line, string enclosing = Getter, string cell = Cell) =>
        new("shared_state", "read", cell, enclosing, "f.cs", line);

    private static DerivedEffect Write(int line, bool inLock, string enclosing = Getter, string cell = Cell) =>
        new("shared_state", "mutate", cell, enclosing, "f.cs", line, Observations: inLock ? [LockSpan()] : null);

    private static EffectObservationInfo? HazardOf(IReadOnlyList<DerivedEffect> result) =>
        result
            .Single(e => e.Operation == "mutate" && e.ResourceType == Cell)
            .Observations?.FirstOrDefault(o => o.Type is "lazy_init_race" or "race_window" or "thread_local_context");

    [Test]
    public void A_lock_enclosed_lazy_init_classifies_to_the_lock_tier_not_the_bare_heuristic()
    {
        var result = FactHazardDeriver.DeriveRaceWindows([Read(10), Write(12, inLock: true)]);

        var hazard = HazardOf(result).ShouldNotBeNull();
        hazard.Type.ShouldBe("lazy_init_race");
        hazard.Reason.ShouldBe("lazy_init_lock_enclosed_verify_dcl");
    }

    [Test]
    public void A_bare_lazy_init_keeps_the_heuristic_reason()
    {
        var result = FactHazardDeriver.DeriveRaceWindows([Read(10), Write(12, inLock: false)]);

        HazardOf(result).ShouldNotBeNull().Reason.ShouldBe("lazy_init_heuristic");
    }

    // SAFE-DCL fixture: volatile cell + the publish is the last lock-held effect => the one suppression.
    [Test]
    public void A_volatile_publish_last_dcl_is_suppressed()
    {
        var volatileCells = new HashSet<string>(StringComparer.Ordinal) { Cell };

        var result = FactHazardDeriver.DeriveRaceWindows([Read(10), Write(12, inLock: true)], volatileCells: volatileCells);

        HazardOf(result).ShouldBeNull();
    }

    // DCL-WITHOUT-VOLATILE fixture: identical shape, non-volatile cell => keeps flagging at the lock tier
    // (the lock serializes writers; it does nothing for the lock-free outer read).
    [Test]
    public void A_non_volatile_dcl_stays_flagged_at_the_lock_tier()
    {
        var volatileCells = new HashSet<string>(StringComparer.Ordinal) { "F:App.Other._unrelated" };

        var result = FactHazardDeriver.DeriveRaceWindows([Read(10), Write(12, inLock: true)], volatileCells: volatileCells);

        HazardOf(result).ShouldNotBeNull().Reason.ShouldBe("lazy_init_lock_enclosed_verify_dcl");
    }

    // Publish-early fixture: volatile, but ANOTHER lock-held effect follows the publish (work after the
    // flag is set — the reorder-hazard shape) => stays flagged.
    [Test]
    public void A_volatile_publish_with_later_lock_held_work_stays_flagged()
    {
        var volatileCells = new HashSet<string>(StringComparer.Ordinal) { Cell };
        var laterLockHeldWork = new DerivedEffect("io", "write", "System.IO.File", Getter, "f.cs", 14, Observations: [LockSpan()]);

        var result = FactHazardDeriver.DeriveRaceWindows(
            [Read(10), Write(12, inLock: true), laterLockHeldWork],
            volatileCells: volatileCells
        );

        HazardOf(result).ShouldNotBeNull().Reason.ShouldBe("lazy_init_lock_enclosed_verify_dcl");
    }

    // The lock tier is scoped to the lazy-init FAMILY: a non-lazy RMW inside a lock keeps its race_window
    // classification untouched (its own isolation tiering is transaction-based, not lock-based).
    [Test]
    public void A_non_lazy_rmw_inside_a_lock_still_classifies_as_race_window()
    {
        const string method = "M:App.Svc.UpdateStatus";
        const string cell = "F:App.Svc._status";

        var result = FactHazardDeriver.DeriveRaceWindows(
            [Read(10, method, cell), Write(12, inLock: true, method, cell)],
            volatileCells: new HashSet<string>(StringComparer.Ordinal) { cell }
        );

        var hazard = result.Single(e => e.Operation == "mutate").Observations!.Single(o => o.Type is "race_window" or "lazy_init_race");
        hazard.Type.ShouldBe("race_window");
    }
}

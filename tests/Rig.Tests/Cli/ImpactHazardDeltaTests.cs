using System.IO;
using Rig.Cli.Commands;
using Shouldly;

namespace Rig.Tests.Cli;

// HAZARD DELTA — `impact` reports, per entry point, the hazard findings (race_window / lazy_init_race /
// n_plus_1 / unserializable_payload) its reach GAINED or LOST between the base and head store, mirroring the
// effect-set delta and the FR-1e guard delta. These pin the pure set-diff over per-EP hazard maps, no store:
// added = head-only, removed = base-only, keyed on (Type, Cell, param-free Enclosing); and the contract that a
// PURE hazard gain (no effect-set change) still surfaces the EP in PerEp.
public sealed class ImpactHazardDeltaTests
{
    private static ImpactCommand.EntryPointRef Ep(string kind, string route) => new(kind, route, $"/{route}.cs", 1, null);

    private static (string, string, string, string) Http(string enclosing = "N.T.M") => ("http", "GET", "Account", enclosing);

    private static ImpactCommand.HazardFinding RaceWindow(
        string cell = "N.T._status",
        string enclosing = "N.T.M",
        string confidence = "high"
    ) => new(Type: "race_window", Cell: cell, Enclosing: enclosing, Confidence: confidence);

    private static ImpactCommand.HazardFinding NPlusOne(string cell = "id", string enclosing = "N.T.M") =>
        new(Type: "n_plus_1", Cell: cell, Enclosing: enclosing, Confidence: "high");

    // A one-EP effect-footprint map.
    private static Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), ImpactCommand.EffectReach>> Effects(
        string kind,
        string route,
        params (string, string, string, string)[] keys
    )
    {
        var inner = new Dictionary<(string, string, string, string), ImpactCommand.EffectReach>();
        foreach (var k in keys)
        {
            inner[k] = new ImpactCommand.EffectReach(Count: 1, InLoop: false);
        }

        return new Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), ImpactCommand.EffectReach>>
        {
            [(kind, route)] = inner,
        };
    }

    // A one-EP hazard-set map.
    private static Dictionary<(string Kind, string Route), HashSet<ImpactCommand.HazardFinding>> Hazards(
        string kind,
        string route,
        params ImpactCommand.HazardFinding[] findings
    ) => new() { [(kind, route)] = new HashSet<ImpactCommand.HazardFinding>(findings) };

    private static IReadOnlyDictionary<(string Kind, string Route), ImpactCommand.EntryPointRef> EpByKey(string kind, string route) =>
        new Dictionary<(string Kind, string Route), ImpactCommand.EntryPointRef> { [(kind, route)] = Ep(kind, route) };

    [Test]
    public void Hazard_gained_on_an_ep_with_an_unchanged_effect_set_surfaces_in_per_ep()
    {
        // PURE hazard gain: the effect footprint is IDENTICAL on both sides (so the effect-set diff is empty),
        // but the head reach gained a race_window. The EP must still appear in PerEp.
        var branchEffects = Effects("http", "x", Http());
        var baseEffects = Effects("http", "x", Http());
        var branchHaz = Hazards("http", "x", RaceWindow());
        var baseHaz = Hazards("http", "x"); // none on base

        var deltas = ImpactCommand.DiffFootprints(
            branch: branchEffects,
            baseStore: baseEffects,
            epByKey: EpByKey("http", "x"),
            branchHazards: branchHaz,
            baseHazards: baseHaz
        );

        deltas.Count.ShouldBe(1);
        deltas[0].Added.ShouldBeEmpty();
        deltas[0].Removed.ShouldBeEmpty();
        deltas[0].HazardsAddedOrEmpty.ShouldBe([RaceWindow()]);
        deltas[0].HazardsRemovedOrEmpty.ShouldBeEmpty();
    }

    [Test]
    public void Hazard_lost_is_reported_as_removed()
    {
        // A fix: the base reach carried a race_window, the head reach no longer does.
        var branchEffects = Effects("http", "x", Http());
        var baseEffects = Effects("http", "x", Http());
        var branchHaz = Hazards("http", "x");
        var baseHaz = Hazards("http", "x", RaceWindow());

        var deltas = ImpactCommand.DiffFootprints(
            branch: branchEffects,
            baseStore: baseEffects,
            epByKey: EpByKey("http", "x"),
            branchHazards: branchHaz,
            baseHazards: baseHaz
        );

        deltas.Count.ShouldBe(1);
        deltas[0].HazardsAddedOrEmpty.ShouldBeEmpty();
        deltas[0].HazardsRemovedOrEmpty.ShouldBe([RaceWindow()]);
    }

    [Test]
    public void Unchanged_hazard_set_produces_no_delta()
    {
        // Same hazard finding on BOTH sides AND the same effect set => not a delta (the EP is unchanged).
        var branchEffects = Effects("http", "x", Http());
        var baseEffects = Effects("http", "x", Http());
        var haz = RaceWindow();
        var branchHaz = Hazards("http", "x", haz);
        var baseHaz = Hazards("http", "x", haz);

        var deltas = ImpactCommand.DiffFootprints(
            branch: branchEffects,
            baseStore: baseEffects,
            epByKey: EpByKey("http", "x"),
            branchHazards: branchHaz,
            baseHazards: baseHaz
        );

        deltas.ShouldBeEmpty();
    }

    [Test]
    public void Hazard_delta_rides_along_an_ep_already_listed_for_an_effect_change()
    {
        // The EP is already a delta (an effect was added); it just gains the hazard lists too.
        var branchEffects = Effects("http", "x", Http(), ("sql", "read", "T", "N.T.M"));
        var baseEffects = Effects("http", "x", Http());
        var branchHaz = Hazards("http", "x", NPlusOne());
        var baseHaz = Hazards("http", "x");

        var deltas = ImpactCommand.DiffFootprints(
            branch: branchEffects,
            baseStore: baseEffects,
            epByKey: EpByKey("http", "x"),
            branchHazards: branchHaz,
            baseHazards: baseHaz
        );

        deltas.Count.ShouldBe(1);
        deltas[0].Added.Count.ShouldBe(1); // the added sql effect
        deltas[0].HazardsAddedOrEmpty.ShouldBe([NPlusOne()]);
    }

    [Test]
    public void Distinct_findings_added_and_removed_are_partitioned_by_key()
    {
        // Same enclosing/cell but DIFFERENT type, plus an added one on a different cell — each is its own key,
        // so the diff partitions them cleanly into added (head-only) and removed (base-only).
        var branchEffects = Effects("http", "x", Http());
        var baseEffects = Effects("http", "x", Http());
        var branchHaz = Hazards("http", "x", RaceWindow(), NPlusOne(cell: "newId"));
        var baseHaz = Hazards("http", "x", RaceWindow(), NPlusOne(cell: "oldId"));

        var deltas = ImpactCommand.DiffFootprints(
            branch: branchEffects,
            baseStore: baseEffects,
            epByKey: EpByKey("http", "x"),
            branchHazards: branchHaz,
            baseHazards: baseHaz
        );

        deltas.Count.ShouldBe(1);
        deltas[0].HazardsAddedOrEmpty.ShouldBe([NPlusOne(cell: "newId")]); // race_window unchanged => not listed
        deltas[0].HazardsRemovedOrEmpty.ShouldBe([NPlusOne(cell: "oldId")]);
    }

    [Test]
    public void Confidence_is_not_part_of_the_diff_identity()
    {
        // The SAME finding (type+cell+enclosing) at a different confidence tier is not a gain or a loss — only
        // (Type, Cell, Enclosing) is the identity, confidence rides along for display.
        var branchEffects = Effects("http", "x", Http());
        var baseEffects = Effects("http", "x", Http());
        var branchHaz = Hazards("http", "x", RaceWindow(confidence: "medium"));
        var baseHaz = Hazards("http", "x", RaceWindow(confidence: "high"));

        var deltas = ImpactCommand.DiffFootprints(
            branch: branchEffects,
            baseStore: baseEffects,
            epByKey: EpByKey("http", "x"),
            branchHazards: branchHaz,
            baseHazards: baseHaz
        );

        // Identity is (Type, Cell, Enclosing) — equal here — so confidence-only change is NOT a delta.
        deltas.ShouldBeEmpty();
    }

    [Test]
    public void Effect_only_callers_passing_no_hazard_maps_get_empty_hazard_lists()
    {
        // The legacy effect-only overload (no hazard maps) still works: the delta carries empty hazard lists.
        var branchEffects = Effects("http", "x", Http(), ("sql", "read", "T", "N.T.M"));
        var baseEffects = Effects("http", "x", Http());

        var deltas = ImpactCommand.DiffFootprints(branch: branchEffects, baseStore: baseEffects, epByKey: EpByKey("http", "x"));

        deltas.Count.ShouldBe(1);
        deltas[0].HazardsAddedOrEmpty.ShouldBeEmpty();
        deltas[0].HazardsRemovedOrEmpty.ShouldBeEmpty();
    }

    [Test]
    public void Pure_hazard_gain_does_not_trip_the_expect_no_effect_change_gate()
    {
        // FR-4 (--expect-no-effect-change) is a deterministic EFFECT-SET gate. A pure hazard gain surfaces the
        // EP in PerEp (for display), but must NOT count as an effect change — otherwise a behavior-preserving
        // refactor that merely trips a (heuristic) hazard would fail the gate. EffectChangedEpCount is the
        // count the gate uses; it excludes hazard-only deltas.
        var deltas = ImpactCommand.DiffFootprints(
            branch: Effects("http", "x", Http()),
            baseStore: Effects("http", "x", Http()),
            epByKey: EpByKey("http", "x"),
            branchHazards: Hazards("http", "x", RaceWindow()),
            baseHazards: Hazards("http", "x")
        );
        deltas.Count.ShouldBe(1); // the EP surfaces (pure hazard gain)

        var diff = new ImpactCommand.ImpactDiff(Ep: null, AffectedEps: [], PerEp: deltas);
        ImpactCommand.EffectChangedEpCount(diff).ShouldBe(0); // ...but it is NOT an effect-set change

        using var err = new StringWriter();
        ImpactCommand
            .ExpectNoEffectChangeExit(expect: true, behavioralEpCount: ImpactCommand.EffectChangedEpCount(diff), error: err)
            .ShouldBe(0);
    }

    [Test]
    public void An_actual_effect_change_still_trips_the_expect_no_effect_change_gate()
    {
        // Sanity: the fix did not neuter FR-4 — a real added effect still counts and still trips the gate.
        var deltas = ImpactCommand.DiffFootprints(
            branch: Effects("http", "x", Http(), ("sql", "read", "T", "N.T.M")),
            baseStore: Effects("http", "x", Http()),
            epByKey: EpByKey("http", "x")
        );

        var diff = new ImpactCommand.ImpactDiff(Ep: null, AffectedEps: [], PerEp: deltas);
        ImpactCommand.EffectChangedEpCount(diff).ShouldBe(1);

        using var err = new StringWriter();
        ImpactCommand
            .ExpectNoEffectChangeExit(expect: true, behavioralEpCount: ImpactCommand.EffectChangedEpCount(diff), error: err)
            .ShouldBe(1);
    }
}

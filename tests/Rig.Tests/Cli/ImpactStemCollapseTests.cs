using Rig.Cli.Commands;
using Shouldly;

namespace Rig.Tests.Cli;

// Task 1 (stem-collapse) + Task 2 (rank by distinct stem) for the per-EP reachable-set diff. A symbol present
// in BOTH the added and removed set under the same param-free stem (StripParams) is a SIGNATURE CHANGE, not an
// add+remove — it must collapse to one `~` change, and the EP magnitude/ordering must count DISTINCT stems so a
// 30-overload ctor swap counts as 1, not 30. These pin BucketStems (the pure partition) and DiffReachSets (the
// ranked output) without touching a store.
public sealed class ImpactStemCollapseTests
{
    // --- BucketStems (Task 1) -------------------------------------------------------------------------

    [Test]
    public void Ctor_signature_change_collapses_to_a_single_changed_stem()
    {
        // Same stem (Ns.Type.#ctor) on both sides, different params => one signature change, no add/remove.
        var added = new[] { "M:Ns.Type.#ctor(System.Int32,System.String)" };
        var removed = new[] { "M:Ns.Type.#ctor(System.Int32)" };

        var b = ImpactCommand.BucketStems(added, removed);

        b.ChangedStems.ShouldBe(new[] { "Ns.Type.#ctor" });
        b.AddedStems.ShouldBeEmpty();
        b.RemovedStems.ShouldBeEmpty();
        // Raw added/removed DocIDs for the signature-changed stem drop out (they're not genuine adds/removes).
        b.Added.ShouldBeEmpty();
        b.Removed.ShouldBeEmpty();
    }

    [Test]
    public void A_genuinely_one_sided_stem_stays_added_or_removed_with_raw_docids()
    {
        var added = new[] { "M:Ns.Type.NewMethod(System.Int32)" };
        var removed = new[] { "M:Ns.Type.GoneMethod()" };

        var b = ImpactCommand.BucketStems(added, removed);

        b.AddedStems.ShouldBe(new[] { "Ns.Type.NewMethod" });
        b.RemovedStems.ShouldBe(new[] { "Ns.Type.GoneMethod" });
        b.ChangedStems.ShouldBeEmpty();
        // Raw DocIDs preserved for tooling (tsv).
        b.Added.ShouldBe(new[] { "M:Ns.Type.NewMethod(System.Int32)" });
        b.Removed.ShouldBe(new[] { "M:Ns.Type.GoneMethod()" });
    }

    [Test]
    public void Mixed_buckets_partition_correctly_and_overloads_collapse_to_one_changed()
    {
        // Two overloads added + one removed for ONE stem (signature change), plus one pure add and one pure
        // removal. The changed stem must count once regardless of how many overloads churned.
        var added = new[]
        {
            "M:Ns.Type.#ctor(System.Int32,System.String)",
            "M:Ns.Type.#ctor(System.Int32,System.String,System.Boolean)",
            "M:Ns.Add(System.Int32)",
        };
        var removed = new[] { "M:Ns.Type.#ctor(System.Int32)", "M:Ns.Drop()" };

        var b = ImpactCommand.BucketStems(added, removed);

        b.ChangedStems.ShouldBe(new[] { "Ns.Type.#ctor" });
        b.AddedStems.ShouldBe(new[] { "Ns.Add" });
        b.RemovedStems.ShouldBe(new[] { "Ns.Drop" });
        // The churned ctor overloads do NOT appear in the raw add/remove lists.
        b.Added.ShouldBe(new[] { "M:Ns.Add(System.Int32)" });
        b.Removed.ShouldBe(new[] { "M:Ns.Drop()" });
    }

    // --- DiffReachSets ranking by distinct stem (Task 2) ----------------------------------------------

    private static ImpactCommand.EntryPointRef Ep(string kind, string route) => new(kind, route, $"/{route}.cs", 1, null);

    [Test]
    public void Ordering_ranks_by_distinct_stem_so_an_overload_swap_loses_to_two_real_changes()
    {
        // EP "big" swaps 30 ctor overloads (one distinct stem); EP "small" gains two genuinely-distinct
        // methods (two distinct stems). Distinct-stem ranking must put "small" (2) ahead of "big" (1).
        var bigBranch = Enumerable.Range(0, 30).Select(i => $"M:N.C.#ctor(P{i})").ToHashSet(StringComparer.Ordinal);
        var bigBase = Enumerable.Range(100, 30).Select(i => $"M:N.C.#ctor(P{i})").ToHashSet(StringComparer.Ordinal);
        var smallBranch = new HashSet<string>(StringComparer.Ordinal) { "M:N.A.One()", "M:N.A.Two()" };
        var smallBase = new HashSet<string>(StringComparer.Ordinal);

        var branch = new Dictionary<(string Kind, string Route), HashSet<string>>
        {
            [("http", "big")] = bigBranch,
            [("http", "small")] = smallBranch,
        };
        var baseStore = new Dictionary<(string Kind, string Route), HashSet<string>>
        {
            [("http", "big")] = bigBase,
            [("http", "small")] = smallBase,
        };
        var epByKey = new Dictionary<(string Kind, string Route), ImpactCommand.EntryPointRef>
        {
            [("http", "big")] = Ep("http", "big"),
            [("http", "small")] = Ep("http", "small"),
        };

        var deltas = ImpactCommand.DiffReachSets(branch, baseStore, epByKey);

        deltas.Count.ShouldBe(2);
        deltas[0].Route.ShouldBe("small"); // 2 distinct stems
        deltas[0].DistinctStemDelta.ShouldBe(2);
        deltas[1].Route.ShouldBe("big"); // 30 overloads = 1 distinct (changed) stem
        deltas[1].DistinctStemDelta.ShouldBe(1);
        deltas[1].ChangedStems.ShouldBe(new[] { "N.C.#ctor" });
    }

    [Test]
    public void Ties_break_stably_by_kind_then_route()
    {
        var branch = new Dictionary<(string Kind, string Route), HashSet<string>>
        {
            [("http", "z")] = new(StringComparer.Ordinal) { "M:N.A.M()" },
            [("http", "a")] = new(StringComparer.Ordinal) { "M:N.B.M()" },
            [("action", "m")] = new(StringComparer.Ordinal) { "M:N.C.M()" },
        };
        var baseStore = new Dictionary<(string Kind, string Route), HashSet<string>>
        {
            [("http", "z")] = new(StringComparer.Ordinal),
            [("http", "a")] = new(StringComparer.Ordinal),
            [("action", "m")] = new(StringComparer.Ordinal),
        };
        var epByKey = new Dictionary<(string Kind, string Route), ImpactCommand.EntryPointRef>
        {
            [("http", "z")] = Ep("http", "z"),
            [("http", "a")] = Ep("http", "a"),
            [("action", "m")] = Ep("action", "m"),
        };

        var deltas = ImpactCommand.DiffReachSets(branch, baseStore, epByKey);

        // All have delta 1 → ordered by Kind (action < http) then Route (a < z), ordinal.
        deltas.Select(d => (d.Kind, d.Route)).ShouldBe(new[] { ("action", "m"), ("http", "a"), ("http", "z") });
    }

    [Test]
    public void An_ep_only_signature_changed_is_still_reported_as_affected()
    {
        // Even with zero genuine adds/removes, a signature change must keep the EP in the affected list.
        var branch = new Dictionary<(string Kind, string Route), HashSet<string>>
        {
            [("http", "x")] = new(StringComparer.Ordinal) { "M:N.C.#ctor(System.Int32)" },
        };
        var baseStore = new Dictionary<(string Kind, string Route), HashSet<string>>
        {
            [("http", "x")] = new(StringComparer.Ordinal) { "M:N.C.#ctor()" },
        };
        var epByKey = new Dictionary<(string Kind, string Route), ImpactCommand.EntryPointRef> { [("http", "x")] = Ep("http", "x") };

        var deltas = ImpactCommand.DiffReachSets(branch, baseStore, epByKey);

        deltas.Count.ShouldBe(1);
        deltas[0].ChangedStems.ShouldBe(new[] { "N.C.#ctor" });
        deltas[0].DistinctStemDelta.ShouldBe(1);
    }
}

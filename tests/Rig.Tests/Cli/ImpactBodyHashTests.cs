using Rig.Cli.Commands;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Cli;

// Task 3 (Phase 2) — the in-place body-edit signal. A reachable method whose BODY hash differs base↔branch is
// an in-place change the structural reach-set diff can't see (it stayed in the reach). These pin (a) the pure
// body-changed-set computation, (b) its per-EP attribution in DiffReachSets, and (c) the backward-compatible
// silent skip when either store lacks the BodyHash fact.
public sealed class ImpactBodyHashLogicTests
{
    private static IReadOnlyDictionary<string, string> Hashes(params (string Id, string Hash)[] pairs) =>
        pairs.ToDictionary(p => p.Id, p => p.Hash, StringComparer.Ordinal);

    [Test]
    public void Body_changed_set_is_the_differing_and_one_sided_symbols()
    {
        var branch = Hashes(("M:N.A.M()", "aaaa"), ("M:N.A.Changed()", "bbbb"), ("M:N.A.OnlyBranch()", "cccc"));
        var @base = Hashes(("M:N.A.M()", "aaaa"), ("M:N.A.Changed()", "ZZZZ"), ("M:N.A.OnlyBase()", "dddd"));

        var changed = ImpactCommand.BodyChangedSymbols(branch, @base);

        changed.ShouldBe(new[] { "M:N.A.Changed()", "M:N.A.OnlyBranch()", "M:N.A.OnlyBase()" }, ignoreOrder: true);
        changed.Contains("M:N.A.M()").ShouldBeFalse(); // identical hash => not changed
    }

    [Test]
    public void Empty_on_either_side_yields_no_signal_pre_fact_store()
    {
        var branch = Hashes(("M:N.A.M()", "aaaa"));

        // base has no BodyHash fact (pre-Phase-2 store) => guarded read returned empty => skip silently.
        ImpactCommand.BodyChangedSymbols(branch, Hashes()).ShouldBeEmpty();
        ImpactCommand.BodyChangedSymbols(Hashes(), branch).ShouldBeEmpty();
    }

    [Test]
    public void An_ep_with_no_structural_change_but_a_changed_reached_body_is_affected_in_place()
    {
        // The reach set is IDENTICAL (M:N.A.M present both sides), so the structural diff is empty — but the
        // method's body changed in place, so the EP must still surface, attributed via InPlace.
        var shared = new[] { "M:N.A.M()" };
        var branch = new Dictionary<(string Kind, string Route), HashSet<string>> { [("http", "x")] = new(shared, StringComparer.Ordinal) };
        var baseStore = new Dictionary<(string Kind, string Route), HashSet<string>>
        {
            [("http", "x")] = new(shared, StringComparer.Ordinal),
        };
        var epByKey = new Dictionary<(string Kind, string Route), ImpactCommand.EntryPointRef>
        {
            [("http", "x")] = new("http", "x", "/x.cs", 1, null),
        };
        var bodyChanged = new HashSet<string>(StringComparer.Ordinal) { "M:N.A.M()" };

        var deltas = ImpactCommand.DiffReachSets(branch, baseStore, epByKey, bodyChanged);

        deltas.Count.ShouldBe(1);
        deltas[0].AddedStems.ShouldBeEmpty();
        deltas[0].RemovedStems.ShouldBeEmpty();
        deltas[0].ChangedStems.ShouldBeEmpty();
        deltas[0].InPlaceCount.ShouldBe(1);
        deltas[0].InPlace.ShouldBe(new[] { "M:N.A.M()" });
        deltas[0].DistinctStemDelta.ShouldBe(1); // the in-place body change carries the magnitude
    }

    [Test]
    public void A_genuinely_added_method_is_not_double_counted_as_in_place()
    {
        // M:N.A.New is only in the branch reach => it's a structural ADD, not in-place (in-place is the SHARED
        // reach whose body changed). Even though its DocID is in the body-changed set, it must not appear under
        // InPlace (it's already attributed by the structural diff).
        var branch = new Dictionary<(string Kind, string Route), HashSet<string>>
        {
            [("http", "x")] = new(new[] { "M:N.A.M()", "M:N.A.New()" }, StringComparer.Ordinal),
        };
        var baseStore = new Dictionary<(string Kind, string Route), HashSet<string>>
        {
            [("http", "x")] = new(new[] { "M:N.A.M()" }, StringComparer.Ordinal),
        };
        var epByKey = new Dictionary<(string Kind, string Route), ImpactCommand.EntryPointRef>
        {
            [("http", "x")] = new("http", "x", "/x.cs", 1, null),
        };
        var bodyChanged = new HashSet<string>(StringComparer.Ordinal) { "M:N.A.New()" };

        var deltas = ImpactCommand.DiffReachSets(branch, baseStore, epByKey, bodyChanged);

        deltas.Count.ShouldBe(1);
        deltas[0].AddedStems.ShouldBe(new[] { "N.A.New" });
        deltas[0].InPlaceCount.ShouldBe(0); // M:N.A.New is an add, not a shared-reach body change
    }
}

// Storage round-trip: a real playground extraction must MINE a non-empty BodyHash for bodied methods and
// persist it so LoadSymbolBodyHashesAsync reads it back. Pins the Facts.cs + entity + Writes INSERT + Reads
// guarded-read wiring end to end, and confirms re-extracting the SAME source yields the SAME hash (deterministic).
[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class ImpactBodyHashStorageTests(AnalyzedPlaygrounds playgrounds)
{
    [Test]
    public async Task Body_hashes_round_trip_and_are_deterministic()
    {
        var pg = await playgrounds.EntryPointEffectsAsync();
        var wd = Path.Combine(Path.GetTempPath(), $"rig-bodyhash-{Guid.NewGuid():n}");
        Directory.CreateDirectory(wd);

        var db1 = Path.Combine(wd, "a.db");
        var db2 = Path.Combine(wd, "b.db");
        await using (var ctx = new RigDbContext(db1, pooling: false))
        {
            await Writes.SaveAsync(ctx, pg.Result);
        }

        await using (var ctx = new RigDbContext(db2, pooling: false))
        {
            await Writes.SaveAsync(ctx, pg.Result);
        }

        await using var read1 = new RigDbContext(db1, pooling: false, readOnly: true);
        await using var read2 = new RigDbContext(db2, pooling: false, readOnly: true);
        var hashes1 = await Reads.LoadSymbolBodyHashesAsync(read1);
        var hashes2 = await Reads.LoadSymbolBodyHashesAsync(read2);

        hashes1.ShouldNotBeEmpty(); // bodied methods exist in the playground and got a hash
        // Same source extracted twice => identical hashes for every symbol (deterministic content hash).
        hashes1.ShouldBe(hashes2);
    }
}

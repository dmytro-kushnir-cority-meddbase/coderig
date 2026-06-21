using Rig.Cli.Commands;
using Shouldly;

namespace Rig.Tests.Cli;

// FR-1e — the guard delta on a shared-mutation path. `impact` flags an entry point that GAINED or LOST a
// lock/async_lock on a path whose branch reach STILL carries a shared_state mutation (an inherently-shared
// cell — ConcurrentDictionary/Atom/static-field-write). Both the lost-guard race and the guard-adding fix
// are flagged for review. These pin the pure classification over footprint maps, without touching a store.
public sealed class ImpactGuardDeltaTests
{
    private static ImpactCommand.EntryPointRef Ep(string kind, string route) => new(kind, route, $"/{route}.cs", 1, null);

    private static (string, string, string, string) Lock(string enclosing = "N.T.M") => ("lock", "acquire", "Monitor", enclosing);

    private static (string, string, string, string) SharedMutate(string enclosing = "N.T.M") =>
        ("shared_state", "mutate", "ConcurrentDictionary", enclosing);

    private static (string, string, string, string) Http(string enclosing = "N.T.M") => ("http", "GET", "Account", enclosing);

    private static Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), ImpactCommand.EffectReach>> Map(
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

    private static IReadOnlyDictionary<(string Kind, string Route), ImpactCommand.EntryPointRef> EpByKey(string kind, string route) =>
        new Dictionary<(string Kind, string Route), ImpactCommand.EntryPointRef> { [(kind, route)] = Ep(kind, route) };

    [Test]
    public void Lock_gained_on_a_path_that_still_mutates_shared_state_is_flagged()
    {
        // The fix shape: a lock acquire is ADDED while the shared mutation persists on the path.
        var branch = Map("http", "x", Lock(), SharedMutate());
        var @base = Map("http", "x", SharedMutate());

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        deltas.Count.ShouldBe(1);
        deltas[0].SharedMutationOnPath.ShouldBeTrue();
        ImpactCommand.HasGuardDeltaOnSharedMutation(deltas[0]).ShouldBeTrue();

        var (added, removed) = ImpactCommand.GuardEffectDelta(deltas[0]);
        added.ShouldBe(["lock:acquire"]);
        removed.ShouldBeEmpty();
    }

    [Test]
    public void Lock_lost_on_a_path_that_still_mutates_shared_state_is_flagged()
    {
        // The race shape: the lock is REMOVED but the shared mutation is still reachable on the branch path.
        var branch = Map("http", "x", SharedMutate());
        var @base = Map("http", "x", Lock(), SharedMutate());

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        deltas.Count.ShouldBe(1);
        ImpactCommand.HasGuardDeltaOnSharedMutation(deltas[0]).ShouldBeTrue();

        var (added, removed) = ImpactCommand.GuardEffectDelta(deltas[0]);
        added.ShouldBeEmpty();
        removed.ShouldBe(["lock:acquire"]);
    }

    [Test]
    public void Guard_change_without_a_shared_mutation_on_path_is_not_flagged()
    {
        // A lock added on a path with NO shared_state mutation is not the FR-1e signal (no race surface).
        var branch = Map("http", "x", Lock(), Http());
        var @base = Map("http", "x", Http());

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        deltas.Count.ShouldBe(1);
        deltas[0].SharedMutationOnPath.ShouldBeFalse();
        ImpactCommand.HasGuardDeltaOnSharedMutation(deltas[0]).ShouldBeFalse();
    }

    [Test]
    public void Shared_mutation_on_path_with_no_guard_change_is_not_flagged()
    {
        // The shared mutation persists and an unrelated effect changed (so the EP IS a delta), but no
        // lock/async_lock moved — not an FR-1e guard delta.
        var branch = Map("http", "x", SharedMutate(), Http());
        var @base = Map("http", "x", SharedMutate());

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        deltas.Count.ShouldBe(1);
        deltas[0].SharedMutationOnPath.ShouldBeTrue();
        ImpactCommand.HasGuardDeltaOnSharedMutation(deltas[0]).ShouldBeFalse();
    }

    [Test]
    public void Async_lock_is_recognized_as_a_guard()
    {
        var branch = Map("http", "x", ("async_lock", "acquire", "SemaphoreSlim", "N.T.M"), SharedMutate());
        var @base = Map("http", "x", SharedMutate());

        var deltas = ImpactCommand.DiffFootprints(branch, @base, EpByKey("http", "x"));

        ImpactCommand.HasGuardDeltaOnSharedMutation(deltas[0]).ShouldBeTrue();
        ImpactCommand.GuardEffectDelta(deltas[0]).Added.ShouldBe(["async_lock:acquire"]);
    }
}

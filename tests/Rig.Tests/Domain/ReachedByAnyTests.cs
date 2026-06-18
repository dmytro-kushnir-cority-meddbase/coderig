using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

// FactPathFinder.ReachedByAny — the multi-source reverse-reach `rig impact` seeds from a diff's changed
// method set. It is the multi-source twin of ReachedBy (which `rig callers` uses): given a SET of exact
// changed-method ids, return the union of everything that can reach ANY of them, keyed to the shortest
// reverse hop to the nearest seed. These tests pin: (a) the union semantics, (b) exact-id seeding (not
// substring), (c) agreement with the single-source ReachedBy, and (d) the reverse dispatch edges (an
// override is reached via its base virtual / interface declaration), so impact's affected-EP set is the
// same closure `callers` would compute.
public sealed class ReachedByAnyTests
{
    // A -> B -> C (direct calls). Two leaves D, E off separate callers so the union is observable.
    //   Root1.A -> Mid.B -> Leaf.C
    //   Root2.X -> Leaf.C            (a second caller of the SAME leaf)
    //   Root3.Y -> Other.Z          (unrelated branch, never reaches a seed)
    private static FactGraphData LinearShape()
    {
        var edges = new[]
        {
            new CallEdge("M:N.Root1.A", "M:N.Mid.B", "invocation", "f.cs", 1),
            new CallEdge("M:N.Mid.B", "M:N.Leaf.C", "invocation", "f.cs", 2),
            new CallEdge("M:N.Root2.X", "M:N.Leaf.C", "invocation", "f.cs", 3),
            new CallEdge("M:N.Root3.Y", "M:N.Other.Z", "invocation", "f.cs", 4),
        };
        var methods = new[]
        {
            new MethodRef("M:N.Root1.A", "A", "T:N.Root1"),
            new MethodRef("M:N.Mid.B", "B", "T:N.Mid"),
            new MethodRef("M:N.Leaf.C", "C", "T:N.Leaf"),
            new MethodRef("M:N.Root2.X", "X", "T:N.Root2"),
            new MethodRef("M:N.Root3.Y", "Y", "T:N.Root3"),
            new MethodRef("M:N.Other.Z", "Z", "T:N.Other"),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, null, null);
    }

    [Test]
    public void Seeds_are_reached_at_depth_zero_and_include_themselves()
    {
        var reached = FactPathFinder.ReachedByAny(LinearShape(), new[] { "M:N.Leaf.C" });

        reached["M:N.Leaf.C"].ShouldBe(0);
    }

    [Test]
    public void Unions_the_reverse_closures_of_all_seeds()
    {
        // Seed the leaf: both callers (Mid.B -> Root1.A, and Root2.X) must appear; the unrelated branch must not.
        var reached = FactPathFinder.ReachedByAny(LinearShape(), new[] { "M:N.Leaf.C" });

        reached.Keys.ShouldContain("M:N.Mid.B");
        reached.Keys.ShouldContain("M:N.Root1.A");
        reached.Keys.ShouldContain("M:N.Root2.X");
        reached.Keys.ShouldNotContain("M:N.Root3.Y");
        reached.Keys.ShouldNotContain("M:N.Other.Z");
    }

    [Test]
    public void Depth_is_the_shortest_reverse_hop_to_the_nearest_seed()
    {
        var reached = FactPathFinder.ReachedByAny(LinearShape(), new[] { "M:N.Leaf.C" });

        reached["M:N.Mid.B"].ShouldBe(1);
        reached["M:N.Root1.A"].ShouldBe(2);
        reached["M:N.Root2.X"].ShouldBe(1);
    }

    [Test]
    public void Multiple_seeds_union_their_closures_and_take_the_min_depth()
    {
        // Seed BOTH a mid and a leaf. Root1.A reaches the leaf at reverse-depth 2, but it also reaches the
        // mid (Mid.B) at depth 1 — the union keys it to the nearer seed (1).
        var reached = FactPathFinder.ReachedByAny(LinearShape(), new[] { "M:N.Leaf.C", "M:N.Mid.B" });

        reached["M:N.Mid.B"].ShouldBe(0); // a seed
        reached["M:N.Root1.A"].ShouldBe(1); // 1 hop to Mid.B (nearer than 2 to Leaf.C)
    }

    [Test]
    public void Seeds_are_matched_by_exact_id_not_substring()
    {
        // "M:N.Leaf" is a substring of "M:N.Leaf.C" but is NOT a node — ReachedByAny seeds by EXACT id, so
        // it matches nothing (unlike ReachedBy's substring pattern). This is what keeps a diff's concrete
        // DocIDs from accidentally fanning to same-prefixed siblings.
        var reached = FactPathFinder.ReachedByAny(LinearShape(), new[] { "M:N.Leaf" });

        reached.ShouldBeEmpty();
    }

    [Test]
    public void Unknown_seed_ids_are_skipped()
    {
        var reached = FactPathFinder.ReachedByAny(LinearShape(), new[] { "M:N.DoesNotExist", "M:N.Leaf.C" });

        reached.Keys.ShouldContain("M:N.Leaf.C");
        reached.Keys.ShouldContain("M:N.Mid.B"); // the real seed's closure still resolves
    }

    [Test]
    public void Agrees_with_single_source_ReachedBy_for_one_seed()
    {
        // The contract that lets `impact` claim it IS the `callers` engine: for a single exact seed,
        // ReachedByAny's closure equals ReachedBy's (which matches the same id as a full-DocID pattern).
        var graph = LinearShape();
        var any = FactPathFinder.ReachedByAny(graph, new[] { "M:N.Leaf.C" });
        var single = FactPathFinder.ReachedBy(graph, "M:N.Leaf.C");

        any.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(single.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Test]
    public void Reaches_an_override_seed_via_its_base_virtual_caller_reverse_dispatch()
    {
        // Reverse dispatch: a caller of the BASE virtual reverse-reaches the OVERRIDE (the override is a
        // runtime target of the base call). So seeding a changed override surfaces the polymorphic caller —
        // exactly the blast radius impact must report for a changed override.
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.Base.V", "invocation", "f.cs", 1) };
        var bases = new[] { new BaseEdge("T:N.Impl", "T:N.Base") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Base.V", "V", "T:N.Base"),
            new MethodRef("M:N.Impl.V", "V", "T:N.Impl", IsOverride: true),
        };
        var mined = new[] { new DispatchFact("M:N.Base.V", "M:N.Impl.V", "override") };
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);

        var reached = FactPathFinder.ReachedByAny(graph, new[] { "M:N.Impl.V" });

        reached.Keys.ShouldContain("M:N.Base.V"); // the base virtual reaches the override forward
        reached.Keys.ShouldContain("M:N.Caller.Go"); // ...and its caller, transitively
    }
}

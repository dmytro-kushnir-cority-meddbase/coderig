using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Pure-domain tests for the mined-first dispatch resolution + Basis provenance (docs/
// HANDOFF-exact-dispatch-facts.md): mined dispatch facts are authoritative when present
// (Basis="roslyn", CHA suppressed for that member), the name/arity CHA fallback fires only for
// members WITHOUT a mined edge (Basis="heuristic"), and the error-type (`!:`) simple-name recovery
// stays on regardless — the recall rail. No Roslyn, no SQLite.
public sealed class MinedDispatchTests
{
    [Fact]
    public void Cha_fallback_edges_are_marked_heuristic_when_no_mined_facts_exist()
    {
        // The pre-mining world: a graph with NO MinedDispatch. Interface impl + base override are
        // resolved by the name/arity CHA scan — still produced (recall preserved), now flagged.
        var impls = new[] { new ImplementsEdge("T:N.Impl", "T:N.IFoo") };
        var bases = new[] { new BaseEdge("T:N.Sub", "T:N.Base") };
        var methods = new[]
        {
            new MethodRef("M:N.IFoo.M", "M", "T:N.IFoo"),
            new MethodRef("M:N.Impl.M", "M", "T:N.Impl"),
            new MethodRef("M:N.Base.V", "V", "T:N.Base"),
            new MethodRef("M:N.Sub.V", "V", "T:N.Sub", IsOverride: true),
        };
        var graph = new FactGraphData(System.Array.Empty<CallEdge>(), impls, methods, bases);

        var edges = FactPathFinder.AllDispatchEdges(graph).ToList();

        edges.ShouldContain(e => e.From == "M:N.IFoo.M" && e.To == "M:N.Impl.M" && e.Kind == "impl-dispatch" && e.Basis == "heuristic");
        edges.ShouldContain(e => e.From == "M:N.Base.V" && e.To == "M:N.Sub.V" && e.Kind == "override-dispatch" && e.Basis == "heuristic");
    }

    [Fact]
    public void Mined_facts_are_authoritative_and_suppress_the_cha_scan_for_that_member()
    {
        // Both Impl and Other declare a same-name, same-arity M and carry a REAL interface edge —
        // the shape name/arity CHA cannot tell apart. The mined fact says the interface member is
        // implemented ONLY by Impl.M (e.g. Other's same-named M is an unrelated overload), so Other.M
        // must NOT be a target, and the edge must carry roslyn provenance.
        var impls = new[] { new ImplementsEdge("T:N.Impl", "T:N.IFoo"), new ImplementsEdge("T:N.Other", "T:N.IFoo") };
        var methods = new[]
        {
            new MethodRef("M:N.IFoo.M", "M", "T:N.IFoo"),
            new MethodRef("M:N.Impl.M", "M", "T:N.Impl"),
            new MethodRef("M:N.Other.M", "M", "T:N.Other"),
        };
        var mined = new[] { new DispatchFact("M:N.IFoo.M", "M:N.Impl.M", "impl") };
        var graph = new FactGraphData(System.Array.Empty<CallEdge>(), impls, methods, null, mined);

        var fromIFoo = FactPathFinder.AllDispatchEdges(graph).Where(e => e.From == "M:N.IFoo.M").ToList();

        fromIFoo.Count.ShouldBe(1);
        fromIFoo[0].To.ShouldBe("M:N.Impl.M");
        fromIFoo[0].Basis.ShouldBe("roslyn");
    }

    [Fact]
    public void Error_type_simple_name_recovery_stays_on_alongside_mined_facts()
    {
        // Broken.M's interface edge never bound (`!:IFoo` — net48 partial binding), so Roslyn could
        // not mine it; the simple-name recovery must still surface it (the highest-recall feature),
        // marked heuristic — WHILE the mined edge for the bound impl stays roslyn-exact.
        var impls = new[] { new ImplementsEdge("T:N.Impl", "T:N.IFoo"), new ImplementsEdge("T:N.Broken", "!:IFoo") };
        var methods = new[]
        {
            new MethodRef("M:N.IFoo.M", "M", "T:N.IFoo"),
            new MethodRef("M:N.Impl.M", "M", "T:N.Impl"),
            new MethodRef("M:N.Broken.M", "M", "T:N.Broken"),
        };
        var mined = new[] { new DispatchFact("M:N.IFoo.M", "M:N.Impl.M", "impl") };
        var graph = new FactGraphData(System.Array.Empty<CallEdge>(), impls, methods, null, mined);

        var fromIFoo = FactPathFinder.AllDispatchEdges(graph).Where(e => e.From == "M:N.IFoo.M").ToList();

        fromIFoo.ShouldContain(e => e.To == "M:N.Impl.M" && e.Basis == "roslyn");
        fromIFoo.ShouldContain(e => e.To == "M:N.Broken.M" && e.Basis == "heuristic");
        fromIFoo.Count.ShouldBe(2);
    }

    [Fact]
    public void Mined_closure_respects_receiver_narrowing_to_a_grandchild()
    {
        // Override chain Base.V <- Mid.V <- Leaf.V (immediate mined hops). A call site whose static
        // receiver is Leaf must dispatch to Leaf.V ONLY — the closure walks THROUGH the narrowed-out
        // intermediate Mid.V to find it, and trims Mid.V itself.
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.Base.V", "invocation", "f.cs", 1, ReceiverType: "N.Leaf") };
        var bases = new[] { new BaseEdge("T:N.Mid", "T:N.Base"), new BaseEdge("T:N.Leaf", "T:N.Mid") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Base.V", "V", "T:N.Base"),
            new MethodRef("M:N.Mid.V", "V", "T:N.Mid", IsOverride: true),
            new MethodRef("M:N.Leaf.V", "V", "T:N.Leaf", IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.Base.V", "M:N.Mid.V", "override"),
            new DispatchFact("M:N.Mid.V", "M:N.Leaf.V", "override"),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases, mined);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.Leaf.V");
        reach.Keys.ShouldNotContain("M:N.Mid.V");
    }

    [Fact]
    public void Reverse_traversal_crosses_mined_dispatch_edges()
    {
        // callers must climb the mined seam: Leaf impl <- interface member <- the interface caller.
        var edges = new[]
        {
            new CallEdge("M:N.EP.Run", "M:N.IFoo.M", "invocation", "f.cs", 1),
            new CallEdge("M:N.Impl.M", "M:N.Leaf.Do", "invocation", "f.cs", 2),
        };
        var impls = new[] { new ImplementsEdge("T:N.Impl", "T:N.IFoo") };
        var methods = new[]
        {
            new MethodRef("M:N.EP.Run", "Run", "T:N.EP"),
            new MethodRef("M:N.IFoo.M", "M", "T:N.IFoo"),
            new MethodRef("M:N.Impl.M", "M", "T:N.Impl"),
            new MethodRef("M:N.Leaf.Do", "Do", "T:N.Leaf"),
        };
        var mined = new[] { new DispatchFact("M:N.IFoo.M", "M:N.Impl.M", "impl") };
        var graph = new FactGraphData(edges, impls, methods, null, mined);

        var reached = FactPathFinder.ReachedBy(graph, "Leaf.Do");

        reached.Keys.ShouldContain("M:N.Impl.M"); // direct caller
        reached.Keys.ShouldContain("M:N.IFoo.M"); // reverse mined impl-dispatch
        reached.Keys.ShouldContain("M:N.EP.Run"); // caller of the interface method
    }
}

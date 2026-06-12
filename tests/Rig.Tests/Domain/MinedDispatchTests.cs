using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class MinedDispatchTests
{
    [Test]
    public void Cha_fallback_edges_are_marked_heuristic_when_no_mined_facts_exist()
    {
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

    [Test]
    public void Mined_facts_are_authoritative_and_suppress_the_cha_scan_for_that_member()
    {
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

    [Test]
    public void Error_type_simple_name_recovery_stays_on_alongside_mined_facts()
    {
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

    [Test]
    public void Mined_closure_respects_receiver_narrowing_to_a_grandchild()
    {
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

    [Test]
    public void Reverse_traversal_crosses_mined_dispatch_edges()
    {
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

        reached.Keys.ShouldContain("M:N.Impl.M");
        reached.Keys.ShouldContain("M:N.IFoo.M");
        reached.Keys.ShouldContain("M:N.EP.Run");
    }
}

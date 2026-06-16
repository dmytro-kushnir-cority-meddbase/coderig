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

    // An interface method whose impl resolves to an INHERITED base method must not fan into that base
    // method's unrelated override siblings. ILogger.Startup is implemented by Impl : ServiceBase (the
    // inherited ServiceBase.Startup satisfies it), so the mined impl edge points at ServiceBase.Startup —
    // which is ALSO overridden by unrelated services (SvcA/SvcB that are NOT ILogger impls). Resolving
    // ILogger.Startup must stop at ServiceBase.Startup, not cross into SvcA/SvcB. (Real-world: MedDBase's
    // IPerformanceLogger.Startup fanned out to 32 service Startups via the inherited ServiceBase.Startup.)
    [Test]
    public void Impl_dispatch_to_an_inherited_base_method_does_not_fan_into_its_unrelated_overrides()
    {
        var impls = new[] { new ImplementsEdge("T:N.Impl", "T:N.ILogger") };
        var bases = new[]
        {
            new BaseEdge("T:N.Impl", "T:N.ServiceBase"),
            new BaseEdge("T:N.SvcA", "T:N.ServiceBase"),
            new BaseEdge("T:N.SvcB", "T:N.ServiceBase"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.ILogger.Startup", "Startup", "T:N.ILogger"),
            new MethodRef("M:N.ServiceBase.Startup", "Startup", "T:N.ServiceBase"),
            new MethodRef("M:N.SvcA.Startup", "Startup", "T:N.SvcA", IsOverride: true),
            new MethodRef("M:N.SvcB.Startup", "Startup", "T:N.SvcB", IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.ILogger.Startup", "M:N.ServiceBase.Startup", "impl"),
            new DispatchFact("M:N.ServiceBase.Startup", "M:N.SvcA.Startup", "override"),
            new DispatchFact("M:N.ServiceBase.Startup", "M:N.SvcB.Startup", "override"),
        };
        var graph = new FactGraphData(System.Array.Empty<CallEdge>(), impls, methods, bases, mined);

        var fromLogger = FactPathFinder.AllDispatchEdges(graph).Where(e => e.From == "M:N.ILogger.Startup").Select(e => e.To).ToList();
        // Resolves to the inherited impl...
        fromLogger.ShouldContain("M:N.ServiceBase.Startup");
        // ...but NOT into the unrelated service overrides (the bug was a ×N fan-out here).
        fromLogger.ShouldNotContain("M:N.SvcA.Startup");
        fromLogger.ShouldNotContain("M:N.SvcB.Startup");

        // Regression guard: a DIRECT call to the base virtual STILL fans to its overrides.
        var fromBase = FactPathFinder.AllDispatchEdges(graph).Where(e => e.From == "M:N.ServiceBase.Startup").Select(e => e.To).ToList();
        fromBase.ShouldContain("M:N.SvcA.Startup");
        fromBase.ShouldContain("M:N.SvcB.Startup");
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

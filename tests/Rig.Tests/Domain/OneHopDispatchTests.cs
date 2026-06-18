using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

// One-hop dispatch: resolving a virtual/interface/base call yields the CONCRETE runtime method; that
// method must not be RE-dispatched as if it were another virtual call site. The motivating bug: an
// interface method (IPerformanceLogger.Startup) whose impl resolves to an INHERITED base method
// (ServiceBase.Startup) was leaking into that base method's 31 unrelated service overrides — so a
// single-entity cache fetch "reached" SideBySideManager. The fix suppresses the re-dispatch of a node
// that was itself reached via a dispatch edge, while still walking that node's BODY (its direct calls).
//
// The fix is scoped to the user-facing forward traversals (Reaches/BuildTree/Find via Successors'
// `fromDispatch`); the receiver-blind oracle (ReachableFromAll) is intentionally left as the all-hops
// superset (it backs the SQL-equivalence contract), which several tests below assert explicitly.
public sealed class OneHopDispatchTests
{
    // ---- shared shapes ------------------------------------------------------------------------------

    // The canonical bug shape:
    //   interface ILogger { Startup }              EP.Run -> ILogger.Startup   (interface-typed call)
    //   class Impl : ServiceBase, ILogger          (inherits ServiceBase.Startup; does NOT override)
    //   class ServiceBase { virtual Startup }      ILogger.Startup --impl--> ServiceBase.Startup
    //   class SvcA : ServiceBase { override }       ServiceBase.Startup --override--> SvcA/SvcB
    //   class SvcB : ServiceBase { override }       (SvcA/SvcB are NOT ILogger implementers)
    private static FactGraphData InheritedInterfaceImplShape(params CallEdge[] extraEdges)
    {
        var edges = new List<CallEdge> { new("M:N.EP.Run", "M:N.ILogger.Startup", "invocation", "f.cs", 1) };
        edges.AddRange(extraEdges);
        var impls = new[] { new ImplementsEdge("T:N.Impl", "T:N.ILogger") };
        var bases = new[]
        {
            new BaseEdge("T:N.Impl", "T:N.ServiceBase"),
            new BaseEdge("T:N.SvcA", "T:N.ServiceBase"),
            new BaseEdge("T:N.SvcB", "T:N.ServiceBase"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.EP.Run", "Run", "T:N.EP"),
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
        return new FactGraphData(edges.ToArray(), impls, methods, bases, mined);
    }

    private static HashSet<string> Flatten(IReadOnlyList<TraceNode> roots)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Walk(TraceNode n)
        {
            set.Add(n.SymbolId);
            foreach (var c in n.Children)
            {
                Walk(c);
            }
        }

        foreach (var r in roots)
        {
            Walk(r);
        }

        return set;
    }

    // ---- Reaches (forward reachability) -------------------------------------------------------------

    [Test]
    public void Reaches_does_not_re_dispatch_an_impl_resolved_inherited_base_method()
    {
        var reach = FactPathFinder.Reaches(InheritedInterfaceImplShape(), "M:N.EP.Run");

        // The interface resolves to the inherited concrete impl...
        reach.Keys.ShouldContain("M:N.ServiceBase.Startup");
        // ...but the base method's unrelated service overrides are NOT reachable.
        reach.Keys.ShouldNotContain("M:N.SvcA.Startup");
        reach.Keys.ShouldNotContain("M:N.SvcB.Startup");
    }

    [Test]
    public void Reaches_keeps_the_first_dispatch_hop_intact()
    {
        // One-hop must not over-suppress: the call site EP.Run -> ILogger.Startup is reached via a real
        // call, so ILogger.Startup's OWN fan-out (the first dispatch hop) still fires.
        var reach = FactPathFinder.Reaches(InheritedInterfaceImplShape(), "M:N.EP.Run");

        reach.Keys.ShouldContain("M:N.ILogger.Startup");
        reach.Keys.ShouldContain("M:N.ServiceBase.Startup");
    }

    [Test]
    public void Reaches_walks_the_body_of_a_dispatch_reached_node()
    {
        // ServiceBase.Startup is reached via dispatch; its DIRECT calls (body) must still be walked —
        // only its re-dispatch is suppressed.
        var graph = InheritedInterfaceImplShape(new CallEdge("M:N.ServiceBase.Startup", "M:N.Db.Save", "invocation", "f.cs", 9));

        var reach = FactPathFinder.Reaches(graph, "M:N.EP.Run");

        reach.Keys.ShouldContain("M:N.Db.Save");
        reach.Keys.ShouldNotContain("M:N.SvcA.Startup");
    }

    [Test]
    public void Reaches_re_enables_dispatch_for_a_real_call_inside_a_dispatch_reached_body()
    {
        // ServiceBase.Startup (dispatch-reached) body calls another INTERFACE method via a real call.
        // That real call is not a dispatch edge, so the target re-enables dispatch and resolves normally.
        var graph = InheritedInterfaceImplShape(new CallEdge("M:N.ServiceBase.Startup", "M:N.IBar.N", "invocation", "f.cs", 9));
        var withBar = new FactGraphData(
            graph.CallEdges,
            new[] { new ImplementsEdge("T:N.Impl", "T:N.ILogger"), new ImplementsEdge("T:N.BarImpl", "T:N.IBar") },
            graph
                .Methods.Concat(new[] { new MethodRef("M:N.IBar.N", "N", "T:N.IBar"), new MethodRef("M:N.BarImpl.N", "N", "T:N.BarImpl") })
                .ToArray(),
            graph.BaseEdges,
            graph.MinedDispatch!.Concat(new[] { new DispatchFact("M:N.IBar.N", "M:N.BarImpl.N", "impl") }).ToArray()
        );

        var reach = FactPathFinder.Reaches(withBar, "M:N.EP.Run");

        reach.Keys.ShouldContain("M:N.IBar.N");
        reach.Keys.ShouldContain("M:N.BarImpl.N"); // dispatch fired again for the body's real interface call
        reach.Keys.ShouldNotContain("M:N.SvcA.Startup");
    }

    [Test]
    public void Reaches_a_directly_called_base_virtual_still_fans_to_all_overrides()
    {
        // When the base virtual is reached via a REAL call (not via dispatch), its fan-out is NOT
        // suppressed — a genuinely polymorphic ServiceBase.Startup() call hits every override.
        var graph = InheritedInterfaceImplShape(new CallEdge("M:N.Host.Boot", "M:N.ServiceBase.Startup", "invocation", "f.cs", 5));

        var reach = FactPathFinder.Reaches(graph, "M:N.Host.Boot");

        reach.Keys.ShouldContain("M:N.SvcA.Startup");
        reach.Keys.ShouldContain("M:N.SvcB.Startup");
    }

    [Test]
    public void Reaches_keeps_transitive_overrides_via_the_first_hop_closure()
    {
        // No recall loss for deep override chains: a base virtual called directly resolves its WHOLE
        // transitive override closure in ONE dispatch hop, so the grandchild is a direct target of the
        // base — reached even though the middle override is itself dispatch-reached (and not re-dispatched).
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.Base.V", "invocation", "f.cs", 1) };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Base.V", "V", "T:N.Base"),
            new MethodRef("M:N.Mid.V", "V", "T:N.Mid", IsOverride: true),
            new MethodRef("M:N.Leaf.V", "V", "T:N.Leaf", IsOverride: true),
        };
        var bases = new[] { new BaseEdge("T:N.Mid", "T:N.Base"), new BaseEdge("T:N.Leaf", "T:N.Mid") };
        var mined = new[]
        {
            new DispatchFact("M:N.Base.V", "M:N.Mid.V", "override"),
            new DispatchFact("M:N.Mid.V", "M:N.Leaf.V", "override"),
        };
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.Mid.V");
        reach.Keys.ShouldContain("M:N.Leaf.V");
    }

    [Test]
    public void Reaches_keeps_a_re_implementing_override_via_its_own_direct_impl_edge()
    {
        // SvcA ALSO implements ILogger and overrides Startup, so per-type mining emits a DIRECT impl edge
        // ILogger.Startup -> SvcA.Startup. That override is a legit interface target and must stay reachable,
        // even though SvcB (an override that does NOT implement ILogger) is correctly excluded.
        var edges = new[] { new CallEdge("M:N.EP.Run", "M:N.ILogger.Startup", "invocation", "f.cs", 1) };
        var impls = new[] { new ImplementsEdge("T:N.Impl", "T:N.ILogger"), new ImplementsEdge("T:N.SvcA", "T:N.ILogger") };
        var bases = new[]
        {
            new BaseEdge("T:N.Impl", "T:N.ServiceBase"),
            new BaseEdge("T:N.SvcA", "T:N.ServiceBase"),
            new BaseEdge("T:N.SvcB", "T:N.ServiceBase"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.EP.Run", "Run", "T:N.EP"),
            new MethodRef("M:N.ILogger.Startup", "Startup", "T:N.ILogger"),
            new MethodRef("M:N.ServiceBase.Startup", "Startup", "T:N.ServiceBase"),
            new MethodRef("M:N.SvcA.Startup", "Startup", "T:N.SvcA", IsOverride: true),
            new MethodRef("M:N.SvcB.Startup", "Startup", "T:N.SvcB", IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.ILogger.Startup", "M:N.ServiceBase.Startup", "impl"),
            new DispatchFact("M:N.ILogger.Startup", "M:N.SvcA.Startup", "impl"), // SvcA re-implements ILogger
            new DispatchFact("M:N.ServiceBase.Startup", "M:N.SvcA.Startup", "override"),
            new DispatchFact("M:N.ServiceBase.Startup", "M:N.SvcB.Startup", "override"),
        };
        var graph = new FactGraphData(edges, impls, methods, bases, mined);

        var reach = FactPathFinder.Reaches(graph, "M:N.EP.Run");

        reach.Keys.ShouldContain("M:N.ServiceBase.Startup"); // Impl's inherited impl
        reach.Keys.ShouldContain("M:N.SvcA.Startup"); // legit re-implementer, direct impl edge
        reach.Keys.ShouldNotContain("M:N.SvcB.Startup"); // override only, NOT an ILogger impl
    }

    [Test]
    public void Reaches_an_impl_resolving_to_a_leaf_method_reaches_only_the_leaf()
    {
        // Sanity: when the impl target is a leaf (no overrides), nothing extra is reached either way.
        var edges = new[] { new CallEdge("M:N.EP.Run", "M:N.IFoo.M", "invocation", "f.cs", 1) };
        var impls = new[] { new ImplementsEdge("T:N.Impl", "T:N.IFoo") };
        var methods = new[]
        {
            new MethodRef("M:N.EP.Run", "Run", "T:N.EP"),
            new MethodRef("M:N.IFoo.M", "M", "T:N.IFoo"),
            new MethodRef("M:N.Impl.M", "M", "T:N.Impl"),
        };
        var mined = new[] { new DispatchFact("M:N.IFoo.M", "M:N.Impl.M", "impl") };
        var graph = new FactGraphData(edges, impls, methods, null, mined);

        var reach = FactPathFinder.Reaches(graph, "M:N.EP.Run");

        reach.Keys.ShouldContain("M:N.Impl.M");
        reach.Count.ShouldBe(3); // EP.Run, IFoo.M, Impl.M
    }

    [Test]
    public void Reaches_still_narrows_dispatch_by_a_concrete_receiver()
    {
        // One-hop coexists with receiver narrowing: a concrete receiver pins the override, and the single
        // resolved target is not re-dispatched either.
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
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.Leaf.V");
        reach.Keys.ShouldNotContain("M:N.Mid.V"); // narrowed away by the concrete receiver
    }

    // ---- BuildTree (rig tree) -----------------------------------------------------------------------

    [Test]
    public void BuildTree_does_not_attach_unrelated_overrides_under_an_impl_resolved_base()
    {
        var tree = FactPathFinder.BuildTree(InheritedInterfaceImplShape(), "M:N.EP.Run");
        var nodes = Flatten(tree);

        nodes.ShouldContain("M:N.ServiceBase.Startup");
        nodes.ShouldNotContain("M:N.SvcA.Startup");
        nodes.ShouldNotContain("M:N.SvcB.Startup");
    }

    [Test]
    public void BuildTree_attaches_the_impl_target_directly_under_the_interface_method()
    {
        var tree = FactPathFinder.BuildTree(InheritedInterfaceImplShape(), "M:N.EP.Run");

        var run = tree.Single(n => n.SymbolId == "M:N.EP.Run");
        var iface = run.Children.Single(c => c.SymbolId == "M:N.ILogger.Startup");
        var impl = iface.Children.Single(c => c.SymbolId == "M:N.ServiceBase.Startup");
        // The resolved impl is a leaf in the tree — no re-dispatched override children hang off it.
        impl.Children.ShouldBeEmpty();
    }

    [Test]
    public void BuildTree_walks_the_body_of_a_dispatch_reached_node()
    {
        var graph = InheritedInterfaceImplShape(new CallEdge("M:N.ServiceBase.Startup", "M:N.Db.Save", "invocation", "f.cs", 9));
        var nodes = Flatten(FactPathFinder.BuildTree(graph, "M:N.EP.Run"));

        nodes.ShouldContain("M:N.Db.Save");
        nodes.ShouldNotContain("M:N.SvcA.Startup");
    }

    [Test]
    public void BuildTree_a_directly_called_base_virtual_still_fans_out()
    {
        var graph = InheritedInterfaceImplShape(new CallEdge("M:N.Host.Boot", "M:N.ServiceBase.Startup", "invocation", "f.cs", 5));
        var nodes = Flatten(FactPathFinder.BuildTree(graph, "M:N.Host.Boot"));

        nodes.ShouldContain("M:N.SvcA.Startup");
        nodes.ShouldContain("M:N.SvcB.Startup");
    }

    // ---- Find (rig path) ----------------------------------------------------------------------------

    [Test]
    public void Find_has_no_path_from_an_interface_method_to_an_unrelated_sibling_override()
    {
        var path = FactPathFinder.Find(InheritedInterfaceImplShape(), "M:N.ILogger.Startup", "M:N.SvcA.Startup");
        path.ShouldBeNull();
    }

    [Test]
    public void Find_finds_the_path_to_the_inherited_impl_target()
    {
        var path = FactPathFinder.Find(InheritedInterfaceImplShape(), "M:N.ILogger.Startup", "M:N.ServiceBase.Startup");

        path.ShouldNotBeNull();
        path!.Select(s => s.SymbolId).ShouldBe(new[] { "M:N.ILogger.Startup", "M:N.ServiceBase.Startup" });
    }

    [Test]
    public void Find_has_no_path_from_the_entry_point_to_the_unrelated_override()
    {
        var path = FactPathFinder.Find(InheritedInterfaceImplShape(), "M:N.EP.Run", "M:N.SvcB.Startup");
        path.ShouldBeNull();
    }

    [Test]
    public void Find_still_reaches_a_directly_called_base_virtuals_overrides()
    {
        var graph = InheritedInterfaceImplShape(new CallEdge("M:N.Host.Boot", "M:N.ServiceBase.Startup", "invocation", "f.cs", 5));
        var path = FactPathFinder.Find(graph, "M:N.Host.Boot", "M:N.SvcA.Startup");

        path.ShouldNotBeNull();
        path!.Last().SymbolId.ShouldBe("M:N.SvcA.Startup");
    }

    // ---- The blind oracle (ReachableFromAll) is intentionally unchanged -----------------------------

    [Test]
    public void ReachableFromAll_oracle_still_traverses_all_hops()
    {
        // The receiver-blind oracle that backs the SQL-equivalence contract is NOT one-hop: it keeps the
        // all-hops superset, so the unrelated overrides remain in its (conservative) reachable set. This
        // is what lets the in-memory engine be more precise than the oracle WITHOUT breaking equivalence.
        var reach = FactPathFinder.ReachableFromAll(InheritedInterfaceImplShape(), new[] { "M:N.EP.Run" });

        reach.ShouldContain("M:N.ServiceBase.Startup");
        reach.ShouldContain("M:N.SvcA.Startup");
        reach.ShouldContain("M:N.SvcB.Startup");
    }

    // ---- DispatchTargets edge-level closure (the companion cross-kind fix) --------------------------

    [Test]
    public void AllDispatchEdges_does_not_emit_a_direct_cross_kind_edge_from_the_interface_to_overrides()
    {
        // Companion to one-hop: within a SINGLE resolution the mined closure never crosses impl->override,
        // so the interface method's own dispatch-edge set excludes the base method's overrides.
        var fromIface = FactPathFinder
            .AllDispatchEdges(InheritedInterfaceImplShape())
            .Where(e => e.From == "M:N.ILogger.Startup")
            .Select(e => e.To)
            .ToList();

        fromIface.ShouldContain("M:N.ServiceBase.Startup");
        fromIface.ShouldNotContain("M:N.SvcA.Startup");
        fromIface.ShouldNotContain("M:N.SvcB.Startup");
    }
}

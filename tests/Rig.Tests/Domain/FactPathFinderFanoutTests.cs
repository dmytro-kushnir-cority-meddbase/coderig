using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Unit tests for loop-fanout propagation in FactPathFinder (powers `rig reaches` / `rig path`).
// Loop context rides on the CallEdge (sourced from reference_facts EnclosingLoopKind/Detail);
// ReachesWithFanout accumulates the count of looped call edges along the shortest path to each
// node, and Find annotates each hop with the enclosing loop of the call that reached it.
// Pure in-memory graph — no Roslyn, no SQLite.
public sealed class FactPathFinderFanoutTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct().Select(M).ToArray();
        return new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), nodes);
    }

    [Fact]
    public void Reaches_counts_looped_edges_and_inherits_through_unlooped_edges()
    {
        // M:A -(foreach)-> M:B -> M:C , plus an unlooped sibling M:A -> M:D
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "x in xs"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20),
            new CallEdge("M:A", "M:D", "invocation", "f.cs", 30)
        );

        var info = FactPathFinder.ReachesWithFanout(graph, "M:A");

        info["M:B"].LoopNesting.ShouldBe(1);
        info["M:B"].NearestLoopDetail.ShouldBe("x in xs");
        info["M:C"].LoopNesting.ShouldBe(1); // inherited through the unlooped B->C edge
        info["M:C"].NearestLoopDetail.ShouldBe("x in xs");
        info["M:D"].LoopNesting.ShouldBe(0); // sibling reached without crossing a loop
    }

    [Fact]
    public void Nested_loops_on_the_path_accumulate_and_report_the_innermost()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "a in aa"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20, "for", "i")
        );

        var info = FactPathFinder.ReachesWithFanout(graph, "M:A");

        info["M:C"].LoopNesting.ShouldBe(2);
        info["M:C"].NearestLoopKind.ShouldBe("for"); // innermost loop wrapping the call chain
    }

    [Fact]
    public void BuildTree_materializes_the_call_tree_with_loops_and_breaks_cycles()
    {
        // M:A -(foreach)-> M:B -> M:C , and a cycle M:C -> M:A
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "x in xs"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20),
            new CallEdge("M:C", "M:A", "invocation", "f.cs", 30)
        );

        var roots = FactPathFinder.BuildTree(graph, "M:A");

        roots.Count.ShouldBe(1);
        var a = roots[0];
        a.SymbolId.ShouldBe("M:A");
        a.EdgeKind.ShouldBe("entry");

        var b = a.Children.Single(c => c.SymbolId == "M:B");
        b.LoopKind.ShouldBe("foreach"); // A->B call site is in a loop
        b.LoopDetail.ShouldBe("x in xs");

        var cNode = b.Children.Single(c => c.SymbolId == "M:C");
        // C -> A closes a cycle; A was already expanded, so it's a truncated "seen" leaf.
        var backToA = cNode.Children.Single(c => c.SymbolId == "M:A");
        backToA.Truncated.ShouldBeTrue();
        backToA.Children.ShouldBeEmpty();
    }

    [Fact]
    public void BuildTree_expands_each_method_once()
    {
        // Diamond: A->B, A->C, B->D, C->D. D is expanded once (under B); under C it's "seen".
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10),
            new CallEdge("M:A", "M:C", "invocation", "f.cs", 11),
            new CallEdge("M:B", "M:D", "invocation", "f.cs", 20),
            new CallEdge("M:C", "M:D", "invocation", "f.cs", 30),
            new CallEdge("M:D", "M:E", "invocation", "f.cs", 40)
        );

        var a = FactPathFinder.BuildTree(graph, "M:A").Single();
        var underB = a.Children.Single(c => c.SymbolId == "M:B").Children.Single(c => c.SymbolId == "M:D");
        var underC = a.Children.Single(c => c.SymbolId == "M:C").Children.Single(c => c.SymbolId == "M:D");

        underB.Truncated.ShouldBeFalse();
        underB.Children.ShouldContain(c => c.SymbolId == "M:E"); // expanded once, here
        underC.Truncated.ShouldBeTrue(); // second encounter = seen leaf
        underC.Children.ShouldBeEmpty();
    }

    [Fact]
    public void ReachedBy_finds_transitive_callers_including_interface_dispatch()
    {
        // EP.Run -> I.M (interface call) ; T.M implements I.M ; T.M -> Leaf.Do
        // Reverse from Leaf.Do must reach T.M (direct), I.M (reverse impl-dispatch), EP.Run (caller).
        var edges = new[]
        {
            new CallEdge("M:EP.Run", "M:I.M", "invocation", "f.cs", 1),
            new CallEdge("M:T.M", "M:Leaf.Do", "invocation", "f.cs", 2),
        };
        var impls = new[] { new ImplementsEdge("T:T", "T:I") };
        var methods = new[]
        {
            new MethodRef("M:EP.Run", "Run", "T:EP"),
            new MethodRef("M:I.M", "M", "T:I"),
            new MethodRef("M:T.M", "M", "T:T"),
            new MethodRef("M:Leaf.Do", "Do", "T:Leaf"),
        };
        var graph = new FactGraphData(edges, impls, methods);

        var reached = FactPathFinder.ReachedBy(graph, "Leaf.Do");

        reached.Keys.ShouldContain("M:T.M"); // direct caller
        reached.Keys.ShouldContain("M:I.M"); // reverse impl-dispatch
        reached.Keys.ShouldContain("M:EP.Run"); // caller of the interface method

        // EP.Run is the only true entry-point root (T.M is reached via dispatch, I.M via a call).
        var roots = FactPathFinder.EntryRootsReaching(graph, "Leaf.Do");
        roots.ShouldContain("M:EP.Run");
        roots.ShouldNotContain("M:T.M");
        roots.ShouldNotContain("M:I.M");
    }

    [Fact]
    public void Dispatch_recovers_from_unresolved_interface_edges_by_simple_name()
    {
        // The call resolves to the real interface (T:Ns.IFoo.M), but the implementer's interface
        // edge failed to bind during indexing and was recorded as an error type (!:IFoo) — the
        // pervasive net48 partial-binding case. Dispatch must still reach the impl via simple name.
        var edges = new[] { new CallEdge("M:EP.Run", "M:Ns.IFoo.M", "invocation", "f.cs", 1) };
        var impls = new[] { new ImplementsEdge("T:Impl", "!:IFoo") };
        var methods = new[]
        {
            new MethodRef("M:EP.Run", "Run", "T:EP"),
            new MethodRef("M:Ns.IFoo.M", "M", "T:Ns.IFoo"),
            new MethodRef("M:Impl.M", "M", "T:Impl"),
        };
        var graph = new FactGraphData(edges, impls, methods);

        var reach = FactPathFinder.Reaches(graph, "EP.Run");

        reach.Keys.ShouldContain("M:Impl.M"); // dispatched despite the unresolved (!:) interface edge
    }

    [Fact]
    public void Dispatch_crosses_generic_base_classes_both_directions()
    {
        // Base`1 declares virtual M; Sub : Base{X} overrides M. The base EDGE stores the instantiated
        // Base{X} while the METHODS are on the open Base`1 — exact-DocID lookup would miss. A call to
        // Base`1.M must reach Sub.M (forward), and reverse from Sub.M must climb to EP.Run.
        var edges = new[] { new CallEdge("M:EP.Run", "M:Ns.Base`1.M", "invocation", "f.cs", 1) };
        var bases = new[] { new BaseEdge("T:Ns.Sub", "T:Ns.Base{T:Ns.X}") };
        var methods = new[]
        {
            new MethodRef("M:EP.Run", "Run", "T:EP"),
            new MethodRef("M:Ns.Base`1.M", "M", "T:Ns.Base`1"),
            new MethodRef("M:Ns.Sub.M", "M", "T:Ns.Sub", IsOverride: true),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);

        FactPathFinder.Reaches(graph, "EP.Run").Keys.ShouldContain("M:Ns.Sub.M"); // forward base->override
        FactPathFinder.EntryRootsReaching(graph, "Sub.M").ShouldContain("M:EP.Run"); // reverse climb
    }

    [Fact]
    public void Reaches_tags_base_override_dispatch_fanout_and_propagates_through_the_subtree()
    {
        // SiteEntity.Save calls base.Save() -> EntityBase.Save (a REAL call edge). EntityBase.Save is
        // virtual with TWO overrides (Site, Company), so base->override dispatch fans EntityBase.Save
        // out to BOTH — but that reach is dispatch fan-out, not a real call. CompanyEntity.Save (and
        // anything it transitively calls) must be tagged as reached VIA the EntityBase.Save fan-out;
        // EntityBase.Save itself, reached by the real base.Save() call, must NOT be tagged. This is
        // the A1 over-count: without the tag, every *Entity.Save and its effects read as real reach.
        var edges = new[]
        {
            new CallEdge("M:N.SiteEntity.Save", "M:N.EntityBase.Save", "invocation", "f.cs", 10),
            new CallEdge("M:N.CompanyEntity.Save", "M:N.CompanyHelper.Touch", "invocation", "f.cs", 20),
        };
        var bases = new[] { new BaseEdge("T:N.SiteEntity", "T:N.EntityBase"), new BaseEdge("T:N.CompanyEntity", "T:N.EntityBase") };
        var methods = new[]
        {
            new MethodRef("M:N.EntityBase.Save", "Save", "T:N.EntityBase"),
            new MethodRef("M:N.SiteEntity.Save", "Save", "T:N.SiteEntity", IsOverride: true),
            new MethodRef("M:N.CompanyEntity.Save", "Save", "T:N.CompanyEntity", IsOverride: true),
            new MethodRef("M:N.CompanyHelper.Touch", "Touch", "T:N.CompanyHelper"),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);

        var info = FactPathFinder.ReachesWithFanout(graph, "M:N.SiteEntity.Save");

        info["M:N.EntityBase.Save"].DispatchVia.ShouldBeNull(); // reached by the real base.Save() call
        info["M:N.CompanyEntity.Save"].DispatchVia.ShouldBe("M:N.EntityBase.Save");
        info["M:N.CompanyEntity.Save"].DispatchDegree.ShouldBe(2);
        info["M:N.CompanyHelper.Touch"].DispatchVia.ShouldBe("M:N.EntityBase.Save"); // propagated into the subtree
        info["M:N.CompanyHelper.Touch"].DispatchDegree.ShouldBe(2);
    }

    [Fact]
    public void Single_target_dispatch_is_not_treated_as_fanout()
    {
        // One base, ONE override. Calling the base virtual dispatches to exactly one override —
        // deterministic, not ambiguous fan-out — so it must NOT be tagged (degree 1, DispatchVia null).
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.Base.M", "invocation", "f.cs", 1) };
        var bases = new[] { new BaseEdge("T:N.Sub", "T:N.Base") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Base.M", "M", "T:N.Base"),
            new MethodRef("M:N.Sub.M", "M", "T:N.Sub", IsOverride: true),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);

        var info = FactPathFinder.ReachesWithFanout(graph, "M:N.Caller.Go");

        info.ContainsKey("M:N.Sub.M").ShouldBeTrue();
        info["M:N.Sub.M"].DispatchVia.ShouldBeNull();
        info["M:N.Sub.M"].DispatchDegree.ShouldBe(0);
    }

    [Fact]
    public void BuildTree_carries_dispatch_fanout_degree_on_the_reaching_edge()
    {
        var edges = new[] { new CallEdge("M:N.SiteEntity.Save", "M:N.EntityBase.Save", "invocation", "f.cs", 10) };
        var bases = new[] { new BaseEdge("T:N.SiteEntity", "T:N.EntityBase"), new BaseEdge("T:N.CompanyEntity", "T:N.EntityBase") };
        var methods = new[]
        {
            new MethodRef("M:N.EntityBase.Save", "Save", "T:N.EntityBase"),
            new MethodRef("M:N.SiteEntity.Save", "Save", "T:N.SiteEntity", IsOverride: true),
            new MethodRef("M:N.CompanyEntity.Save", "Save", "T:N.CompanyEntity", IsOverride: true),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);

        var root = FactPathFinder.BuildTree(graph, "M:N.SiteEntity.Save").Single();
        var entityBase = root.Children.Single(c => c.SymbolId == "M:N.EntityBase.Save");
        var company = entityBase.Children.Single(c => c.SymbolId == "M:N.CompanyEntity.Save");

        company.EdgeKind.ShouldBe("override-dispatch");
        company.Fanout.ShouldBe(2);
        entityBase.Fanout.ShouldBe(0); // reached by a real call, not a dispatch fan-out
    }

    // --- Receiver-type dispatch narrowing (the signal/noise fix) ---

    // A base virtual Save with TWO overrides. A call site whose receiver is statically CompanyEntity
    // must dispatch ONLY to CompanyEntity.Save — not to SiteEntity.Save (the CHA over-approximation).
    [Fact]
    public void Concrete_receiver_narrows_override_dispatch_to_that_receivers_override()
    {
        var edges = new[]
        {
            // caller.Go() does company.Save() — receiver is CompanyEntity.
            new CallEdge("M:N.Caller.Go", "M:N.EntityBase.Save", "invocation", "f.cs", 10, ReceiverType: "N.CompanyEntity"),
        };
        var bases = new[] { new BaseEdge("T:N.SiteEntity", "T:N.EntityBase"), new BaseEdge("T:N.CompanyEntity", "T:N.EntityBase") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.EntityBase.Save", "Save", "T:N.EntityBase"),
            new MethodRef("M:N.SiteEntity.Save", "Save", "T:N.SiteEntity", IsOverride: true),
            new MethodRef("M:N.CompanyEntity.Save", "Save", "T:N.CompanyEntity", IsOverride: true),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.EntityBase.Save"); // the real call
        reach.Keys.ShouldContain("M:N.CompanyEntity.Save"); // the receiver's override
        reach.Keys.ShouldNotContain("M:N.SiteEntity.Save"); // narrowed away — wrong receiver type
    }

    // A descendant receiver also pulls in the receiver's OWN subtype overrides (the CLR could dispatch
    // to a subtype's override), but still excludes siblings outside the receiver's subtree.
    [Fact]
    public void Concrete_receiver_includes_its_subtypes_but_not_siblings()
    {
        var edges = new[]
        {
            new CallEdge("M:N.Caller.Go", "M:N.EntityBase.Save", "invocation", "f.cs", 10, ReceiverType: "N.CompanyEntity"),
        };
        var bases = new[]
        {
            new BaseEdge("T:N.SiteEntity", "T:N.EntityBase"),
            new BaseEdge("T:N.CompanyEntity", "T:N.EntityBase"),
            new BaseEdge("T:N.SubCompanyEntity", "T:N.CompanyEntity"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.EntityBase.Save", "Save", "T:N.EntityBase"),
            new MethodRef("M:N.SiteEntity.Save", "Save", "T:N.SiteEntity", IsOverride: true),
            new MethodRef("M:N.CompanyEntity.Save", "Save", "T:N.CompanyEntity", IsOverride: true),
            new MethodRef("M:N.SubCompanyEntity.Save", "Save", "T:N.SubCompanyEntity", IsOverride: true),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.CompanyEntity.Save");
        reach.Keys.ShouldContain("M:N.SubCompanyEntity.Save"); // a subtype of the receiver
        reach.Keys.ShouldNotContain("M:N.SiteEntity.Save"); // sibling — excluded
    }

    // Base-typed receiver (the declaring base itself) can dispatch to ANY override at runtime, so it
    // must fall back to full CHA — no narrowing, both overrides reached.
    [Fact]
    public void Base_typed_receiver_falls_back_to_full_cha()
    {
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.EntityBase.Save", "invocation", "f.cs", 10, ReceiverType: "N.EntityBase") };
        var bases = new[] { new BaseEdge("T:N.SiteEntity", "T:N.EntityBase"), new BaseEdge("T:N.CompanyEntity", "T:N.EntityBase") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.EntityBase.Save", "Save", "T:N.EntityBase"),
            new MethodRef("M:N.SiteEntity.Save", "Save", "T:N.SiteEntity", IsOverride: true),
            new MethodRef("M:N.CompanyEntity.Save", "Save", "T:N.CompanyEntity", IsOverride: true),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.SiteEntity.Save"); // CHA fallback — both overrides reached
        reach.Keys.ShouldContain("M:N.CompanyEntity.Save");
    }

    // Null receiver (a bare/static-shaped call edge) also falls back to CHA.
    [Fact]
    public void Null_receiver_falls_back_to_full_cha()
    {
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.EntityBase.Save", "invocation", "f.cs", 10) };
        var bases = new[] { new BaseEdge("T:N.SiteEntity", "T:N.EntityBase"), new BaseEdge("T:N.CompanyEntity", "T:N.EntityBase") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.EntityBase.Save", "Save", "T:N.EntityBase"),
            new MethodRef("M:N.SiteEntity.Save", "Save", "T:N.SiteEntity", IsOverride: true),
            new MethodRef("M:N.CompanyEntity.Save", "Save", "T:N.CompanyEntity", IsOverride: true),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.SiteEntity.Save");
        reach.Keys.ShouldContain("M:N.CompanyEntity.Save");
    }

    // An unknown receiver type (not a first-party method-bearing type in the index) is untrustworthy
    // and falls back to CHA rather than dropping a real override.
    [Fact]
    public void Unknown_receiver_type_falls_back_to_full_cha()
    {
        var edges = new[]
        {
            new CallEdge("M:N.Caller.Go", "M:N.EntityBase.Save", "invocation", "f.cs", 10, ReceiverType: "Some.Unindexed.Type"),
        };
        var bases = new[] { new BaseEdge("T:N.SiteEntity", "T:N.EntityBase"), new BaseEdge("T:N.CompanyEntity", "T:N.EntityBase") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.EntityBase.Save", "Save", "T:N.EntityBase"),
            new MethodRef("M:N.SiteEntity.Save", "Save", "T:N.SiteEntity", IsOverride: true),
            new MethodRef("M:N.CompanyEntity.Save", "Save", "T:N.CompanyEntity", IsOverride: true),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.SiteEntity.Save");
        reach.Keys.ShouldContain("M:N.CompanyEntity.Save");
    }

    // Interface impl-dispatch narrows to a concrete receiver too: i.Do() where the receiver is
    // statically FooImpl reaches FooImpl.Do, not BarImpl.Do.
    [Fact]
    public void Concrete_receiver_narrows_interface_dispatch()
    {
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.IService.Do", "invocation", "f.cs", 10, ReceiverType: "N.FooImpl") };
        var impls = new[] { new ImplementsEdge("T:N.FooImpl", "T:N.IService"), new ImplementsEdge("T:N.BarImpl", "T:N.IService") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.IService.Do", "Do", "T:N.IService"),
            new MethodRef("M:N.FooImpl.Do", "Do", "T:N.FooImpl"),
            new MethodRef("M:N.BarImpl.Do", "Do", "T:N.BarImpl"),
        };
        var graph = new FactGraphData(edges, impls, methods);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.FooImpl.Do"); // the receiver's impl
        reach.Keys.ShouldNotContain("M:N.BarImpl.Do"); // narrowed away
    }

    // Interface-typed receiver (the interface itself, not a concrete impl) falls back to CHA — any impl
    // could be the runtime target.
    [Fact]
    public void Interface_typed_receiver_falls_back_to_full_cha()
    {
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.IService.Do", "invocation", "f.cs", 10, ReceiverType: "N.IService") };
        var impls = new[] { new ImplementsEdge("T:N.FooImpl", "T:N.IService"), new ImplementsEdge("T:N.BarImpl", "T:N.IService") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.IService.Do", "Do", "T:N.IService"),
            new MethodRef("M:N.FooImpl.Do", "Do", "T:N.FooImpl"),
            new MethodRef("M:N.BarImpl.Do", "Do", "T:N.BarImpl"),
        };
        var graph = new FactGraphData(edges, impls, methods);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.FooImpl.Do"); // CHA fallback — both impls reached
        reach.Keys.ShouldContain("M:N.BarImpl.Do");
    }

    // Reverse narrows symmetrically at the DISPATCH hop: when every call site of the base method has a
    // concrete receiver that excludes a given override, that base method does NOT reverse-reach the
    // override. Here EntityBase.Save is only ever called with a CompanyEntity receiver, so the reverse
    // closure of SiteEntity.Save must NOT pick up EntityBase.Save (nor its caller) — the CHA fan-out
    // that would otherwise make every *Caller reach every *Entity.Save.
    [Fact]
    public void Reverse_dispatch_narrows_by_receiver_at_the_dispatch_hop()
    {
        var edges = new[]
        {
            new CallEdge("M:N.CompanyCaller.Go", "M:N.EntityBase.Save", "invocation", "f.cs", 10, ReceiverType: "N.CompanyEntity"),
        };
        var bases = new[] { new BaseEdge("T:N.SiteEntity", "T:N.EntityBase"), new BaseEdge("T:N.CompanyEntity", "T:N.EntityBase") };
        var methods = new[]
        {
            new MethodRef("M:N.CompanyCaller.Go", "Go", "T:N.CompanyCaller"),
            new MethodRef("M:N.EntityBase.Save", "Save", "T:N.EntityBase"),
            new MethodRef("M:N.SiteEntity.Save", "Save", "T:N.SiteEntity", IsOverride: true),
            new MethodRef("M:N.CompanyEntity.Save", "Save", "T:N.CompanyEntity", IsOverride: true),
        };
        var graph = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);

        // Forward from the company caller reaches CompanyEntity.Save but NOT SiteEntity.Save.
        var fwd = FactPathFinder.Reaches(graph, "M:N.CompanyCaller.Go");
        fwd.Keys.ShouldContain("M:N.CompanyEntity.Save");
        fwd.Keys.ShouldNotContain("M:N.SiteEntity.Save");

        // Reverse from CompanyEntity.Save climbs through the base seam to the caller.
        var reachedByCompany = FactPathFinder.ReachedBy(graph, "M:N.CompanyEntity.Save");
        reachedByCompany.Keys.ShouldContain("M:N.EntityBase.Save");
        reachedByCompany.Keys.ShouldContain("M:N.CompanyCaller.Go");

        // Reverse from SiteEntity.Save must NOT reach the base seam — no call site dispatches to Site.
        var reachedBySite = FactPathFinder.ReachedBy(graph, "M:N.SiteEntity.Save");
        reachedBySite.Keys.ShouldNotContain("M:N.EntityBase.Save");
        reachedBySite.Keys.ShouldNotContain("M:N.CompanyCaller.Go");
    }

    [Fact]
    public void Find_annotates_each_hop_with_its_call_site_loop()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "x in xs"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20)
        );

        var path = FactPathFinder.Find(graph, "M:A", "M:C");

        path.ShouldNotBeNull();
        path!.Count.ShouldBe(3);
        path[0].LoopKind.ShouldBeNull(); // entry node
        path[1].LoopKind.ShouldBe("foreach"); // A->B call site is in a loop
        path[1].LoopDetail.ShouldBe("x in xs");
        path[2].LoopKind.ShouldBeNull(); // B->C call site is not
    }
}

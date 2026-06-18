using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class FactPathFinderFanoutTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    [Test]
    public void Reaches_counts_looped_edges_and_inherits_through_unlooped_edges()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "x in xs"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20),
            new CallEdge("M:A", "M:D", "invocation", "f.cs", 30)
        );

        var info = FactPathFinder.ReachesWithFanout(graph, "M:A");

        info["M:B"].LoopNesting.ShouldBe(1);
        info["M:B"].NearestLoopDetail.ShouldBe("x in xs");
        info["M:C"].LoopNesting.ShouldBe(1);
        info["M:C"].NearestLoopDetail.ShouldBe("x in xs");
        info["M:D"].LoopNesting.ShouldBe(0);
    }

    [Test]
    public void Nested_loops_on_the_path_accumulate_and_report_the_innermost()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "a in aa"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20, "for", "i")
        );

        var info = FactPathFinder.ReachesWithFanout(graph, "M:A");

        info["M:C"].LoopNesting.ShouldBe(2);
        info["M:C"].NearestLoopKind.ShouldBe("for");
    }

    [Test]
    public void BuildTree_materializes_the_call_tree_with_loops_and_breaks_cycles()
    {
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
        b.LoopKind.ShouldBe("foreach");
        b.LoopDetail.ShouldBe("x in xs");

        var cNode = b.Children.Single(c => c.SymbolId == "M:C");
        var backToA = cNode.Children.Single(c => c.SymbolId == "M:A");
        backToA.Truncated.ShouldBeTrue();
        backToA.Children.ShouldBeEmpty();
    }

    [Test]
    public void BuildTree_expands_each_method_once()
    {
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
        underB.Children.ShouldContain(c => c.SymbolId == "M:E");
        underC.Truncated.ShouldBeTrue();
        underC.Children.ShouldBeEmpty();
    }

    [Test]
    public void ReachedBy_finds_transitive_callers_including_interface_dispatch()
    {
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

        reached.Keys.ShouldContain("M:T.M");
        reached.Keys.ShouldContain("M:I.M");
        reached.Keys.ShouldContain("M:EP.Run");

        var roots = FactPathFinder.EntryRootsReaching(graph, "Leaf.Do");
        roots.ShouldContain("M:EP.Run");
        roots.ShouldNotContain("M:T.M");
        roots.ShouldNotContain("M:I.M");
    }

    [Test]
    public void Dispatch_recovers_from_unresolved_interface_edges_by_simple_name()
    {
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

        reach.Keys.ShouldContain("M:Impl.M");
    }

    [Test]
    public void Dispatch_crosses_generic_base_classes_both_directions()
    {
        var edges = new[] { new CallEdge("M:EP.Run", "M:Ns.Base`1.M", "invocation", "f.cs", 1) };
        var bases = new[] { new BaseEdge("T:Ns.Sub", "T:Ns.Base{T:Ns.X}") };
        var methods = new[]
        {
            new MethodRef("M:EP.Run", "Run", "T:EP"),
            new MethodRef("M:Ns.Base`1.M", "M", "T:Ns.Base`1"),
            new MethodRef("M:Ns.Sub.M", "M", "T:Ns.Sub", IsOverride: true),
        };
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        FactPathFinder.Reaches(graph, "EP.Run").Keys.ShouldContain("M:Ns.Sub.M");
        FactPathFinder.EntryRootsReaching(graph, "Sub.M").ShouldContain("M:EP.Run");
    }

    [Test]
    public void Reaches_tags_base_override_dispatch_fanout_and_propagates_through_the_subtree()
    {
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
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        var info = FactPathFinder.ReachesWithFanout(graph, "M:N.SiteEntity.Save");

        info["M:N.EntityBase.Save"].DispatchVia.ShouldBeNull();
        info["M:N.CompanyEntity.Save"].DispatchVia.ShouldBe("M:N.EntityBase.Save");
        info["M:N.CompanyEntity.Save"].DispatchDegree.ShouldBe(2);
        info["M:N.CompanyHelper.Touch"].DispatchVia.ShouldBe("M:N.EntityBase.Save");
        info["M:N.CompanyHelper.Touch"].DispatchDegree.ShouldBe(2);
    }

    [Test]
    public void Single_target_dispatch_is_not_treated_as_fanout()
    {
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.Base.M", "invocation", "f.cs", 1) };
        var bases = new[] { new BaseEdge("T:N.Sub", "T:N.Base") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Base.M", "M", "T:N.Base"),
            new MethodRef("M:N.Sub.M", "M", "T:N.Sub", IsOverride: true),
        };
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        var info = FactPathFinder.ReachesWithFanout(graph, "M:N.Caller.Go");

        info.ContainsKey("M:N.Sub.M").ShouldBeTrue();
        info["M:N.Sub.M"].DispatchVia.ShouldBeNull();
        info["M:N.Sub.M"].DispatchDegree.ShouldBe(0);
    }

    [Test]
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
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        var root = FactPathFinder.BuildTree(graph, "M:N.SiteEntity.Save").Single();
        var entityBase = root.Children.Single(c => c.SymbolId == "M:N.EntityBase.Save");
        var company = entityBase.Children.Single(c => c.SymbolId == "M:N.CompanyEntity.Save");

        company.EdgeKind.ShouldBe("override-dispatch");
        company.Fanout.ShouldBe(2);
        entityBase.Fanout.ShouldBe(0);
    }

    [Test]
    public void Concrete_receiver_narrows_override_dispatch_to_that_receivers_override()
    {
        var edges = new[]
        {
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
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.EntityBase.Save");
        reach.Keys.ShouldContain("M:N.CompanyEntity.Save");
        reach.Keys.ShouldNotContain("M:N.SiteEntity.Save");
    }

    [Test]
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
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.CompanyEntity.Save");
        reach.Keys.ShouldContain("M:N.SubCompanyEntity.Save");
        reach.Keys.ShouldNotContain("M:N.SiteEntity.Save");
    }

    [Test]
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
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.SiteEntity.Save");
        reach.Keys.ShouldContain("M:N.CompanyEntity.Save");
    }

    [Test]
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
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.SiteEntity.Save");
        reach.Keys.ShouldContain("M:N.CompanyEntity.Save");
    }

    [Test]
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
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.SiteEntity.Save");
        reach.Keys.ShouldContain("M:N.CompanyEntity.Save");
    }

    [Test]
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

        reach.Keys.ShouldContain("M:N.FooImpl.Do");
        reach.Keys.ShouldNotContain("M:N.BarImpl.Do");
    }

    [Test]
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

        reach.Keys.ShouldContain("M:N.FooImpl.Do");
        reach.Keys.ShouldContain("M:N.BarImpl.Do");
    }

    [Test]
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
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        var fwd = FactPathFinder.Reaches(graph, "M:N.CompanyCaller.Go");
        fwd.Keys.ShouldContain("M:N.CompanyEntity.Save");
        fwd.Keys.ShouldNotContain("M:N.SiteEntity.Save");

        var reachedByCompany = FactPathFinder.ReachedBy(graph, "M:N.CompanyEntity.Save");
        reachedByCompany.Keys.ShouldContain("M:N.EntityBase.Save");
        reachedByCompany.Keys.ShouldContain("M:N.CompanyCaller.Go");

        var reachedBySite = FactPathFinder.ReachedBy(graph, "M:N.SiteEntity.Save");
        reachedBySite.Keys.ShouldNotContain("M:N.EntityBase.Save");
        reachedBySite.Keys.ShouldNotContain("M:N.CompanyCaller.Go");
    }

    [Test]
    public void Find_annotates_each_hop_with_its_call_site_loop()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "x in xs"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20)
        );

        var path = FactPathFinder.Find(graph, "M:A", "M:C");

        path.ShouldNotBeNull();
        path!.Count.ShouldBe(3);
        path[0].LoopKind.ShouldBeNull();
        path[1].LoopKind.ShouldBe("foreach");
        path[1].LoopDetail.ShouldBe("x in xs");
        path[2].LoopKind.ShouldBeNull();
    }
}

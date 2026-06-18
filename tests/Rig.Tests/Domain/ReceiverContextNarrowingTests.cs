using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class ReceiverContextNarrowingTests
{
    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var bases = new[]
        {
            new BaseEdge("T:N.Controller", "T:N.WCB"),
            new BaseEdge("T:N.Lab", "T:N.WCB"),
            new BaseEdge("T:N.Pacs", "T:N.WCB"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.Master.Build", "Build", "T:N.Master"),
            new MethodRef("M:N.Controller.Init", "Init", "T:N.Controller"),
            new MethodRef("M:N.WCB.Init", "Init", "T:N.WCB"),
            new MethodRef("M:N.WCB.OnInit", "OnInit", "T:N.WCB"),
            new MethodRef("M:N.Controller.OnInit", "OnInit", "T:N.Controller", IsOverride: true),
            new MethodRef("M:N.Lab.OnInit", "OnInit", "T:N.Lab", IsOverride: true),
            new MethodRef("M:N.Pacs.OnInit", "OnInit", "T:N.Pacs", IsOverride: true),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);
    }

    private static HashSet<string> Ids(TraceNode node)
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
        Walk(node);
        return set;
    }

    [Test]
    public void Concrete_this_carries_across_base_and_this_self_calls_narrowing_the_inner_virtual()
    {
        var graph = Graph(
            new CallEdge("M:N.Master.Build", "M:N.Controller.Init", "invocation", "f.cs", 10, ReceiverType: "N.Controller"),
            new CallEdge("M:N.Controller.Init", "M:N.WCB.Init", "invocation", "f.cs", 20, ReceiverType: "N.WCB"),
            new CallEdge("M:N.WCB.Init", "M:N.WCB.OnInit", "invocation", "f.cs", 30, ReceiverType: "N.WCB")
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Master.Build").Single());

        ids.ShouldContain("M:N.Controller.OnInit");
        ids.ShouldNotContain("M:N.Lab.OnInit");
        ids.ShouldNotContain("M:N.Pacs.OnInit");
    }

    [Test]
    public void Bare_implicit_this_self_call_also_carries_the_concrete_receiver()
    {
        var graph = Graph(
            new CallEdge("M:N.Master.Build", "M:N.Controller.Init", "invocation", "f.cs", 10, ReceiverType: "N.Controller"),
            new CallEdge("M:N.Controller.Init", "M:N.WCB.Init", "invocation", "f.cs", 20, ReceiverType: "N.WCB"),
            new CallEdge("M:N.WCB.Init", "M:N.WCB.OnInit", "invocation", "f.cs", 30)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Master.Build").Single());

        ids.ShouldContain("M:N.Controller.OnInit");
        ids.ShouldNotContain("M:N.Lab.OnInit");
        ids.ShouldNotContain("M:N.Pacs.OnInit");
    }

    [Test]
    public void No_concrete_this_falls_back_to_full_cha()
    {
        var graph = Graph(
            new CallEdge("M:N.Master.Build", "M:N.WCB.Init", "invocation", "f.cs", 10, ReceiverType: "N.WCB"),
            new CallEdge("M:N.WCB.Init", "M:N.WCB.OnInit", "invocation", "f.cs", 30, ReceiverType: "N.WCB")
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Master.Build").Single());

        ids.ShouldContain("M:N.Controller.OnInit");
        ids.ShouldContain("M:N.Lab.OnInit");
        ids.ShouldContain("M:N.Pacs.OnInit");
    }

    [Test]
    public void External_call_on_a_sibling_object_is_not_treated_as_self()
    {
        var graph = Graph(
            new CallEdge("M:N.Master.Build", "M:N.Controller.Init", "invocation", "f.cs", 10, ReceiverType: "N.Controller"),
            new CallEdge("M:N.Controller.Init", "M:N.WCB.Init", "invocation", "f.cs", 20, ReceiverType: "N.WCB"),
            new CallEdge("M:N.WCB.Init", "M:N.WCB.OnInit", "invocation", "f.cs", 30, ReceiverType: "N.Lab")
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Master.Build").Single());

        ids.ShouldContain("M:N.Lab.OnInit");
        ids.ShouldNotContain("M:N.Controller.OnInit");
        ids.ShouldNotContain("M:N.Pacs.OnInit");
    }

    [Test]
    public void Dispatch_seeds_the_resolved_concrete_type_so_a_self_call_inside_it_narrows()
    {
        var bases = new[]
        {
            new BaseEdge("T:N.Controller", "T:N.WCB"),
            new BaseEdge("T:N.Lab", "T:N.WCB"),
            new BaseEdge("T:N.Pacs", "T:N.WCB"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.WCB.Step", "Step", "T:N.WCB"),
            new MethodRef("M:N.WCB.Deep", "Deep", "T:N.WCB"),
            new MethodRef("M:N.Controller.Step", "Step", "T:N.Controller", IsOverride: true),
            new MethodRef("M:N.Lab.Step", "Step", "T:N.Lab", IsOverride: true),
            new MethodRef("M:N.Pacs.Step", "Step", "T:N.Pacs", IsOverride: true),
            new MethodRef("M:N.Controller.Deep", "Deep", "T:N.Controller", IsOverride: true),
            new MethodRef("M:N.Lab.Deep", "Deep", "T:N.Lab", IsOverride: true),
            new MethodRef("M:N.Pacs.Deep", "Deep", "T:N.Pacs", IsOverride: true),
        };
        var edges = new[]
        {
            new CallEdge("M:N.Caller.Go", "M:N.WCB.Step", "invocation", "f.cs", 10, ReceiverType: "N.WCB"),
            new CallEdge("M:N.Controller.Step", "M:N.WCB.Deep", "invocation", "f.cs", 20, ReceiverType: "N.WCB"),
        };
        var graph = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases);

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Controller.Step");
        ids.ShouldContain("M:N.Lab.Step");
        ids.ShouldContain("M:N.Pacs.Step");
        ids.ShouldContain("M:N.Controller.Deep");
        ids.ShouldNotContain("M:N.Lab.Deep");
        ids.ShouldNotContain("M:N.Pacs.Deep");
    }

    [Test]
    public void Reaches_closure_also_narrows_the_self_called_inner_virtual()
    {
        var graph = Graph(
            new CallEdge("M:N.Master.Build", "M:N.Controller.Init", "invocation", "f.cs", 10, ReceiverType: "N.Controller"),
            new CallEdge("M:N.Controller.Init", "M:N.WCB.Init", "invocation", "f.cs", 20, ReceiverType: "N.WCB"),
            new CallEdge("M:N.WCB.Init", "M:N.WCB.OnInit", "invocation", "f.cs", 30, ReceiverType: "N.WCB")
        );

        var reach = FactPathFinder.Reaches(graph, "M:N.Master.Build");

        reach.Keys.ShouldContain("M:N.Controller.OnInit");
        reach.Keys.ShouldNotContain("M:N.Lab.OnInit");
        reach.Keys.ShouldNotContain("M:N.Pacs.OnInit");
    }
}

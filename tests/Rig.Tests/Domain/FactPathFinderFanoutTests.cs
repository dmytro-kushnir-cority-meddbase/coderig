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
        var nodes = edges
            .SelectMany(e => new[] { e.Caller, e.Callee })
            .Distinct()
            .Select(M)
            .ToArray();
        return new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), nodes);
    }

    [Fact]
    public void Reaches_counts_looped_edges_and_inherits_through_unlooped_edges()
    {
        // M:A -(foreach)-> M:B -> M:C , plus an unlooped sibling M:A -> M:D
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "x in xs"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20),
            new CallEdge("M:A", "M:D", "invocation", "f.cs", 30));

        var info = FactPathFinder.ReachesWithFanout(graph, "M:A");

        info["M:B"].LoopNesting.ShouldBe(1);
        info["M:B"].NearestLoopDetail.ShouldBe("x in xs");
        info["M:C"].LoopNesting.ShouldBe(1);          // inherited through the unlooped B->C edge
        info["M:C"].NearestLoopDetail.ShouldBe("x in xs");
        info["M:D"].LoopNesting.ShouldBe(0);          // sibling reached without crossing a loop
    }

    [Fact]
    public void Nested_loops_on_the_path_accumulate_and_report_the_innermost()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "a in aa"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20, "for", "i"));

        var info = FactPathFinder.ReachesWithFanout(graph, "M:A");

        info["M:C"].LoopNesting.ShouldBe(2);
        info["M:C"].NearestLoopKind.ShouldBe("for");  // innermost loop wrapping the call chain
    }

    [Fact]
    public void BuildTree_materializes_the_call_tree_with_loops_and_breaks_cycles()
    {
        // M:A -(foreach)-> M:B -> M:C , and a cycle M:C -> M:A
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "x in xs"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20),
            new CallEdge("M:C", "M:A", "invocation", "f.cs", 30));

        var roots = FactPathFinder.BuildTree(graph, "M:A");

        roots.Count.ShouldBe(1);
        var a = roots[0];
        a.SymbolId.ShouldBe("M:A");
        a.EdgeKind.ShouldBe("entry");

        var b = a.Children.Single(c => c.SymbolId == "M:B");
        b.LoopKind.ShouldBe("foreach");          // A->B call site is in a loop
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
            new CallEdge("M:D", "M:E", "invocation", "f.cs", 40));

        var a = FactPathFinder.BuildTree(graph, "M:A").Single();
        var underB = a.Children.Single(c => c.SymbolId == "M:B").Children.Single(c => c.SymbolId == "M:D");
        var underC = a.Children.Single(c => c.SymbolId == "M:C").Children.Single(c => c.SymbolId == "M:D");

        underB.Truncated.ShouldBeFalse();
        underB.Children.ShouldContain(c => c.SymbolId == "M:E");   // expanded once, here
        underC.Truncated.ShouldBeTrue();                           // second encounter = seen leaf
        underC.Children.ShouldBeEmpty();
    }

    [Fact]
    public void Find_annotates_each_hop_with_its_call_site_loop()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:B", "invocation", "f.cs", 10, "foreach", "x in xs"),
            new CallEdge("M:B", "M:C", "invocation", "f.cs", 20));

        var path = FactPathFinder.Find(graph, "M:A", "M:C");

        path.ShouldNotBeNull();
        path!.Count.ShouldBe(3);
        path[0].LoopKind.ShouldBeNull();              // entry node
        path[1].LoopKind.ShouldBe("foreach");          // A->B call site is in a loop
        path[1].LoopDetail.ShouldBe("x in xs");
        path[2].LoopKind.ShouldBeNull();               // B->C call site is not
    }
}

using System.Linq;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class SiblingDedupTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), nodes);
    }

    [Test]
    public void Identical_sibling_edges_collapse_to_one_child_with_callsite_count()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Target", "invocation", "f.cs", 10),
            new CallEdge("M:A", "M:Target", "invocation", "f.cs", 11),
            new CallEdge("M:A", "M:Target", "invocation", "f.cs", 12),
            new CallEdge("M:A", "M:Target", "invocation", "f.cs", 13)
        );

        var a = FactPathFinder.BuildTree(graph, "M:A").Single();

        var kids = a.Children.Where(c => c.SymbolId == "M:Target").ToList();
        kids.Count.ShouldBe(1);
        kids[0].CallSites.ShouldBe(4);
        kids[0].Truncated.ShouldBeFalse();
    }

    [Test]
    public void Sibling_edges_in_different_loop_context_do_not_collapse()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Target", "invocation", "f.cs", 10),
            new CallEdge("M:A", "M:Target", "invocation", "f.cs", 20, "foreach", "x in xs")
        );

        var a = FactPathFinder.BuildTree(graph, "M:A").Single();

        var kids = a.Children.Where(c => c.SymbolId == "M:Target").ToList();
        kids.Count.ShouldBe(2);
        kids.ShouldContain(c => c.LoopKind == "foreach" && c.CallSites == 1);
        kids.ShouldContain(c => c.LoopKind == null && c.CallSites == 1);
    }

    [Test]
    public void Single_call_keeps_callsite_count_of_one()
    {
        var graph = Graph(new CallEdge("M:A", "M:Target", "invocation", "f.cs", 10));

        var a = FactPathFinder.BuildTree(graph, "M:A").Single();

        a.Children.Single(c => c.SymbolId == "M:Target").CallSites.ShouldBe(1);
    }
}

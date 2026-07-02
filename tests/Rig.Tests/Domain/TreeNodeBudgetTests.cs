using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// BuildTree's maxNodes budget — the engine behind `tree --limit <n>`. Each popped node consumes one
// unit; the node that exhausts the budget is emitted as a Truncated leaf with TruncationCause
// .BudgetCapped and its subtree is not walked. Cause precedence (AlreadyExpanded > DepthCapped >
// BudgetCapped) is pinned by the mixed-cause test.
public sealed class TreeNodeBudgetTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    private static CallEdge Edge(string caller, string callee) => new(caller, callee, "invocation", "f.cs", 1);

    [Test]
    public void The_node_hitting_the_budget_is_a_budget_capped_leaf_and_deeper_nodes_are_never_built()
    {
        // Chain A -> B -> C with budget 2: A expands, B pops on a spent budget -> capped leaf; C absent.
        var graph = Graph(Edge("M:A", "M:B"), Edge("M:B", "M:C"));

        var a = FactPathFinder.BuildTree(graph, "M:A", maxNodes: 2).Single();

        a.Truncated.ShouldBeFalse();
        var b = a.Children.ShouldHaveSingleItem();
        b.SymbolId.ShouldBe("M:B");
        b.Truncated.ShouldBeTrue();
        b.TruncationCause.ShouldBe(TruncationCause.BudgetCapped);
        b.Children.ShouldBeEmpty();
    }

    [Test]
    public void A_budget_of_one_caps_the_root_itself()
    {
        var graph = Graph(Edge("M:A", "M:B"));

        var a = FactPathFinder.BuildTree(graph, "M:A", maxNodes: 1).Single();

        a.Truncated.ShouldBeTrue();
        a.TruncationCause.ShouldBe(TruncationCause.BudgetCapped);
        a.Children.ShouldBeEmpty();
    }

    [Test]
    public void A_sufficient_budget_leaves_no_budget_capped_nodes()
    {
        // Fencepost, deliberate: the node consuming the FINAL budget unit is conservatively marked
        // capped before it can expand (the check runs after the decrement), so budget N fully expands
        // N-1 nodes — a 3-node chain needs 4. The conservative mark is the safe direction: it can
        // claim "walk stopped here" on a true leaf, never silently hide an unwalked subtree.
        var graph = Graph(Edge("M:A", "M:B"), Edge("M:B", "M:C"));

        var a = FactPathFinder.BuildTree(graph, "M:A", maxNodes: 4).Single();

        Flatten(a).ShouldAllBe(n => n.TruncationCause != TruncationCause.BudgetCapped);
        Flatten(a).Select(n => n.SymbolId).ShouldBe(["M:A", "M:B", "M:C"]);

        // And at exactly node-count budget, the last node carries the conservative capped mark.
        var capped = FactPathFinder.BuildTree(graph, "M:A", maxNodes: 3).Single();
        Flatten(capped).Single(n => n.SymbolId == "M:C").TruncationCause.ShouldBe(TruncationCause.BudgetCapped);
    }

    [Test]
    public void A_cycle_reencounter_is_already_expanded_not_budget_capped()
    {
        // A -> B -> A with room to spare: the re-encountered A must keep the AlreadyExpanded cause
        // (the redundancy signal) — budget capping must not steal it.
        var graph = Graph(Edge("M:A", "M:B"), Edge("M:B", "M:A"));

        var a = FactPathFinder.BuildTree(graph, "M:A", maxNodes: 10).Single();

        var reencountered = a.Children.Single().Children.Single();
        reencountered.SymbolId.ShouldBe("M:A");
        reencountered.TruncationCause.ShouldBe(TruncationCause.AlreadyExpanded);
    }

    private static IEnumerable<TraceNode> Flatten(TraceNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(Flatten))
        {
            yield return child;
        }
    }
}

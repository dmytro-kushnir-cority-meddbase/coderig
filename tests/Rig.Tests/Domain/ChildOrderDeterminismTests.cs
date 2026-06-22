using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Guards that BuildTree child-ordering is total-order-stable (store-independent) for same-line edges.
// The sort key in BuildIndex is: line (primary) -> callee SymbolId (ordinal) -> Kind (ordinal) ->
// ReceiverType (ordinal).  A re-index reshuffles SQLite rowids; these tests prove the child sequence
// is identical regardless of the order edges arrive from the DB.
public sealed class ChildOrderDeterminismTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    // Two callees on the SAME source line — callee SymbolId ordinal must break the tie.
    // Build with edges in forward order, then reversed, and assert the tree children
    // come out in SymbolId-ordinal order (M:Z before M:Z is impossible; M:A before M:B) both times.
    [Test]
    public void Same_line_children_are_ordered_by_callee_symbol_id_regardless_of_input_order()
    {
        var edgesForward = new[]
        {
            new CallEdge("M:Root", "M:Callee.Z", "invocation", "f.cs", 10),
            new CallEdge("M:Root", "M:Callee.A", "invocation", "f.cs", 10),
        };
        var edgesReversed = new[]
        {
            new CallEdge("M:Root", "M:Callee.A", "invocation", "f.cs", 10),
            new CallEdge("M:Root", "M:Callee.Z", "invocation", "f.cs", 10),
        };

        var rootForward = FactPathFinder.BuildTree(Graph(edgesForward), "M:Root").Single();
        var rootReversed = FactPathFinder.BuildTree(Graph(edgesReversed), "M:Root").Single();

        var orderForward = rootForward.Children.Select(c => c.SymbolId).ToArray();
        var orderReversed = rootReversed.Children.Select(c => c.SymbolId).ToArray();

        // Both inputs must produce the identical ordinal sequence.
        orderForward.ShouldBe(orderReversed);

        // The sequence itself must be callee SymbolId ordinal: A before Z.
        orderForward[0].ShouldBe("M:Callee.A");
        orderForward[1].ShouldBe("M:Callee.Z");
    }

    // Three callees: two on the same line (same-line tie broken by callee id) and one on a
    // different line (line ordering must still dominate for distinct-line children).
    [Test]
    public void Distinct_line_children_still_sort_by_line_before_callee_id()
    {
        var edgesForward = new[]
        {
            new CallEdge("M:Root", "M:Callee.Z", "invocation", "f.cs", 10),
            new CallEdge("M:Root", "M:Callee.A", "invocation", "f.cs", 10),
            new CallEdge("M:Root", "M:Callee.M", "invocation", "f.cs", 5),
        };
        var edgesShuffled = new[]
        {
            new CallEdge("M:Root", "M:Callee.M", "invocation", "f.cs", 5),
            new CallEdge("M:Root", "M:Callee.A", "invocation", "f.cs", 10),
            new CallEdge("M:Root", "M:Callee.Z", "invocation", "f.cs", 10),
        };

        var rootForward = FactPathFinder.BuildTree(Graph(edgesForward), "M:Root").Single();
        var rootShuffled = FactPathFinder.BuildTree(Graph(edgesShuffled), "M:Root").Single();

        var orderForward = rootForward.Children.Select(c => c.SymbolId).ToArray();
        var orderShuffled = rootShuffled.Children.Select(c => c.SymbolId).ToArray();

        // Input-order-independent.
        orderForward.ShouldBe(orderShuffled);

        // Line-5 child must appear before the line-10 children.
        orderForward[0].ShouldBe("M:Callee.M");

        // Line-10 children are in callee-id ordinal order.
        orderForward[1].ShouldBe("M:Callee.A");
        orderForward[2].ShouldBe("M:Callee.Z");
    }

    // Same callee on the same line with different Kind values — Kind ordinal must break the tie.
    [Test]
    public void Same_line_same_callee_different_kind_is_ordered_by_kind()
    {
        // "invocation" < "methodGroup" (ordinal).
        var edgesForward = new[]
        {
            new CallEdge("M:Root", "M:Target", "methodGroup", "f.cs", 10),
            new CallEdge("M:Root", "M:Target", "invocation", "f.cs", 10),
        };
        var edgesReversed = new[]
        {
            new CallEdge("M:Root", "M:Target", "invocation", "f.cs", 10),
            new CallEdge("M:Root", "M:Target", "methodGroup", "f.cs", 10),
        };

        var rootForward = FactPathFinder.BuildTree(Graph(edgesForward), "M:Root").Single();
        var rootReversed = FactPathFinder.BuildTree(Graph(edgesReversed), "M:Root").Single();

        var orderForward = rootForward.Children.Select(c => c.EdgeKind).ToArray();
        var orderReversed = rootReversed.Children.Select(c => c.EdgeKind).ToArray();

        // Both orderings must produce the identical edge-kind sequence.
        orderForward.ShouldBe(orderReversed);

        // "invocation" < "methodGroup" ordinal.
        orderForward[0].ShouldBe("invocation");
        orderForward[1].ShouldBe("methodGroup");
    }

    // Three same-line callees where two share the same SymbolId but differ in Kind — verifies the
    // third sort key (Kind) breaks the tie before ReceiverType is even needed, and that the
    // overall child sequence is stable regardless of input order.
    // Uses "invocation" < "methodGroup" (ordinal) and a distinct third callee to anchor the test.
    [Test]
    public void Same_line_same_callee_different_kind_plus_distinct_callee_is_stable()
    {
        // edgesForward: kind-tie comes first, distinct callee last
        var edgesForward = new[]
        {
            new CallEdge("M:Root", "M:Callee.B", "methodGroup", "f.cs", 10),
            new CallEdge("M:Root", "M:Callee.A", "invocation", "f.cs", 10),
            new CallEdge("M:Root", "M:Callee.B", "invocation", "f.cs", 10),
        };
        // edgesShuffled: same three edges in a different insertion order
        var edgesShuffled = new[]
        {
            new CallEdge("M:Root", "M:Callee.B", "invocation", "f.cs", 10),
            new CallEdge("M:Root", "M:Callee.A", "invocation", "f.cs", 10),
            new CallEdge("M:Root", "M:Callee.B", "methodGroup", "f.cs", 10),
        };

        var rootForward = FactPathFinder.BuildTree(Graph(edgesForward), "M:Root").Single();
        var rootShuffled = FactPathFinder.BuildTree(Graph(edgesShuffled), "M:Root").Single();

        // (SymbolId, EdgeKind) pairs capture the full identity of each child.
        var orderForward = rootForward.Children.Select(c => (c.SymbolId, c.EdgeKind)).ToArray();
        var orderShuffled = rootShuffled.Children.Select(c => (c.SymbolId, c.EdgeKind)).ToArray();

        // Input-order-independent: both must yield the same sequence.
        orderForward.ShouldBe(orderShuffled);

        // Callee.A (invocation) < Callee.B (invocation) < Callee.B (methodGroup) — total order.
        orderForward[0].ShouldBe(("M:Callee.A", "invocation"));
        orderForward[1].ShouldBe(("M:Callee.B", "invocation"));
        orderForward[2].ShouldBe(("M:Callee.B", "methodGroup"));
    }
}

using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

// The concrete receiver type (CallEdge.ReceiverTypeConcrete) must be forwarded onto the node the edge
// reaches (TraceNode.ConcreteReceiver) so the renderer can substitute the declaring-type placeholders.
// Pure plumbing — no narrowing semantics (dispatch still uses the open ReceiverType).
public sealed class ConcreteReceiverPropagationTests
{
    private static FactGraphData Graph(params CallEdge[] edges) =>
        new(edges, System.Array.Empty<ImplementsEdge>(), System.Array.Empty<MethodRef>(), System.Array.Empty<BaseEdge>());

    private static TraceNode Child(TraceNode root, string id) => root.Children.Single(c => c.SymbolId == id);

    [Test]
    public void A_direct_call_edges_concrete_receiver_lands_on_the_reached_node()
    {
        var graph = Graph(
            new CallEdge(
                "M:N.Caller.Go",
                "M:N.QueryPipeline`2.Enumerate",
                "invocation",
                "f.cs",
                10,
                ReceiverType: "N.QueryPipeline<T, U>",
                ReceiverTypeConcrete: "N.QueryPipeline<N.Account, N.Invoice>"
            )
        );

        var root = FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single();
        var enumerate = Child(root, "M:N.QueryPipeline`2.Enumerate");

        enumerate.ConcreteReceiver.ShouldBe("N.QueryPipeline<N.Account, N.Invoice>");
    }

    [Test]
    public void A_node_reached_by_an_edge_with_no_concrete_receiver_has_a_null_concrete_receiver()
    {
        var graph = Graph(new CallEdge("M:N.Caller.Go", "M:N.Plain.Do", "invocation", "f.cs", 10, ReceiverType: "N.Plain"));

        var root = FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single();

        Child(root, "M:N.Plain.Do").ConcreteReceiver.ShouldBeNull();
        // The root itself was reached by no edge.
        root.ConcreteReceiver.ShouldBeNull();
    }
}

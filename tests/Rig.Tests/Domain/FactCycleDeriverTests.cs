using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// FactCycleDeriver.DeriveEventCycles — the `event_cycle` GRAPH hazard: a feedback cycle that closes through
// ≥1 publish→consumer DELIVERY edge (a Kind="handoff" CallEdge tagged event_raise / actor_tell). Detected as
// a strongly-connected component of the Caller→Callee graph that CONTAINS such a delivery edge (both endpoints
// in the SCC). Confidence is "high" when every delivery edge is an exact event_raise, "low" when any is the
// heuristic process-name actor_tell. These synthetic-graph tests mirror EventDeliveryEdgeTests' harness.
public sealed class FactCycleDeriverTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    [Test]
    public void A_feedback_cycle_through_an_event_raise_is_one_high_confidence_cycle()
    {
        // A raises an event delivered to handler H; H calls B; B raises an event delivered back to A — a
        // feedback loop closing through two event_raise delivery edges. {A, H, B} is one SCC.
        var graph = Graph(
            new CallEdge("M:N.A", "M:N.H", "handoff", "f.cs", 10, HandoffDispatcher: "event_raise"),
            new CallEdge("M:N.H", "M:N.B", "invocation", "f.cs", 20),
            new CallEdge("M:N.B", "M:N.A", "handoff", "f.cs", 30, HandoffDispatcher: "event_raise")
        );

        var cycles = FactCycleDeriver.DeriveEventCycles(graph);

        cycles.Count.ShouldBe(1);
        cycles[0].Confidence.ShouldBe("high");
        cycles[0].Members.ShouldBe(new[] { "M:N.A", "M:N.B", "M:N.H" }); // sorted Ordinal
        cycles[0].DeliveryEdges.Count.ShouldBe(2);
    }

    [Test]
    public void A_cycle_whose_delivery_edge_is_actor_tell_is_low_confidence()
    {
        var graph = Graph(
            new CallEdge("M:N.A", "M:N.H", "handoff", "f.cs", 10, HandoffDispatcher: "actor_tell"),
            new CallEdge("M:N.H", "M:N.A", "invocation", "f.cs", 20)
        );

        var cycles = FactCycleDeriver.DeriveEventCycles(graph);

        cycles.Count.ShouldBe(1);
        cycles[0].Confidence.ShouldBe("low");
    }

    [Test]
    public void A_linear_delivery_chain_with_no_edge_back_is_no_cycle()
    {
        // A raises to H, H calls B — but nothing returns to A. No SCC > size 1, no self-loop: no cycle.
        var graph = Graph(
            new CallEdge("M:N.A", "M:N.H", "handoff", "f.cs", 10, HandoffDispatcher: "event_raise"),
            new CallEdge("M:N.H", "M:N.B", "invocation", "f.cs", 20)
        );

        FactCycleDeriver.DeriveEventCycles(graph).ShouldBeEmpty();
    }

    [Test]
    public void A_pure_synchronous_recursion_cycle_is_not_an_event_cycle()
    {
        // A -> B -> A is a real SCC and a real cycle, but it traverses NO delivery edge — so it is not an
        // event_cycle (the hazard requires the loop to close through a publish→consumer delivery hop).
        var graph = Graph(
            new CallEdge("M:N.A", "M:N.B", "invocation", "f.cs", 10),
            new CallEdge("M:N.B", "M:N.A", "invocation", "f.cs", 20)
        );

        FactCycleDeriver.DeriveEventCycles(graph).ShouldBeEmpty();
    }

    [Test]
    public void A_self_delivery_edge_is_a_size_one_cycle()
    {
        // A raises an event it itself handles: A --event_raise--> A is a size-1 SCC with a self delivery edge.
        var graph = Graph(new CallEdge("M:N.A", "M:N.A", "handoff", "f.cs", 10, HandoffDispatcher: "event_raise"));

        var cycles = FactCycleDeriver.DeriveEventCycles(graph);

        cycles.Count.ShouldBe(1);
        cycles[0].Members.ShouldBe(new[] { "M:N.A" });
        cycles[0].Confidence.ShouldBe("high");
    }
}

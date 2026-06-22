using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// The publish→consumer DELIVERY edge for C# events (FactPathFinder.AddEventDeliveryEdges): a raise
// (`someEvent?.Invoke()`) is resolved, by event identity, to the event's subscribers and ADDED as a handoff
// edge raiser→handler — the edge no syntactic call records. Modeled as a handoff: sync-cut by default,
// walked under --async, so the raiser's --async reach now includes what the handler does. Subscriptions vs
// raises are discriminated by co-location with a method-group edge (a `+= Handler` site has one; a raise
// doesn't).
public sealed class EventDeliveryEdgeTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    private const string Evt = "E:N.Bus.Changed";

    [Test]
    public void Raise_delivers_to_subscribers_as_an_async_handoff()
    {
        // Register subscribes Handler to the event at (Register, f.cs, 10) — the methodGroup edge already in
        // the graph. Handler's body writes an effect. Raiser raises the event at (Raiser, f.cs, 50).
        var graph = Graph(
            new CallEdge("M:N.Register.Wire", "M:N.Handler.OnChanged", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.Handler.OnChanged", "M:N.Repo.Save", "invocation", "f.cs", 99)
        );
        var reads = new[]
        {
            new EventReadSite("M:N.Register.Wire", "f.cs", 10, Evt), // subscription (co-located w/ methodGroup)
            new EventReadSite("M:N.Raiser.Fire", "f.cs", 50, Evt), // raise (no co-located methodGroup)
        };

        var delivered = FactPathFinder.AddEventDeliveryEdges(graph, reads);

        // The raise→handler edge was added: a handoff tagged event_raise, at the raise site.
        var edge = delivered.CallEdges.Single(e => e.Caller == "M:N.Raiser.Fire" && e.Callee == "M:N.Handler.OnChanged");
        edge.Kind.ShouldBe("handoff");
        edge.HandoffDispatcher.ShouldBe("event_raise");
        edge.Line.ShouldBe(50);

        // Sync-cut: the raiser does NOT synchronously reach the handler (delivery is deferred).
        var sync = FactPathFinder.BuildTree(delivered, "M:N.Raiser.Fire").Single();
        sync.Children.ShouldNotContain(c => c.SymbolId == "M:N.Handler.OnChanged");

        // Under --async the delivery edge is walked, so the raiser reaches the handler AND its effect.
        FactPathFinder
            .Find(delivered, "M:N.Raiser.Fire", "M:N.Repo.Save", mode: FactPathFinder.TraversalMode.AsyncInclude)
            .ShouldNotBeNull();
        FactPathFinder.Find(delivered, "M:N.Raiser.Fire", "M:N.Repo.Save").ShouldBeNull(); // not synchronously
    }

    [Test]
    public void A_subscription_with_no_raise_adds_no_delivery_edge()
    {
        var graph = Graph(new CallEdge("M:N.Register.Wire", "M:N.Handler.OnChanged", "methodGroup", "f.cs", 10));
        var reads = new[] { new EventReadSite("M:N.Register.Wire", "f.cs", 10, Evt) }; // subscription only

        var delivered = FactPathFinder.AddEventDeliveryEdges(graph, reads);

        delivered.CallEdges.ShouldBe(graph.CallEdges); // unchanged — nothing to deliver to
    }

    [Test]
    public void A_raise_of_an_event_with_no_subscribers_adds_nothing()
    {
        var graph = Graph(new CallEdge("M:N.Raiser.Fire", "M:N.Other.X", "invocation", "f.cs", 1));
        var reads = new[] { new EventReadSite("M:N.Raiser.Fire", "f.cs", 50, Evt) }; // raise, but nobody subscribes

        var delivered = FactPathFinder.AddEventDeliveryEdges(graph, reads);

        delivered.CallEdges.ShouldBe(graph.CallEdges);
    }

    [Test]
    public void Repeated_raises_of_the_same_event_in_one_method_dedupe_to_one_edge()
    {
        var graph = Graph(
            new CallEdge("M:N.Register.Wire", "M:N.Handler.OnChanged", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.Raiser.Fire", "M:N.Other.X", "invocation", "f.cs", 1)
        );
        var reads = new[]
        {
            new EventReadSite("M:N.Register.Wire", "f.cs", 10, Evt),
            new EventReadSite("M:N.Raiser.Fire", "f.cs", 50, Evt), // raise #1
            new EventReadSite("M:N.Raiser.Fire", "f.cs", 60, Evt), // raise #2 (same method, same event)
        };

        var delivered = FactPathFinder.AddEventDeliveryEdges(graph, reads);

        delivered.CallEdges.Count(e => e.Caller == "M:N.Raiser.Fire" && e.Callee == "M:N.Handler.OnChanged").ShouldBe(1);
    }
}

using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// A+D fix (docs/FIX-event-raise-overapproximation.md): the symbol-only delivery join fans a raise out to
// EVERY same-symbol subscriber with no instance/call-site identity, so a multi-subscriber channel produces
// false cross-caller edges. AddDeliveryEdges now stamps DeliveryPrecision — Exact for a single-handler
// channel (unambiguous), Fanout for a multi-handler one (the imprecise cross-product). The default --async
// (TraversalMode.AsyncExact) walks Exact delivery + every sound handoff but CUTS Fanout; --include-delivery
// (AsyncInclude) restores the Fanout superset. SyncCut cuts all handoffs as before.
public sealed class DeliveryFanoutPrecisionTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    private const string Evt = "E:N.Bus.Changed";

    private static DeliverySite Read(string caller, string file, int line) =>
        new(Caller: caller, FilePath: file, Line: line, IdentityToken: Evt, Tag: "event_raise", Role: DeliveryRole.ByColocation);

    [Test]
    public void Single_subscriber_channel_is_stamped_exact()
    {
        var graph = Graph(new CallEdge("M:N.RegisterA.Wire", "M:N.HandlerA.On", "methodGroup", "f.cs", 10));
        var reads = new[] { Read("M:N.RegisterA.Wire", "f.cs", 10), Read("M:N.Raiser.Fire", "f.cs", 50) };

        var delivered = FactPathFinder.AddDeliveryEdges(graph, reads);

        var edge = delivered.CallEdges.Single(e => e.Caller == "M:N.Raiser.Fire" && e.Callee == "M:N.HandlerA.On");
        edge.DeliveryPrecision.ShouldBe(DeliveryPrecisions.Exact);
    }

    [Test]
    public void Multi_subscriber_channel_is_stamped_fanout()
    {
        // Two unrelated callers each wire their OWN handler to the same event symbol — the dialog/proxy
        // pattern. The raise then fans out to BOTH, regardless of which caller it actually belongs to.
        var graph = Graph(
            new CallEdge("M:N.RegisterA.Wire", "M:N.HandlerA.On", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.RegisterB.Wire", "M:N.HandlerB.On", "methodGroup", "g.cs", 20)
        );
        var reads = new[]
        {
            Read("M:N.RegisterA.Wire", "f.cs", 10),
            Read("M:N.RegisterB.Wire", "g.cs", 20),
            Read("M:N.Raiser.Fire", "f.cs", 50),
        };

        var delivered = FactPathFinder.AddDeliveryEdges(graph, reads);

        var fanoutEdges = delivered.CallEdges.Where(e => e.Caller == "M:N.Raiser.Fire" && e.Kind == EdgeKinds.Handoff).ToList();
        fanoutEdges.Count.ShouldBe(2); // the cross-product: one raise → both handlers
        fanoutEdges.ShouldAllBe(e => e.DeliveryPrecision == DeliveryPrecisions.Fanout);
    }

    [Test]
    public void AsyncExact_cuts_fanout_but_AsyncInclude_walks_it()
    {
        var graph = Graph(
            new CallEdge("M:N.RegisterA.Wire", "M:N.HandlerA.On", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.RegisterB.Wire", "M:N.HandlerB.On", "methodGroup", "g.cs", 20),
            new CallEdge("M:N.HandlerB.On", "M:N.Repo.Save", "invocation", "g.cs", 99)
        );
        var reads = new[]
        {
            Read("M:N.RegisterA.Wire", "f.cs", 10),
            Read("M:N.RegisterB.Wire", "g.cs", 20),
            Read("M:N.Raiser.Fire", "f.cs", 50),
        };
        var delivered = FactPathFinder.AddDeliveryEdges(graph, reads);

        // Default --async (AsyncExact) does NOT cross the fan-out: the raiser does not reach a handler it
        // may not actually be wired to.
        FactPathFinder.Find(delivered, "M:N.Raiser.Fire", "M:N.Repo.Save", mode: FactPathFinder.TraversalMode.AsyncExact).ShouldBeNull();

        // --include-delivery (AsyncInclude) restores the over-approximate fan-out reach.
        FactPathFinder
            .Find(delivered, "M:N.Raiser.Fire", "M:N.Repo.Save", mode: FactPathFinder.TraversalMode.AsyncInclude)
            .ShouldNotBeNull();
    }

    [Test]
    public void AsyncExact_still_walks_exact_single_subscriber_delivery()
    {
        var graph = Graph(
            new CallEdge("M:N.RegisterA.Wire", "M:N.HandlerA.On", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.HandlerA.On", "M:N.Repo.Save", "invocation", "f.cs", 99)
        );
        var reads = new[] { Read("M:N.RegisterA.Wire", "f.cs", 10), Read("M:N.Raiser.Fire", "f.cs", 50) };
        var delivered = FactPathFinder.AddDeliveryEdges(graph, reads);

        // A single-subscriber channel is unambiguous, so AsyncExact crosses it (only fan-out is quarantined).
        FactPathFinder
            .Find(delivered, "M:N.Raiser.Fire", "M:N.Repo.Save", mode: FactPathFinder.TraversalMode.AsyncExact)
            .ShouldNotBeNull();
    }

    [Test]
    public void CutsHandoff_targets_only_fanout_delivery_not_sound_handoffs()
    {
        var fanout = new CallEdge(
            "a",
            "b",
            EdgeKinds.Handoff,
            "f.cs",
            1,
            HandoffDispatcher: "event_raise",
            DeliveryPrecision: DeliveryPrecisions.Fanout
        );
        var exact = new CallEdge(
            "a",
            "b",
            EdgeKinds.Handoff,
            "f.cs",
            1,
            HandoffDispatcher: "event_raise",
            DeliveryPrecision: DeliveryPrecisions.Exact
        );
        var eventReg = new CallEdge("a", "b", EdgeKinds.Handoff, "f.cs", 1, HandoffDispatcher: "event"); // registrant→handler
        var schedule = new CallEdge("a", "b", EdgeKinds.Handoff, "f.cs", 1, HandoffDispatcher: "meddbase.repeating.schedule");
        var plain = new CallEdge("a", "b", EdgeKinds.Invocation, "f.cs", 1);

        // SyncCut cuts every handoff; never a plain call.
        FactPathFinder.CutsHandoff(FactPathFinder.TraversalMode.SyncCut, fanout).ShouldBeTrue();
        FactPathFinder.CutsHandoff(FactPathFinder.TraversalMode.SyncCut, eventReg).ShouldBeTrue();
        FactPathFinder.CutsHandoff(FactPathFinder.TraversalMode.SyncCut, plain).ShouldBeFalse();

        // AsyncExact cuts ONLY the fan-out delivery edge — exact delivery, registrant→handler, and
        // scheduler handoffs are all kept.
        FactPathFinder.CutsHandoff(FactPathFinder.TraversalMode.AsyncExact, fanout).ShouldBeTrue();
        FactPathFinder.CutsHandoff(FactPathFinder.TraversalMode.AsyncExact, exact).ShouldBeFalse();
        FactPathFinder.CutsHandoff(FactPathFinder.TraversalMode.AsyncExact, eventReg).ShouldBeFalse();
        FactPathFinder.CutsHandoff(FactPathFinder.TraversalMode.AsyncExact, schedule).ShouldBeFalse();

        // AsyncInclude cuts nothing.
        FactPathFinder.CutsHandoff(FactPathFinder.TraversalMode.AsyncInclude, fanout).ShouldBeFalse();
    }
}

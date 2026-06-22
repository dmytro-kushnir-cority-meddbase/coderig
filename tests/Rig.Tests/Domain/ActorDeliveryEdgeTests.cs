using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// The publish→consumer DELIVERY edge for Echo actors, through the single framework-blind join
// (FactPathFinder.AddDeliveryEdges) — mirroring EventDeliveryEdgeTests. A `Process.tell(name, msg)` is
// resolved, by PROCESS NAME, to the handler(s) spawned under that name and ADDED as a handoff edge
// teller→handler — the edge no syntactic call records. Modeled as a handoff: sync-cut by default, walked
// under --async, so the teller's --async reach now includes what the handler does. Registrations (spawn)
// vs producers (tell/ask) are pre-discriminated on the DeliverySite Role; a spawn co-locates the process
// name with the handler's method-group edge (like an event `+= H`), a tell does not. The identity is a STRING
// process name, not an exact symbol, so the binding is ~heuristic (over-approximate on a shared name).
//
// NOTE: the member-path PRECISION GATE (a bare-variable process name like "pid" must not join) now lives in
// the actor LOADER (Reads.LoadActorDeliverySitesAsync), not in this Domain join — the join is framework-blind
// and joins purely on (Tag, IdentityToken). So there is no pure-Domain test for the gate here; it is covered
// loader-side. These tests use member-path tokens, exactly the sites the loader would emit.
public sealed class ActorDeliveryEdgeTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    private const string Proc = "ProcessDns.AccountService";

    // An actor DeliverySite as the loader emits it: tagged "actor_tell", the process name as the channel
    // identity, Role from the spawn/tell discrimination (Registration = spawn, Producer = tell).
    private static DeliverySite Site(string caller, string file, int line, string proc, bool isRegistration) =>
        new(
            Caller: caller,
            FilePath: file,
            Line: line,
            IdentityToken: proc,
            Tag: "actor_tell",
            Role: isRegistration ? DeliveryRole.Registration : DeliveryRole.Producer
        );

    [Test]
    public void Tell_delivers_to_the_spawn_handler_as_an_async_handoff()
    {
        // Wire spawns Handler under the process name at (Wire, f.cs, 10) — the methodGroup edge already in
        // the graph. Handler's body writes an effect. Teller tells the same process at (Teller, f.cs, 50).
        var graph = Graph(
            new CallEdge("M:N.Wire.Spawn", "M:N.Handler.OnMsg", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.Handler.OnMsg", "M:N.Repo.Save", "invocation", "f.cs", 99)
        );
        var sites = new[]
        {
            Site("M:N.Wire.Spawn", "f.cs", 10, Proc, isRegistration: true), // spawn (co-located w/ methodGroup)
            Site("M:N.Teller.Fire", "f.cs", 50, Proc, isRegistration: false), // tell (the producer)
        };

        var delivered = FactPathFinder.AddDeliveryEdges(graph, sites);

        // The teller→handler edge was added: a handoff tagged actor_tell, at the tell site.
        var edge = delivered.CallEdges.Single(e => e.Caller == "M:N.Teller.Fire" && e.Callee == "M:N.Handler.OnMsg");
        edge.Kind.ShouldBe("handoff");
        edge.HandoffDispatcher.ShouldBe("actor_tell");
        edge.Line.ShouldBe(50);

        // Sync-cut: the teller does NOT synchronously reach the handler (delivery is deferred to the mailbox).
        var sync = FactPathFinder.BuildTree(delivered, "M:N.Teller.Fire").Single();
        sync.Children.ShouldNotContain(c => c.SymbolId == "M:N.Handler.OnMsg");

        // Under --async the delivery edge is walked, so the teller reaches the handler AND its effect.
        FactPathFinder
            .Find(delivered, "M:N.Teller.Fire", "M:N.Repo.Save", mode: FactPathFinder.TraversalMode.AsyncInclude)
            .ShouldNotBeNull();
        FactPathFinder.Find(delivered, "M:N.Teller.Fire", "M:N.Repo.Save").ShouldBeNull(); // not synchronously
    }

    [Test]
    public void A_spawn_with_no_tell_adds_no_delivery_edge()
    {
        var graph = Graph(new CallEdge("M:N.Wire.Spawn", "M:N.Handler.OnMsg", "methodGroup", "f.cs", 10));
        var sites = new[] { Site("M:N.Wire.Spawn", "f.cs", 10, Proc, isRegistration: true) }; // spawn only

        var delivered = FactPathFinder.AddDeliveryEdges(graph, sites);

        delivered.CallEdges.ShouldBe(graph.CallEdges); // unchanged — nobody tells the process
    }

    [Test]
    public void A_tell_of_a_process_with_no_spawn_adds_nothing()
    {
        var graph = Graph(new CallEdge("M:N.Teller.Fire", "M:N.Other.X", "invocation", "f.cs", 1));
        var sites = new[] { Site("M:N.Teller.Fire", "f.cs", 50, Proc, isRegistration: false) }; // tell, but nobody spawns

        var delivered = FactPathFinder.AddDeliveryEdges(graph, sites);

        delivered.CallEdges.ShouldBe(graph.CallEdges);
    }

    [Test]
    public void Repeated_tells_of_the_same_process_in_one_method_dedupe_to_one_edge()
    {
        var graph = Graph(
            new CallEdge("M:N.Wire.Spawn", "M:N.Handler.OnMsg", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.Teller.Fire", "M:N.Other.X", "invocation", "f.cs", 1)
        );
        var sites = new[]
        {
            Site("M:N.Wire.Spawn", "f.cs", 10, Proc, isRegistration: true),
            Site("M:N.Teller.Fire", "f.cs", 50, Proc, isRegistration: false), // tell #1
            Site("M:N.Teller.Fire", "f.cs", 60, Proc, isRegistration: false), // tell #2 (same method, same process)
        };

        var delivered = FactPathFinder.AddDeliveryEdges(graph, sites);

        delivered.CallEdges.Count(e => e.Caller == "M:N.Teller.Fire" && e.Callee == "M:N.Handler.OnMsg").ShouldBe(1);
    }

    // TAG NAMESPACING: the join keys on (Tag, IdentityToken), so a registration and a producer that share an
    // identity TOKEN but carry different TAGS (e.g. an event channel vs an actor channel) must NOT cross. This
    // is what lets the one framework-blind join serve both frameworks safely even on a token collision.
    [Test]
    public void Same_token_different_tag_does_not_cross()
    {
        var graph = Graph(
            new CallEdge("M:N.Wire.Spawn", "M:N.Handler.OnMsg", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.Teller.Fire", "M:N.Other.X", "invocation", "f.cs", 1)
        );
        // Same IdentityToken (Proc) on both, but the registration is tagged "event_raise" and the producer
        // "actor_tell" — different channels, so the producer finds no handler.
        var sites = new[]
        {
            new DeliverySite(
                Caller: "M:N.Wire.Spawn",
                FilePath: "f.cs",
                Line: 10,
                IdentityToken: Proc,
                Tag: "event_raise",
                Role: DeliveryRole.Registration
            ),
            new DeliverySite(
                Caller: "M:N.Teller.Fire",
                FilePath: "f.cs",
                Line: 50,
                IdentityToken: Proc,
                Tag: "actor_tell",
                Role: DeliveryRole.Producer
            ),
        };

        var delivered = FactPathFinder.AddDeliveryEdges(graph, sites);

        delivered.CallEdges.ShouldBe(graph.CallEdges); // no cross-tag edge
    }
}

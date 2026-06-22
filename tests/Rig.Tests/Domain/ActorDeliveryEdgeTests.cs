using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// The publish→consumer DELIVERY edge for Echo actors (FactPathFinder.AddActorDeliveryEdges) — the SECOND
// resolver in the delivery-edge framework, mirroring EventDeliveryEdgeTests. A `Process.tell(name, msg)` is
// resolved, by PROCESS NAME, to the handler(s) spawned under that name and ADDED as a handoff edge
// teller→handler — the edge no syntactic call records. Modeled as a handoff: sync-cut by default, walked
// under --async, so the teller's --async reach now includes what the handler does. Registrations (spawn)
// vs producers (tell/ask) are pre-discriminated on the site (IsRegistration); a spawn co-locates the process
// name with the handler's method-group edge (like an event `+= H`), a tell does not. The identity is a STRING
// process name, not an exact symbol, so the binding is ~heuristic (over-approximate on a shared name).
public sealed class ActorDeliveryEdgeTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    private const string Proc = "ProcessDns.AccountService";

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
            new ActorDeliverySite("M:N.Wire.Spawn", "f.cs", 10, Proc, IsRegistration: true), // spawn (co-located w/ methodGroup)
            new ActorDeliverySite("M:N.Teller.Fire", "f.cs", 50, Proc, IsRegistration: false), // tell (the producer)
        };

        var delivered = FactPathFinder.AddActorDeliveryEdges(graph, sites);

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
        var sites = new[] { new ActorDeliverySite("M:N.Wire.Spawn", "f.cs", 10, Proc, IsRegistration: true) }; // spawn only

        var delivered = FactPathFinder.AddActorDeliveryEdges(graph, sites);

        delivered.CallEdges.ShouldBe(graph.CallEdges); // unchanged — nobody tells the process
    }

    [Test]
    public void A_tell_of_a_process_with_no_spawn_adds_nothing()
    {
        var graph = Graph(new CallEdge("M:N.Teller.Fire", "M:N.Other.X", "invocation", "f.cs", 1));
        var sites = new[] { new ActorDeliverySite("M:N.Teller.Fire", "f.cs", 50, Proc, IsRegistration: false) }; // tell, but nobody spawns

        var delivered = FactPathFinder.AddActorDeliveryEdges(graph, sites);

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
            new ActorDeliverySite("M:N.Wire.Spawn", "f.cs", 10, Proc, IsRegistration: true),
            new ActorDeliverySite("M:N.Teller.Fire", "f.cs", 50, Proc, IsRegistration: false), // tell #1
            new ActorDeliverySite("M:N.Teller.Fire", "f.cs", 60, Proc, IsRegistration: false), // tell #2 (same method, same process)
        };

        var delivered = FactPathFinder.AddActorDeliveryEdges(graph, sites);

        delivered.CallEdges.Count(e => e.Caller == "M:N.Teller.Fire" && e.Callee == "M:N.Handler.OnMsg").ShouldBe(1);
    }

    // PRECISION GATE: a BARE-variable process name (no member path — `tell(pid, ..)` / `spawn(name, ..)`)
    // is not a stable identity and collides spuriously across unrelated tells/spawns (Echo framework
    // internals were the dominant noise on MedDBase). Only member-path names (`ProcessDns.X`) join.
    [Test]
    public void A_bare_variable_process_name_is_not_joined()
    {
        var graph = Graph(
            new CallEdge("M:N.Wire.Spawn", "M:N.Handler.OnMsg", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.Teller.Fire", "M:N.Other.X", "invocation", "f.cs", 1)
        );
        // Both sides share the SAME bare name "pid" — but it's not a member path, so they must NOT join.
        var sites = new[]
        {
            new ActorDeliverySite("M:N.Wire.Spawn", "f.cs", 10, "pid", IsRegistration: true),
            new ActorDeliverySite("M:N.Teller.Fire", "f.cs", 50, "pid", IsRegistration: false),
        };

        var delivered = FactPathFinder.AddActorDeliveryEdges(graph, sites);

        delivered.CallEdges.ShouldBe(graph.CallEdges); // no spurious bare-name edge
    }
}

using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

public sealed class EventSubscriptionHandoffTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    [Test]
    public void Subscription_handler_is_sync_cut_but_walked_under_async()
    {
        var graph = Graph(
            new CallEdge("M:N.R.RegisterEvents", "M:N.H.OnX", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.H.OnX", "M:N.Effect", "invocation", "f.cs", 99)
        );
        var sites = new HashSet<EventSubscriptionSite> { new("M:N.R.RegisterEvents", "f.cs", 10) };

        var marked = FactPathFinder.MarkEventSubscriptionHandoffs(graph, sites);

        var sync = FactPathFinder.BuildTree(marked, "M:N.R.RegisterEvents").Single();
        sync.Children.ShouldNotContain(c => c.SymbolId == "M:N.H.OnX");

        var async = FactPathFinder.BuildTree(marked, "M:N.R.RegisterEvents", mode: FactPathFinder.TraversalMode.AsyncInclude).Single();
        async.Children.ShouldContain(c => c.SymbolId == "M:N.H.OnX");
    }

    [Test]
    public void A_plain_methodgroup_at_a_non_event_site_is_left_synchronous()
    {
        var graph = Graph(new CallEdge("M:N.R.Run", "M:N.H.OnX", "methodGroup", "f.cs", 10));

        var marked = FactPathFinder.MarkEventSubscriptionHandoffs(graph, new HashSet<EventSubscriptionSite>());

        var sync = FactPathFinder.BuildTree(marked, "M:N.R.Run").Single();
        sync.Children.ShouldContain(c => c.SymbolId == "M:N.H.OnX");
    }

    // Part (b) of the 2026-06-16 over-reach fix, exercised through the FORWARD path engine that `rig path`
    // uses (FactPathFinder.Find): after MarkEventSubscriptionHandoffs, an event-subscription `+=` edge is
    // sync-cut by default (no synchronous path through the handler) and only crossed under --async. This is
    // the same behavior reaches/tree already had; PathCommand now applies the same marking before Find.
    [Test]
    public void Find_does_not_cross_an_event_handoff_synchronously_but_does_under_async()
    {
        var graph = Graph(
            new CallEdge("M:N.R.RegisterEvents", "M:N.H.OnX", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.H.OnX", "M:N.Effect", "invocation", "f.cs", 99)
        );
        var sites = new HashSet<EventSubscriptionSite> { new("M:N.R.RegisterEvents", "f.cs", 10) };

        var marked = FactPathFinder.MarkEventSubscriptionHandoffs(graph, sites);

        FactPathFinder.Find(marked, "M:N.R.RegisterEvents", "M:N.Effect").ShouldBeNull();

        var asyncPath = FactPathFinder.Find(marked, "M:N.R.RegisterEvents", "M:N.Effect", mode: FactPathFinder.TraversalMode.AsyncInclude);
        asyncPath.ShouldNotBeNull();
        asyncPath!.ShouldContain(s => s.SymbolId == "M:N.H.OnX");
    }

    // Same fix through the REVERSE reachability engine that `rig callers` uses (FactPathFinder.ReachedBy):
    // an event handler is NOT a synchronous reverse caller of its body's effect; it only reaches it under
    // --async. CallersCommand now applies the same marking before ReachedBy.
    [Test]
    public void ReachedBy_excludes_an_event_handler_synchronously_but_includes_it_under_async()
    {
        var graph = Graph(
            new CallEdge("M:N.R.RegisterEvents", "M:N.H.OnX", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.H.OnX", "M:N.Effect", "invocation", "f.cs", 99)
        );
        var sites = new HashSet<EventSubscriptionSite> { new("M:N.R.RegisterEvents", "f.cs", 10) };

        var marked = FactPathFinder.MarkEventSubscriptionHandoffs(graph, sites);

        // Synchronous reverse closure of the effect reaches the handler (a direct caller) but is sync-cut
        // at the `+=` edge, so the subscriber RegisterEvents is NOT a synchronous caller.
        var sync = FactPathFinder.ReachedBy(marked, "M:N.Effect");
        sync.ContainsKey("M:N.R.RegisterEvents").ShouldBeFalse();

        // Under --async the handoff edge is walked, so the subscriber reaches the effect.
        var async = FactPathFinder.ReachedBy(marked, "M:N.Effect", mode: FactPathFinder.TraversalMode.AsyncInclude);
        async.ContainsKey("M:N.R.RegisterEvents").ShouldBeTrue();
    }
}

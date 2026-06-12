using System.Collections.Generic;
using System.Linq;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// `someEvent += Handler` is a deferred subscription, not a synchronous call. MarkEventSubscriptionHandoffs
// reclassifies the method-group edge at an event-subscription site to a handoff, so the sync tree skips
// the handler (it runs when the event is raised) and --async walks it tagged.
public sealed class EventSubscriptionHandoffTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct().Select(M).ToArray();
        return new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), nodes);
    }

    [Fact]
    public void Subscription_handler_is_sync_cut_but_walked_under_async()
    {
        var graph = Graph(
            new CallEdge("M:N.R.RegisterEvents", "M:N.H.OnX", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.H.OnX", "M:N.Effect", "invocation", "f.cs", 99)
        );
        var sites = new HashSet<(string, string, int)> { ("M:N.R.RegisterEvents", "f.cs", 10) };

        var marked = FactPathFinder.MarkEventSubscriptionHandoffs(graph, sites);

        // Sync (default): the deferred handler is NOT walked.
        var sync = FactPathFinder.BuildTree(marked, "M:N.R.RegisterEvents").Single();
        sync.Children.ShouldNotContain(c => c.SymbolId == "M:N.H.OnX");

        // Async: the handler appears (walked across the handoff).
        var async = FactPathFinder.BuildTree(marked, "M:N.R.RegisterEvents", mode: FactPathFinder.TraversalMode.AsyncInclude).Single();
        async.Children.ShouldContain(c => c.SymbolId == "M:N.H.OnX");
    }

    [Fact]
    public void A_plain_methodgroup_at_a_non_event_site_is_left_synchronous()
    {
        var graph = Graph(new CallEdge("M:N.R.Run", "M:N.H.OnX", "methodGroup", "f.cs", 10));

        // No event sites -> no reclassification; a plain method-group stays a synchronous edge.
        var marked = FactPathFinder.MarkEventSubscriptionHandoffs(graph, new HashSet<(string, string, int)>());

        var sync = FactPathFinder.BuildTree(marked, "M:N.R.Run").Single();
        sync.Children.ShouldContain(c => c.SymbolId == "M:N.H.OnX");
    }
}

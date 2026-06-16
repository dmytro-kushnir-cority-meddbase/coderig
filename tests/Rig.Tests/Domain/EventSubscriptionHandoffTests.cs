using System.Collections.Generic;
using System.Linq;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class EventSubscriptionHandoffTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), nodes);
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
}

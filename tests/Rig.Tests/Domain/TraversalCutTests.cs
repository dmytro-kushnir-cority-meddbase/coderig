using System.Linq;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class TraversalCutTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct().Select(M).ToArray();
        return new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), nodes);
    }

    private static FactTraversalCutRule Cut(string pattern, string label = "test-cut") => new(pattern, label);

    [Test]
    public void Tree_expands_the_first_occurrence_in_render_order_and_marks_later_ones_seen()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Noise", "invocation", "f.cs", 10),
            new CallEdge("M:Noise", "M:Target", "invocation", "f.cs", 15),
            new CallEdge("M:A", "M:Target", "invocation", "f.cs", 20),
            new CallEdge("M:Target", "M:Leaf", "invocation", "f.cs", 25)
        );

        var roots = FactPathFinder.BuildTree(graph, "M:A");

        roots.Count.ShouldBe(1);
        var a = roots[0];
        a.Children.ShouldContain(c => c.SymbolId == "M:Noise");
        a.Children.ShouldContain(c => c.SymbolId == "M:Target");
        var noise = a.Children.First(c => c.SymbolId == "M:Noise");
        var target = a.Children.First(c => c.SymbolId == "M:Target");
        var noiseToTarget = noise.Children.First(c => c.SymbolId == "M:Target");
        noiseToTarget.Truncated.ShouldBeFalse();
        noiseToTarget.Children.ShouldContain(c => c.SymbolId == "M:Leaf");
        target.Truncated.ShouldBeTrue();
        target.Children.ShouldBeEmpty();
    }

    [Test]
    public void Cut_rule_makes_node_a_traversal_leaf_in_BuildTree()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Infra", "invocation", "f.cs", 10),
            new CallEdge("M:Infra", "M:X", "invocation", "f.cs", 20),
            new CallEdge("M:X", "M:Y", "invocation", "f.cs", 30)
        );
        var cuts = new[] { Cut("M:Infra") };

        var roots = FactPathFinder.BuildTree(graph, "M:A", cutRules: cuts);

        roots.Count.ShouldBe(1);
        var a = roots[0];
        a.Children.ShouldContain(c => c.SymbolId == "M:Infra");
        var infra = a.Children.First(c => c.SymbolId == "M:Infra");
        infra.Children.ShouldBeEmpty();
        infra.Truncated.ShouldBeFalse();
        a.Children.ShouldNotContain(c => c.SymbolId == "M:X");
        a.Children.ShouldNotContain(c => c.SymbolId == "M:Y");
    }

    [Test]
    public void Cut_rule_stops_ReachesWithFanout_at_cut_node()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Infra", "invocation", "f.cs", 10),
            new CallEdge("M:Infra", "M:X", "invocation", "f.cs", 20),
            new CallEdge("M:X", "M:Y", "invocation", "f.cs", 30)
        );
        var cuts = new[] { Cut("M:Infra") };

        var reach = FactPathFinder.ReachesWithFanout(graph, "M:A", cutRules: cuts);

        reach.Keys.ShouldContain("M:Infra");
        reach.Keys.ShouldNotContain("M:X");
        reach.Keys.ShouldNotContain("M:Y");
    }

    [Test]
    public void Without_cut_rule_X_and_Y_are_reached()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Infra", "invocation", "f.cs", 10),
            new CallEdge("M:Infra", "M:X", "invocation", "f.cs", 20),
            new CallEdge("M:X", "M:Y", "invocation", "f.cs", 30)
        );

        var reach = FactPathFinder.ReachesWithFanout(graph, "M:A");
        reach.Keys.ShouldContain("M:Infra");
        reach.Keys.ShouldContain("M:X");
        reach.Keys.ShouldContain("M:Y");

        var roots = FactPathFinder.BuildTree(graph, "M:A");
        var a = roots.Single();
        var infra = a.Children.Single(c => c.SymbolId == "M:Infra");
        infra.Children.ShouldContain(c => c.SymbolId == "M:X");
    }

    [Test]
    public void Cut_rule_pattern_is_case_insensitive_substring()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Some.ServiceHelper.CreateService(System.String)", "invocation", "f.cs", 10),
            new CallEdge("M:Some.ServiceHelper.CreateService(System.String)", "M:Downstream", "invocation", "f.cs", 20)
        );
        var cuts = new[] { Cut("servicehelper.createservice") };

        var reach = FactPathFinder.ReachesWithFanout(graph, "M:A", cutRules: cuts);

        reach.Keys.ShouldContain("M:Some.ServiceHelper.CreateService(System.String)");
        reach.Keys.ShouldNotContain("M:Downstream");
    }

    [Test]
    public void ReachableFromAll_is_not_affected_by_cut_rules()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Infra", "invocation", "f.cs", 10),
            new CallEdge("M:Infra", "M:X", "invocation", "f.cs", 20)
        );

        var reachAll = FactPathFinder.ReachableFromAll(graph, new[] { "M:A" });

        reachAll.ShouldContain("M:Infra");
        reachAll.ShouldContain("M:X");
    }
}

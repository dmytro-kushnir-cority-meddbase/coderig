using System.Linq;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Unit tests for Task B (traversal-cut rules) and Task A regression (shallowest-first BFS).
public sealed class TraversalCutTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct().Select(M).ToArray();
        return new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), nodes);
    }

    private static FactTraversalCutRule Cut(string pattern, string label = "test-cut") => new(pattern, label);

    // --- Task A regression: shallowest-first BFS expansion ---

    // A->Noise (first in source order), Noise->Target (deeper), AND A->Target (shallow, source-order
    // AFTER Noise), with Target->Leaf. The tree is built DEPTH-FIRST in render (pre-order) order, so the
    // FIRST occurrence of a shared symbol in top-to-bottom reading order is the one expanded and every
    // later occurrence is the "↺seen" leaf — the marker always refers to a subtree shown ABOVE it.
    // Here `Noise` (line 10) is walked before `Target` (line 20), so `Noise->Target` is the first-read
    // occurrence and expands (carrying Leaf); the shallow `A->Target`, rendered afterwards, becomes
    // ↺seen. (This supersedes the earlier breadth-first "shallowest occurrence expands" rule: that kept
    // the subtree at the least-indented point but let a ↺seen under an earlier sibling read BEFORE its
    // expansion. First-mention-expands is the top-to-down reading contract.)
    [Fact]
    public void Tree_expands_the_first_occurrence_in_render_order_and_marks_later_ones_seen()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Noise", "invocation", "f.cs", 10),
            new CallEdge("M:Noise", "M:Target", "invocation", "f.cs", 15),
            new CallEdge("M:A", "M:Target", "invocation", "f.cs", 20), // shallow, after Noise in source
            new CallEdge("M:Target", "M:Leaf", "invocation", "f.cs", 25)
        );

        var roots = FactPathFinder.BuildTree(graph, "M:A");

        roots.Count.ShouldBe(1);
        var a = roots[0];

        // A has both Noise and Target as direct children.
        a.Children.ShouldContain(c => c.SymbolId == "M:Noise");
        a.Children.ShouldContain(c => c.SymbolId == "M:Target");
        var noise = a.Children.First(c => c.SymbolId == "M:Noise");
        var target = a.Children.First(c => c.SymbolId == "M:Target");

        // Noise->Target is the FIRST occurrence in render order -> expanded, carrying Leaf.
        var noiseToTarget = noise.Children.First(c => c.SymbolId == "M:Target");
        noiseToTarget.Truncated.ShouldBeFalse();
        noiseToTarget.Children.ShouldContain(c => c.SymbolId == "M:Leaf");

        // The shallow A->Target, rendered AFTER, is the ↺seen leaf (points up to the expansion above).
        target.Truncated.ShouldBeTrue();
        target.Children.ShouldBeEmpty();
    }

    // --- Task B: traversal-cut rules ---

    // Synthetic graph: A->Infra->X->Y. With a cut on Infra:
    // (a) BuildTree shows Infra as a leaf (no children, «cut» marker would be shown in CLI);
    //     X and Y are absent from the tree.
    // (b) ReachesWithFanout from A does NOT include X or Y (closure stops at Infra).
    // (c) Without the cut, X and Y ARE reached.
    [Fact]
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

        // Infra is emitted but has no children (traversal cut).
        infra.Children.ShouldBeEmpty();
        infra.Truncated.ShouldBeFalse(); // it's a cut leaf, not ↺seen

        // X and Y are absent.
        a.Children.ShouldNotContain(c => c.SymbolId == "M:X");
        a.Children.ShouldNotContain(c => c.SymbolId == "M:Y");
    }

    [Fact]
    public void Cut_rule_stops_ReachesWithFanout_at_cut_node()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Infra", "invocation", "f.cs", 10),
            new CallEdge("M:Infra", "M:X", "invocation", "f.cs", 20),
            new CallEdge("M:X", "M:Y", "invocation", "f.cs", 30)
        );
        var cuts = new[] { Cut("M:Infra") };

        var reach = FactPathFinder.ReachesWithFanout(graph, "M:A", cutRules: cuts);

        reach.Keys.ShouldContain("M:Infra"); // Infra itself is reachable
        reach.Keys.ShouldNotContain("M:X"); // stopped at cut
        reach.Keys.ShouldNotContain("M:Y");
    }

    [Fact]
    public void Without_cut_rule_X_and_Y_are_reached()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Infra", "invocation", "f.cs", 10),
            new CallEdge("M:Infra", "M:X", "invocation", "f.cs", 20),
            new CallEdge("M:X", "M:Y", "invocation", "f.cs", 30)
        );

        // No cut rules — full traversal.
        var reach = FactPathFinder.ReachesWithFanout(graph, "M:A");
        reach.Keys.ShouldContain("M:Infra");
        reach.Keys.ShouldContain("M:X");
        reach.Keys.ShouldContain("M:Y");

        var roots = FactPathFinder.BuildTree(graph, "M:A");
        var a = roots.Single();
        var infra = a.Children.Single(c => c.SymbolId == "M:Infra");
        infra.Children.ShouldContain(c => c.SymbolId == "M:X");
    }

    // A cut on a pattern that only matches part of the name: verify substring matching works.
    [Fact]
    public void Cut_rule_pattern_is_case_insensitive_substring()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Some.ServiceHelper.CreateService(System.String)", "invocation", "f.cs", 10),
            new CallEdge("M:Some.ServiceHelper.CreateService(System.String)", "M:Downstream", "invocation", "f.cs", 20)
        );
        // Pattern uses different case and only a fragment.
        var cuts = new[] { Cut("servicehelper.createservice") };

        var reach = FactPathFinder.ReachesWithFanout(graph, "M:A", cutRules: cuts);

        reach.Keys.ShouldContain("M:Some.ServiceHelper.CreateService(System.String)");
        reach.Keys.ShouldNotContain("M:Downstream");
    }

    // Verify that ReachableFromAll (dead-code path) is NOT affected by cuts — it must see the full graph.
    [Fact]
    public void ReachableFromAll_is_not_affected_by_cut_rules()
    {
        var graph = Graph(
            new CallEdge("M:A", "M:Infra", "invocation", "f.cs", 10),
            new CallEdge("M:Infra", "M:X", "invocation", "f.cs", 20)
        );

        // ReachableFromAll takes a root list, not cut rules — verifying it doesn't use cuts.
        var reachAll = FactPathFinder.ReachableFromAll(graph, new[] { "M:A" });

        reachAll.ShouldContain("M:Infra");
        reachAll.ShouldContain("M:X"); // unaffected by any cuts (cuts not passed here)
    }
}

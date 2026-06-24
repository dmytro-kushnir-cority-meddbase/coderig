using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Phase 3 of static monomorphization (docs/design-dispatch-precision.md): MonomorphCollapse folds the split
// `~mono` instantiation node ids surfaced by a traversal back to their BASE method id — restoring the
// effect-join (`reachable.ContainsKey(e.EnclosingSymbolId)`, keyed on the base id) and deduping fragmented
// instantiations into one base entry, while leaving real CLI output byte-identical until Phase 2's Materialize
// is wired in (the GUARDED no-op). Tests are pure over synthetic results / synthetic FactGraphData, mirroring
// GenericMonomorphizerTests construction.
public sealed class MonomorphCollapseTests
{
    private const string Base = "M:N.Repo.SaveServices";
    private static readonly string MonoBilling = MonomorphizedNodeId.For(
        Base,
        Array.Empty<string>(),
        new[] { "N.BillingRuleEntity", "int" }
    );
    private static readonly string MonoContact = MonomorphizedNodeId.For(Base, Array.Empty<string>(), new[] { "N.ContactEntity", "int" });

    private static FactPathFinder.ReachInfo Info(int depth) =>
        new(Depth: depth, LoopNesting: 0, NearestLoopKind: null, NearestLoopDetail: null);

    // ---- CollapseReachInfo --------------------------------------------------------------------------

    [Test]
    public void CollapseReachInfo_relabels_mono_keys_to_base()
    {
        var input = new Dictionary<string, FactPathFinder.ReachInfo>(StringComparer.Ordinal)
        {
            ["M:N.Caller.DoIt"] = Info(0),
            [MonoBilling] = Info(2),
        };

        var collapsed = MonomorphCollapse.CollapseReachInfo(input);

        collapsed.Keys.ShouldContain(Base);
        collapsed.Keys.ShouldNotContain(MonoBilling);
        collapsed.Keys.ShouldContain("M:N.Caller.DoIt");
        collapsed[Base].Depth.ShouldBe(2);
    }

    [Test]
    public void CollapseReachInfo_unions_collisions_keeping_min_depth()
    {
        var input = new Dictionary<string, FactPathFinder.ReachInfo>(StringComparer.Ordinal)
        {
            [MonoBilling] = Info(5),
            [MonoContact] = Info(3),
            [Base] = Info(7),
        };

        var collapsed = MonomorphCollapse.CollapseReachInfo(input);

        // Two instantiations + the already-present base all fold to ONE base key, MIN depth wins.
        collapsed.Count.ShouldBe(1);
        collapsed.Keys.ShouldContain(Base);
        collapsed[Base].Depth.ShouldBe(3);
    }

    [Test]
    public void CollapseReachInfo_is_a_reference_equal_noop_without_mono()
    {
        var input = new Dictionary<string, FactPathFinder.ReachInfo>(StringComparer.Ordinal)
        {
            ["M:N.Caller.DoIt"] = Info(0),
            [Base] = Info(2),
        };

        var collapsed = MonomorphCollapse.CollapseReachInfo(input);

        collapsed.ShouldBeSameAs(input);
    }

    // ---- CollapseDepthMap ---------------------------------------------------------------------------

    [Test]
    public void CollapseDepthMap_relabels_and_keeps_min_depth_on_collision()
    {
        var input = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["M:N.Caller.DoIt"] = 1,
            [MonoBilling] = 4,
            [MonoContact] = 2,
        };

        var collapsed = MonomorphCollapse.CollapseDepthMap(input);

        collapsed["M:N.Caller.DoIt"].ShouldBe(1);
        collapsed.Keys.ShouldContain(Base);
        collapsed.Keys.ShouldNotContain(MonoBilling);
        collapsed[Base].ShouldBe(2);
    }

    [Test]
    public void CollapseDepthMap_is_a_reference_equal_noop_without_mono()
    {
        var input = new Dictionary<string, int>(StringComparer.Ordinal) { ["M:N.Caller.DoIt"] = 1 };

        MonomorphCollapse.CollapseDepthMap(input).ShouldBeSameAs(input);
    }

    // ---- CollapsePath -------------------------------------------------------------------------------

    [Test]
    public void CollapsePath_relabels_mono_steps_and_preserves_order_and_fields()
    {
        var path = new[]
        {
            new PathStep("M:N.Caller.DoIt", "entry", "f.cs", 1),
            new PathStep(MonoBilling, "invocation", "f.cs", 9, LoopKind: "for", Fanout: 3, DispatchBasis: "roslyn"),
            new PathStep("M:N.BillingRuleEntity.Delete", "override-dispatch", "f.cs", 12),
        };

        var collapsed = MonomorphCollapse.CollapsePath(path);

        collapsed.Count.ShouldBe(3);
        collapsed[0].SymbolId.ShouldBe("M:N.Caller.DoIt");
        collapsed[1].SymbolId.ShouldBe(Base);
        collapsed[2].SymbolId.ShouldBe("M:N.BillingRuleEntity.Delete");
        // Every other field of the relabelled step is preserved.
        collapsed[1].Kind.ShouldBe("invocation");
        collapsed[1].LoopKind.ShouldBe("for");
        collapsed[1].Fanout.ShouldBe(3);
        collapsed[1].DispatchBasis.ShouldBe("roslyn");
        collapsed[1].Line.ShouldBe(9);
    }

    [Test]
    public void CollapsePath_is_a_reference_equal_noop_without_mono()
    {
        var path = new[] { new PathStep("M:N.Caller.DoIt", "entry", "f.cs", 1), new PathStep("M:N.X.Y", "invocation", "f.cs", 2) };

        MonomorphCollapse.CollapsePath(path).ShouldBeSameAs(path);
    }

    // ---- CollapseTree -------------------------------------------------------------------------------

    [Test]
    public void CollapseTree_relabels_node_recursively_and_preserves_children_and_binding_fields()
    {
        var leaf = new TraceNode("M:N.BillingRuleEntity.Delete", "override-dispatch", null, null, Array.Empty<TraceNode>(), Fanout: 1);
        var monoNode = new TraceNode(
            MonoBilling,
            "invocation",
            null,
            null,
            new[] { leaf },
            DeclaringTypeArgBinding: null,
            MethodTypeArgBinding: "[\"C:N.BillingRuleEntity\",\"C:int\"]"
        );
        var root = new TraceNode("M:N.Caller.DoIt", "entry", null, null, new[] { monoNode });

        var collapsed = MonomorphCollapse.CollapseTree(new[] { root });

        var rootOut = collapsed.Single();
        rootOut.SymbolId.ShouldBe("M:N.Caller.DoIt");
        var monoOut = rootOut.Children.Single();
        // The instantiation node's id collapses to base; its precise child subtree is preserved.
        monoOut.SymbolId.ShouldBe(Base);
        monoOut.Children.Single().SymbolId.ShouldBe("M:N.BillingRuleEntity.Delete");
        // The binding render-label fields are UNTOUCHED (they keep the SaveServices<BillingRule> render).
        monoOut.MethodTypeArgBinding.ShouldBe("[\"C:N.BillingRuleEntity\",\"C:int\"]");
    }

    [Test]
    public void CollapseTree_does_not_merge_sibling_instantiation_subtrees()
    {
        var billing = new TraceNode(MonoBilling, "invocation", null, null, Array.Empty<TraceNode>());
        var contact = new TraceNode(MonoContact, "invocation", null, null, Array.Empty<TraceNode>());
        var root = new TraceNode("M:N.Caller.DoIt", "entry", null, null, new[] { billing, contact });

        var collapsed = MonomorphCollapse.CollapseTree(new[] { root });

        // Both siblings collapse to the SAME base id but remain DISTINCT subtree nodes (no merge).
        var children = collapsed.Single().Children;
        children.Count.ShouldBe(2);
        children[0].SymbolId.ShouldBe(Base);
        children[1].SymbolId.ShouldBe(Base);
    }

    [Test]
    public void CollapseTree_is_a_reference_equal_noop_without_mono()
    {
        var child = new TraceNode("M:N.X.Y", "invocation", null, null, Array.Empty<TraceNode>());
        var root = new TraceNode("M:N.Caller.DoIt", "entry", null, null, new[] { child });
        var forest = new[] { root };

        MonomorphCollapse.CollapseTree(forest).ShouldBeSameAs(forest);
    }

    // ---- END-TO-END over a real materialized graph (the load-bearing proof) -------------------------

    private const string EntityBase = "N.EntityBase";
    private const string BillingRule = "N.BillingRuleEntity";
    private const string Contact = "N.ContactEntity";
    private const string Company = "N.CompanyEntity";

    // Mirrors GenericMonomorphizerTests.MethodGenericGraph: SaveServices<TEntity,Tv> body calls EntityBase.Delete
    // through a type-param receiver; EntityBase has 3 overriders; Caller.DoIt binds TEntity=BillingRuleEntity.
    private static FactGraphData MethodGenericGraph()
    {
        var edges = new[]
        {
            new CallEdge(
                "M:N.Caller.DoIt",
                "M:N.Repo.SaveServices",
                "invocation",
                "f.cs",
                1,
                MethodTypeArgBinding: "[\"C:" + BillingRule + "\",\"C:int\"]"
            ),
            new CallEdge("M:N.Repo.SaveServices", "M:N.EntityBase.Delete", "invocation", "f.cs", 9, ReceiverType: "TEntity"),
        };
        var bases = new[]
        {
            new BaseEdge("T:" + BillingRule, "T:" + EntityBase),
            new BaseEdge("T:" + Contact, "T:" + EntityBase),
            new BaseEdge("T:" + Company, "T:" + EntityBase),
        };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.DoIt", "DoIt", "T:N.Caller"),
            new MethodRef("M:N.Repo.SaveServices", "SaveServices", "T:N.Repo"),
            new MethodRef("M:N.EntityBase.Delete", "Delete", "T:" + EntityBase),
            new MethodRef("M:N.BillingRuleEntity.Delete", "Delete", "T:" + BillingRule, IsOverride: true),
            new MethodRef("M:N.ContactEntity.Delete", "Delete", "T:" + Contact, IsOverride: true),
            new MethodRef("M:N.CompanyEntity.Delete", "Delete", "T:" + Company, IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.EntityBase.Delete", "M:N.BillingRuleEntity.Delete", "override"),
            new DispatchFact("M:N.EntityBase.Delete", "M:N.ContactEntity.Delete", "override"),
            new DispatchFact("M:N.EntityBase.Delete", "M:N.CompanyEntity.Delete", "override"),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);
    }

    private static IReadOnlyList<string> MethodGenericNames(string symbolId) =>
        symbolId == "M:N.Repo.SaveServices" ? new[] { "TEntity", "Tv" } : Array.Empty<string>();

    private static FactGraphData Materialized()
    {
        var graph = MethodGenericGraph();
        var inventory = GenericInstantiationInventory.Build(graph);
        return GenericMonomorphizer.Materialize(graph, inventory, MethodGenericNames);
    }

    [Test]
    public void EndToEnd_collapse_restores_the_base_id_that_was_only_present_as_mono()
    {
        var materialized = Materialized();
        var raw = FactPathFinder.ReachesWithFanout(materialized, "M:N.Caller.DoIt");

        // BEFORE collapse: the generic method is present ONLY as its `~mono` instantiation node, NOT as base.
        var instId = MonomorphizedNodeId.For("M:N.Repo.SaveServices", Array.Empty<string>(), new[] { BillingRule, "int" });
        raw.Keys.ShouldContain(instId);
        raw.Keys.ShouldNotContain("M:N.Repo.SaveServices");

        // AFTER collapse: the BASE id is now a key — so a base-keyed effect's EnclosingSymbolId joins.
        var collapsed = MonomorphCollapse.CollapseReachInfo(raw);
        collapsed.Keys.ShouldContain("M:N.Repo.SaveServices");
        collapsed.Keys.ShouldNotContain(instId);

        // (b) the narrowed concrete target is reachable; the decoy overrides are NOT.
        collapsed.Keys.ShouldContain("M:N.BillingRuleEntity.Delete");
        collapsed.Keys.ShouldNotContain("M:N.ContactEntity.Delete");
        collapsed.Keys.ShouldNotContain("M:N.CompanyEntity.Delete");

        // The restored join works: a base-keyed effect now finds its enclosing method.
        var effectEnclosing = "M:N.Repo.SaveServices";
        collapsed.ContainsKey(effectEnclosing).ShouldBeTrue();
    }

    [Test]
    public void EndToEnd_two_instantiations_fold_to_one_base_key()
    {
        // Add a SECOND caller binding TEntity=ContactEntity, so TWO distinct instantiation nodes of
        // SaveServices are materialized and reachable from DoIt.
        var graph = MethodGenericGraph();
        var edges = graph.CallEdges.ToList();
        edges.Add(
            new CallEdge(
                "M:N.Caller.DoIt",
                "M:N.Repo.SaveServices",
                "invocation",
                "f.cs",
                2,
                MethodTypeArgBinding: "[\"C:" + Contact + "\",\"C:int\"]"
            )
        );
        var twoCallers = graph with { CallEdges = edges };
        var inventory = GenericInstantiationInventory.Build(twoCallers);
        var materialized = GenericMonomorphizer.Materialize(twoCallers, inventory, MethodGenericNames);

        var raw = FactPathFinder.ReachesWithFanout(materialized, "M:N.Caller.DoIt");
        var billingInst = MonomorphizedNodeId.For("M:N.Repo.SaveServices", Array.Empty<string>(), new[] { BillingRule, "int" });
        var contactInst = MonomorphizedNodeId.For("M:N.Repo.SaveServices", Array.Empty<string>(), new[] { Contact, "int" });

        // BEFORE collapse: two DISTINCT `~mono` keys, no base key.
        raw.Keys.ShouldContain(billingInst);
        raw.Keys.ShouldContain(contactInst);
        raw.Keys.ShouldNotContain("M:N.Repo.SaveServices");

        var collapsed = MonomorphCollapse.CollapseReachInfo(raw);

        // AFTER collapse: the two instantiation keys fold to EXACTLY ONE base entry (no fragmentation).
        collapsed.Keys.Count(k => k == "M:N.Repo.SaveServices").ShouldBe(1);
        collapsed.Keys.ShouldContain("M:N.Repo.SaveServices");
        collapsed.Keys.ShouldNotContain(billingInst);
        collapsed.Keys.ShouldNotContain(contactInst);
    }

    [Test]
    public void EndToEnd_tree_collapses_instantiation_node_id_but_keeps_the_precise_subtree()
    {
        var materialized = Materialized();
        var raw = FactPathFinder.BuildTree(materialized, "M:N.Caller.DoIt");

        // BEFORE collapse the instantiation node carries the `~mono` id.
        var instId = MonomorphizedNodeId.For("M:N.Repo.SaveServices", Array.Empty<string>(), new[] { BillingRule, "int" });
        Flatten(raw).ShouldContain(n => n.SymbolId == instId);

        var collapsed = MonomorphCollapse.CollapseTree(raw);

        // The instantiation node's id collapsed to the BASE id; no raw `~mono` id leaked into the tree.
        var allNodes = Flatten(collapsed).ToList();
        allNodes.ShouldContain(n => n.SymbolId == "M:N.Repo.SaveServices");
        allNodes.ShouldNotContain(n => MonomorphizedNodeId.IsMonomorphized(n.SymbolId));

        // The PRECISE subtree under the collapsed node is preserved: SaveServices dispatches (one hop) to the
        // narrowed concrete override BillingRuleEntity.Delete; the decoy overrides are NOT in the subtree.
        var saveNode = allNodes.First(n => n.SymbolId == "M:N.Repo.SaveServices");
        var saveSubtree = Flatten(saveNode.Children).ToList();
        saveSubtree.ShouldContain(n => n.SymbolId == "M:N.BillingRuleEntity.Delete");
        saveSubtree.ShouldNotContain(n => n.SymbolId == "M:N.ContactEntity.Delete");
        saveSubtree.ShouldNotContain(n => n.SymbolId == "M:N.CompanyEntity.Delete");
    }

    private static IEnumerable<TraceNode> Flatten(IReadOnlyList<TraceNode> forest)
    {
        foreach (var node in forest)
        {
            yield return node;
            foreach (var d in Flatten(node.Children))
            {
                yield return d;
            }
        }
    }
}

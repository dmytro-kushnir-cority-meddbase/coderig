using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

// The generic monomorphization bindings (CallEdge.DeclaringTypeArgBinding / MethodTypeArgBinding) must be
// forwarded onto the node the edge reaches (TraceNode.*) so the renderer can substitute the label's
// placeholders. Pure plumbing — no narrowing semantics (dispatch still uses the open ReceiverType).
public sealed class ConcreteReceiverPropagationTests
{
    private static FactGraphData Graph(params CallEdge[] edges) =>
        new(edges, System.Array.Empty<ImplementsEdge>(), System.Array.Empty<MethodRef>(), System.Array.Empty<BaseEdge>());

    private static TraceNode Child(TraceNode root, string id) => root.Children.Single(c => c.SymbolId == id);

    [Test]
    public void A_direct_call_edges_bindings_land_on_the_reached_node()
    {
        var graph = Graph(
            new CallEdge(
                "M:N.Caller.Go",
                "M:N.QueryPipeline`2.Enumerate",
                "invocation",
                "f.cs",
                10,
                ReceiverType: "N.QueryPipeline<T, U>",
                DeclaringTypeArgBinding: """["C:N.Account","C:N.Invoice"]""",
                MethodTypeArgBinding: """["C:N.Row"]"""
            )
        );

        var root = FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single();
        var enumerate = Child(root, "M:N.QueryPipeline`2.Enumerate");

        enumerate.DeclaringTypeArgBinding.ShouldBe("""["C:N.Account","C:N.Invoice"]""");
        enumerate.MethodTypeArgBinding.ShouldBe("""["C:N.Row"]""");
    }

    [Test]
    public void A_node_reached_by_an_edge_with_no_bindings_has_null_bindings()
    {
        var graph = Graph(new CallEdge("M:N.Caller.Go", "M:N.Plain.Do", "invocation", "f.cs", 10, ReceiverType: "N.Plain"));

        var root = FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single();

        Child(root, "M:N.Plain.Do").DeclaringTypeArgBinding.ShouldBeNull();
        // The root itself was reached by no edge.
        root.DeclaringTypeArgBinding.ShouldBeNull();
    }

    // A `from` pattern that matches a method ALSO matches its synthetic inline lambdas (`…~λN`, whose ids
    // embed the method name). Those lambdas are sub-parts, not independent roots: re-rooting them emitted a
    // spurious top-level `↺seen` line (bug: tree-spurious-seen-footer-for-lambdas). BuildTree must return a
    // single root — the method — with the lambda rendered inline under it, not as a second Truncated root.
    [Test]
    public void Inline_lambda_whose_container_also_matches_is_not_a_separate_root()
    {
        var graph = Graph(
            new CallEdge("M:N.Page.Action", "M:N.Page.Action~λ0", "methodGroup", "f.cs", 10),
            new CallEdge("M:N.Page.Action~λ0", "M:N.Dep.Use", "invocation", "f.cs", 11)
        );

        var roots = FactPathFinder.BuildTree(graph, "Action");

        roots.Count.ShouldBe(1);
        roots[0].SymbolId.ShouldBe("M:N.Page.Action");
        roots[0].Children.ShouldContain(c => c.SymbolId == "M:N.Page.Action~λ0" && !c.Truncated);
    }

    // A lambda whose CONTAINER is not matched (e.g. a promoted async-handoff entry point targeted directly)
    // is still a legitimate root — the fix must only drop lambdas whose container also matched.
    [Test]
    public void Lambda_whose_container_is_not_matched_stays_a_root()
    {
        var graph = Graph(new CallEdge("M:N.Other.Handler~λ0", "M:N.Dep.Use", "invocation", "f.cs", 10));

        var roots = FactPathFinder.BuildTree(graph, "Handler~λ0");

        roots.Count.ShouldBe(1);
        roots[0].SymbolId.ShouldBe("M:N.Other.Handler~λ0");
    }
}

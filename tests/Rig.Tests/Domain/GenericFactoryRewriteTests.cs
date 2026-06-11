using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Unit tests for FactPathFinder.RewriteGenericFactories — generic-factory monomorphization at the
// call edge. A call to a factory (e.g. Entity.New<Account,…>) with a CONCRETE construct type arg is
// rewritten to point straight at the construct's method (Account.New), so the traversal skips the
// generic plumbing the factory forwards through. Pure FactGraphData -> FactGraphData.
public sealed class GenericFactoryRewriteTests
{
    // The factory + its plumbing, plus two constructs (Account, Company) each with New overloads.
    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Entity.New``3(``1)", "New", "T:N.Entity"),
            new MethodRef("M:N.Account.New(System.Int32)", "New", "T:N.Account"),
            new MethodRef("M:N.Account.New(System.Int32,N.ITxn)", "New", "T:N.Account"),
            new MethodRef("M:N.Company.New(System.Int32)", "New", "T:N.Company"),
        };
        return new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods);
    }

    private static readonly FactGenericFactoryRule Rule = new("N.Entity.New", ConstructArgIndex: 0, TargetMethod: "New");

    private static IReadOnlyList<string> Callees(FactGraphData g, string caller) =>
        g.CallEdges.Where(e => e.Caller == caller).Select(e => e.Callee).ToList();

    [Fact]
    public void Concrete_factory_call_is_rewritten_to_the_construct_method()
    {
        var g = Graph(
            new CallEdge(
                "M:N.Caller.Go",
                "M:N.Entity.New``3(``1)",
                "invocation",
                "f.cs",
                1,
                TypeArguments: "N.Account,System.Int32,N.AccountRecord"
            )
        );

        var rewritten = FactPathFinder.RewriteGenericFactories(g, [Rule]);

        var callees = Callees(rewritten, "M:N.Caller.Go");
        callees.ShouldContain("M:N.Account.New(System.Int32)"); // arity-1 New on the construct
        callees.ShouldNotContain("M:N.Entity.New``3(``1)"); // factory edge replaced
        callees.ShouldNotContain("M:N.Account.New(System.Int32,N.ITxn)"); // arity 2 — not matched
        callees.ShouldNotContain("M:N.Company.New(System.Int32)"); // wrong construct
    }

    [Fact]
    public void Forwarded_type_parameter_is_left_intact_for_the_in_memory_fallback()
    {
        // Inside a generic helper the type arg is a bare parameter token (no '.') — nothing to resolve.
        var g = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New``3(``1)", "invocation", "f.cs", 1, TypeArguments: "TConstruct,TPk,TRecord")
        );

        var rewritten = FactPathFinder.RewriteGenericFactories(g, [Rule]);

        Callees(rewritten, "M:N.Caller.Go").ShouldBe(["M:N.Entity.New``3(``1)"]); // unchanged
    }

    [Fact]
    public void Non_factory_edges_are_untouched()
    {
        var g = Graph(new CallEdge("M:N.Caller.Go", "M:N.Account.New(System.Int32)", "invocation", "f.cs", 1));

        var rewritten = FactPathFinder.RewriteGenericFactories(g, [Rule]);

        Callees(rewritten, "M:N.Caller.Go").ShouldBe(["M:N.Account.New(System.Int32)"]);
    }

    [Fact]
    public void Unresolvable_construct_keeps_the_edge()
    {
        // Concrete-looking but not a type with the target method in the graph -> keep the edge (sound).
        var g = Graph(
            new CallEdge(
                "M:N.Caller.Go",
                "M:N.Entity.New``3(``1)",
                "invocation",
                "f.cs",
                1,
                TypeArguments: "N.Unknown,System.Int32,N.UnknownRecord"
            )
        );

        var rewritten = FactPathFinder.RewriteGenericFactories(g, [Rule]);

        Callees(rewritten, "M:N.Caller.Go").ShouldBe(["M:N.Entity.New``3(``1)"]);
    }

    [Fact]
    public void No_rules_returns_the_same_graph()
    {
        var g = Graph(
            new CallEdge(
                "M:N.Caller.Go",
                "M:N.Entity.New``3(``1)",
                "invocation",
                "f.cs",
                1,
                TypeArguments: "N.Account,System.Int32,N.AccountRecord"
            )
        );

        FactPathFinder.RewriteGenericFactories(g, []).ShouldBeSameAs(g);
    }

    [Fact]
    public void Rewritten_edge_collapses_the_plumbing_in_the_tree()
    {
        // End-to-end: Caller -> Entity.New<Account> -> (plumbing) Construct.New. After rewrite the tree
        // goes Caller -> Account.New directly; the plumbing node is unreachable and absent.
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Entity.New``3(``1)", "New", "T:N.Entity"),
            new MethodRef("M:N.Construct.New", "New", "T:N.Construct"),
            new MethodRef("M:N.Account.New(System.Int32)", "New", "T:N.Account"),
        };
        var edges = new[]
        {
            new CallEdge(
                "M:N.Caller.Go",
                "M:N.Entity.New``3(``1)",
                "invocation",
                "f.cs",
                1,
                TypeArguments: "N.Account,System.Int32,N.AccountRecord"
            ),
            new CallEdge("M:N.Entity.New``3(``1)", "M:N.Construct.New", "invocation", "f.cs", 2), // plumbing
        };
        var g = new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods);

        var rewritten = FactPathFinder.RewriteGenericFactories(g, [Rule]);
        var root = FactPathFinder.BuildTree(rewritten, "M:N.Caller.Go").Single();

        var ids = new HashSet<string>(System.StringComparer.Ordinal);
        void Walk(TraceNode n)
        {
            ids.Add(n.SymbolId);
            foreach (var c in n.Children)
                Walk(c);
        }
        Walk(root);

        ids.ShouldContain("M:N.Account.New(System.Int32)");
        ids.ShouldNotContain("M:N.Entity.New``3(``1)"); // plumbing bypassed
        ids.ShouldNotContain("M:N.Construct.New"); // unreachable after rewrite
    }
}

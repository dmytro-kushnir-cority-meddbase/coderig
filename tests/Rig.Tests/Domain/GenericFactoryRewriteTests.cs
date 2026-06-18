using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class GenericFactoryRewriteTests
{
    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Entity.New``3(``1)", "New", "T:N.Entity"),
            new MethodRef("M:N.Account.New(System.Int32)", "New", "T:N.Account"),
            new MethodRef("M:N.Account.New(System.Guid)", "New", "T:N.Account"),
            new MethodRef("M:N.Account.New(System.Int32,N.ITxn)", "New", "T:N.Account"),
            new MethodRef("M:N.Company.New(System.Int32)", "New", "T:N.Company"),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods);
    }

    private static readonly FactGenericFactoryRule Rule = new("N.Entity.New", ConstructArgIndex: 0, TargetMethod: "New");

    private static IReadOnlyList<string> Callees(FactGraphData g, string caller) =>
        g.CallEdges.Where(e => e.Caller == caller).Select(e => e.Callee).ToList();

    [Test]
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
        callees.ShouldContain("M:N.Account.New(System.Int32)");
        callees.ShouldNotContain("M:N.Entity.New``3(``1)");
        callees.ShouldNotContain("M:N.Account.New(System.Guid)");
        callees.ShouldNotContain("M:N.Account.New(System.Int32,N.ITxn)");
        callees.ShouldNotContain("M:N.Company.New(System.Int32)");
    }

    [Test]
    public void Pk_type_disambiguates_same_arity_overloads()
    {
        var g = Graph(
            new CallEdge(
                "M:N.Caller.Go",
                "M:N.Entity.New``3(``1)",
                "invocation",
                "f.cs",
                1,
                TypeArguments: "N.Account,System.Guid,N.AccountRecord"
            )
        );

        var callees = Callees(FactPathFinder.RewriteGenericFactories(g, [Rule]), "M:N.Caller.Go");

        callees.ShouldBe(["M:N.Account.New(System.Guid)"]);
    }

    [Test]
    public void Pk_keyword_matches_the_bcl_parameter_type()
    {
        var g = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New``3(``1)", "invocation", "f.cs", 1, TypeArguments: "N.Account,int,N.AccountRecord")
        );

        var callees = Callees(FactPathFinder.RewriteGenericFactories(g, [Rule]), "M:N.Caller.Go");

        callees.ShouldBe(["M:N.Account.New(System.Int32)"]);
    }

    [Test]
    public void Forwarded_type_parameter_is_left_intact_for_the_in_memory_fallback()
    {
        var g = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New``3(``1)", "invocation", "f.cs", 1, TypeArguments: "TConstruct,TPk,TRecord")
        );

        var rewritten = FactPathFinder.RewriteGenericFactories(g, [Rule]);

        Callees(rewritten, "M:N.Caller.Go").ShouldBe(["M:N.Entity.New``3(``1)"]);
    }

    [Test]
    public void Non_factory_edges_are_untouched()
    {
        var g = Graph(new CallEdge("M:N.Caller.Go", "M:N.Account.New(System.Int32)", "invocation", "f.cs", 1));

        var rewritten = FactPathFinder.RewriteGenericFactories(g, [Rule]);

        Callees(rewritten, "M:N.Caller.Go").ShouldBe(["M:N.Account.New(System.Int32)"]);
    }

    [Test]
    public void Unresolvable_construct_keeps_the_edge()
    {
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

    [Test]
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

    [Test]
    public void Rewritten_edge_collapses_the_plumbing_in_the_tree()
    {
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
            new CallEdge("M:N.Entity.New``3(``1)", "M:N.Construct.New", "invocation", "f.cs", 2),
        };
        var g = new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods);

        var rewritten = FactPathFinder.RewriteGenericFactories(g, [Rule]);
        var root = FactPathFinder.BuildTree(rewritten, "M:N.Caller.Go").Single();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Walk(TraceNode n)
        {
            ids.Add(n.SymbolId);
            foreach (var c in n.Children)
            {
                Walk(c);
            }
        }
        Walk(root);

        ids.ShouldContain("M:N.Account.New(System.Int32)");
        ids.ShouldNotContain("M:N.Entity.New``3(``1)");
        ids.ShouldNotContain("M:N.Construct.New");
    }
}

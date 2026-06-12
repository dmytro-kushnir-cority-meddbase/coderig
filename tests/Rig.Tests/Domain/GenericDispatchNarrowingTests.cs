using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class GenericDispatchNarrowingTests
{
    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var bases = new[]
        {
            new BaseEdge("T:N.Account", "T:N.Construct"),
            new BaseEdge("T:N.Company", "T:N.Construct"),
            new BaseEdge("T:N.Profile", "T:N.Construct"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Entity.New", "New", "T:N.Entity"),
            new MethodRef("M:N.EntityCache.New", "New", "T:N.EntityCache"),
            new MethodRef("M:N.Construct.New", "New", "T:N.Construct"),
            new MethodRef("M:N.Account.New", "New", "T:N.Account", IsOverride: true),
            new MethodRef("M:N.Company.New", "New", "T:N.Company", IsOverride: true),
            new MethodRef("M:N.Profile.New", "New", "T:N.Profile", IsOverride: true),
        };
        return new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);
    }

    private static HashSet<string> Ids(TraceNode node)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        void Walk(TraceNode n)
        {
            set.Add(n.SymbolId);
            foreach (var c in n.Children)
                Walk(c);
        }
        Walk(node);
        return set;
    }

    [Test]
    public void Concrete_type_arg_narrows_the_generic_factory_to_the_matching_constructor()
    {
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Account,int,N.AccountRecord"),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Account.New");
        ids.ShouldNotContain("M:N.Company.New");
    }

    [Test]
    public void Binding_persists_through_a_forwarded_type_parameter_hop()
    {
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Account,int,N.AccountRecord"),
            new CallEdge("M:N.Entity.New", "M:N.EntityCache.New", "invocation", "f.cs", 20, TypeArguments: "TConstruct,TPk,TRecord"),
            new CallEdge("M:N.EntityCache.New", "M:N.Construct.New", "invocation", "f.cs", 30)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Account.New");
        ids.ShouldNotContain("M:N.Company.New");
    }

    [Test]
    public void No_binding_keeps_the_full_cha_fanout()
    {
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Account.New");
        ids.ShouldContain("M:N.Company.New");
    }

    [Test]
    public void Binding_matching_no_candidate_falls_back_to_full_cha()
    {
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Unrelated,int"),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Account.New");
        ids.ShouldContain("M:N.Company.New");
    }

    [Test]
    public void Sibling_paths_each_narrow_to_their_own_construct()
    {
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Account,int,N.AccountRecord"),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Account.New");
        ids.ShouldNotContain("M:N.Company.New");
    }

    [Test]
    public void Reaches_narrows_the_generic_hub_to_the_single_bound_construct()
    {
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Account,int,N.AccountRecord"),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.Account.New");
        reach.Keys.ShouldNotContain("M:N.Company.New");
        reach.Keys.ShouldNotContain("M:N.Profile.New");
    }

    [Test]
    public void Reaches_unions_bindings_across_paths_to_a_shared_hub_keeping_all_reachable_constructs()
    {
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Account,int,N.AccountRecord"),
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 11, TypeArguments: "N.Company,int,N.CompanyRecord"),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.Account.New");
        reach.Keys.ShouldContain("M:N.Company.New");
        reach.Keys.ShouldNotContain("M:N.Profile.New");
    }

    [Test]
    public void Reaches_with_no_binding_keeps_the_full_cha_closure()
    {
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.Account.New");
        reach.Keys.ShouldContain("M:N.Company.New");
        reach.Keys.ShouldContain("M:N.Profile.New");
    }
}

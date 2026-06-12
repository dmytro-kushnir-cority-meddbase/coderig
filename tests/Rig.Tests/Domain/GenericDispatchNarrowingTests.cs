using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Unit tests for GENERIC-DISPATCH narrowing (monomorphization) in FactPathFinder: a CHA-fanned generic
// factory hub (`Construct.New`, an abstract base with one override per entity) is narrowed to the ONE
// constructor whose declaring type matches a concrete type argument carried down the path from the
// generic entry (`Entity.New<Account,…>`). Mirrors the entity-cache chain
// AccountCache.New -> Entity.New``3 -> … -> Construct`2.New -> {43 entity ctors}. The carried binding
// threads parallel to receiver-type narrowing; the filter is recall-safe (never empties the set).
// Pure in-memory graph — no Roslyn, no SQLite.
public sealed class GenericDispatchNarrowingTests
{
    // Three entities (Account, Company, Profile) each override the abstract Construct.New, so a call to
    // the base Construct.New CHA-fans to all three — the over-approximation generic narrowing collapses.
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

    // All SymbolIds present anywhere in a tree (BuildTree builds the full tree; no render pruning).
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

    [Fact]
    public void Concrete_type_arg_narrows_the_generic_factory_to_the_matching_constructor()
    {
        // Caller.Go calls Entity.New<Account,int,AccountRecord>; Entity.New calls the abstract
        // Construct.New, which CHA-fans to Account.New + Company.New. The carried `N.Account` picks
        // Account.New and drops Company.New.
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Account,int,N.AccountRecord"),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Account.New"); // the bound construct
        ids.ShouldNotContain("M:N.Company.New"); // CHA over-approximation, narrowed away
    }

    [Fact]
    public void Binding_persists_through_a_forwarded_type_parameter_hop()
    {
        // Entity.New<Account,…> -> EntityCache.New<TConstruct,…> (bare type-PARAMETER token, adds no
        // concrete) -> Construct.New. The concrete `N.Account` seeded at the top must survive the
        // forwarding hop and still narrow the dispatch.
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Account,int,N.AccountRecord"),
            new CallEdge("M:N.Entity.New", "M:N.EntityCache.New", "invocation", "f.cs", 20, TypeArguments: "TConstruct,TPk,TRecord"),
            new CallEdge("M:N.EntityCache.New", "M:N.Construct.New", "invocation", "f.cs", 30)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Account.New");
        ids.ShouldNotContain("M:N.Company.New");
    }

    [Fact]
    public void No_binding_keeps_the_full_cha_fanout()
    {
        // No type args anywhere -> nothing to narrow by -> both overrides reached (unchanged behavior).
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Account.New");
        ids.ShouldContain("M:N.Company.New");
    }

    [Fact]
    public void Binding_matching_no_candidate_falls_back_to_full_cha()
    {
        // The carried concrete (`N.Unrelated`) is not the declaring type of any candidate, so the filter
        // would empty the set — recall-safe: keep the full CHA fan-out rather than drop real targets.
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Unrelated,int"),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Account.New");
        ids.ShouldContain("M:N.Company.New");
    }

    [Fact]
    public void Sibling_paths_each_narrow_to_their_own_construct()
    {
        // Two callers through the same factory with different type args. The tree expands the shared
        // Construct.New once (under the first caller), narrowed by THAT path's binding — so the first
        // caller sees its own construct and not the other. (Reaches-closure soundness across siblings is
        // B3; here we assert the per-path tree narrowing is correct for the expanded path.)
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Account,int,N.AccountRecord"),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Caller.Go").Single());

        ids.ShouldContain("M:N.Account.New");
        ids.ShouldNotContain("M:N.Company.New");
    }

    // --- Reaches closure (B3): union-fixpoint at the shared generic hub ---

    [Fact]
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

    [Fact]
    public void Reaches_unions_bindings_across_paths_to_a_shared_hub_keeping_all_reachable_constructs()
    {
        // Caller reaches the SAME generic factory twice with different type args (Account, Company). The
        // shared Construct.New hub must end up narrowed to BOTH constructs — a first-path-wins narrowing
        // would unsoundly keep only one. Profile (never a carried type arg) stays excluded.
        var graph = Graph(
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 10, TypeArguments: "N.Account,int,N.AccountRecord"),
            new CallEdge("M:N.Caller.Go", "M:N.Entity.New", "invocation", "f.cs", 11, TypeArguments: "N.Company,int,N.CompanyRecord"),
            new CallEdge("M:N.Entity.New", "M:N.Construct.New", "invocation", "f.cs", 20)
        );

        var reach = FactPathFinder.Reaches(graph, "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.Account.New"); // path A's construct
        reach.Keys.ShouldContain("M:N.Company.New"); // path B's construct — union soundness
        reach.Keys.ShouldNotContain("M:N.Profile.New"); // no path carried Profile
    }

    [Fact]
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

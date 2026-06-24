using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Phase 2 of static monomorphization (docs/design-dispatch-precision.md): GenericMonomorphizer.Materialize
// clones+substitutes the body of each reachable generic instantiation into a DISTINCT node, so the EXISTING
// FactPathFinder dispatch-narrowing (driven by CallEdge.ReceiverType) resolves the concrete override instead
// of CHA-fanning to every base-type override. Pure over synthetic FactGraphData (mirrors OneHopDispatchTests
// / ReverseReceiverNarrowingTests construction). Receiver types use the bare display-FQN convention those
// tests use (e.g. "N.Dog"), since that is what FactPathFinder.ReceiverToStrippedTypeId expects.
public sealed class GenericMonomorphizerTests
{
    // ---- MonomorphizedNodeId round-trip (Unit B) ----------------------------------------------------

    [Test]
    public void NodeId_round_trips_base_and_is_recognized()
    {
        var id = MonomorphizedNodeId.For("M:N.Repo.SaveServices", new[] { "N.Account" }, new[] { "N.BillingRuleEntity", "int" });

        MonomorphizedNodeId.IsMonomorphized(id).ShouldBeTrue();
        MonomorphizedNodeId.BaseOf(id).ShouldBe("M:N.Repo.SaveServices");
    }

    [Test]
    public void NodeId_is_not_monomorphized_for_a_plain_id_and_BaseOf_is_identity()
    {
        MonomorphizedNodeId.IsMonomorphized("M:N.Repo.SaveServices").ShouldBeFalse();
        MonomorphizedNodeId.BaseOf("M:N.Repo.SaveServices").ShouldBe("M:N.Repo.SaveServices");
    }

    [Test]
    public void NodeId_distinct_bindings_yield_distinct_ids_same_binding_yields_same_id()
    {
        var a = MonomorphizedNodeId.For("M:N.Repo.M", Array.Empty<string>(), new[] { "N.A" });
        var b = MonomorphizedNodeId.For("M:N.Repo.M", Array.Empty<string>(), new[] { "N.B" });
        var aAgain = MonomorphizedNodeId.For("M:N.Repo.M", Array.Empty<string>(), new[] { "N.A" });

        a.ShouldNotBe(b);
        a.ShouldBe(aAgain);
    }

    // ---- Method-generic scenario (the load-bearing narrowing test) ----------------------------------

    private const string EntityBase = "N.EntityBase";
    private const string BillingRule = "N.BillingRuleEntity";
    private const string Contact = "N.ContactEntity";
    private const string Company = "N.CompanyEntity";

    // A generic method `SaveServices<TEntity, Tv>` whose body has ONE call edge into the overridable
    // `EntityBase.Delete` with a type-param receiver ("TEntity"). EntityBase has 3 concrete overriders, so a
    // base-typed-receiver CHA fan reaches ALL of them. A caller `Caller.DoIt` invokes SaveServices with the
    // concrete method binding TEntity=BillingRuleEntity (Tv=int). The base method `Save` body edge keeps the
    // type-param receiver (it stays CHA for any un-monomorphized caller).
    private static FactGraphData MethodGenericGraph()
    {
        var edges = new[]
        {
            // Caller -> generic method, with the concrete method type-arg binding.
            new CallEdge(
                "M:N.Caller.DoIt",
                "M:N.Repo.SaveServices",
                "invocation",
                "f.cs",
                1,
                MethodTypeArgBinding: "[\"C:" + BillingRule + "\",\"C:int\"]"
            ),
            // Generic method body: a virtual call into EntityBase.Delete whose receiver is the type param.
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

    // typeParamNamesFor for the method-generic scenario: the generic method's two method type-params.
    private static IReadOnlyList<string> MethodGenericNames(string symbolId) =>
        symbolId == "M:N.Repo.SaveServices" ? new[] { "TEntity", "Tv" } : Array.Empty<string>();

    private static FactGraphData Materialized(FactGraphData graph, Func<string, IReadOnlyList<string>> names)
    {
        var inventory = GenericInstantiationInventory.Build(graph);
        return GenericMonomorphizer.Materialize(graph, inventory, names);
    }

    [Test]
    public void Original_graph_CHA_fans_to_all_decoy_overrides()
    {
        // Contrast baseline: on the un-materialized graph the base-typed receiver fans to ALL overrides.
        var reach = FactPathFinder.Reaches(MethodGenericGraph(), "M:N.Caller.DoIt");

        reach.Keys.ShouldContain("M:N.BillingRuleEntity.Delete");
        reach.Keys.ShouldContain("M:N.ContactEntity.Delete");
        reach.Keys.ShouldContain("M:N.CompanyEntity.Delete");
    }

    [Test]
    public void Materialized_graph_narrows_to_the_concrete_override_only()
    {
        var materialized = Materialized(MethodGenericGraph(), MethodGenericNames);

        var reach = FactPathFinder.Reaches(materialized, "M:N.Caller.DoIt");

        // The monomorphized body's receiver is now concrete BillingRuleEntity -> only its override is reached.
        reach.Keys.ShouldContain("M:N.BillingRuleEntity.Delete");
        reach.Keys.ShouldNotContain("M:N.ContactEntity.Delete");
        reach.Keys.ShouldNotContain("M:N.CompanyEntity.Delete");
    }

    [Test]
    public void Materialized_forward_and_reverse_agree()
    {
        var materialized = Materialized(MethodGenericGraph(), MethodGenericNames);

        // Reverse: callers of the concrete override include the seam (the instantiation node) and DoIt.
        var callersOfBilling = FactPathFinder.ReachedBy(materialized, "M:N.BillingRuleEntity.Delete");
        callersOfBilling.Keys.ShouldContain("M:N.Caller.DoIt");

        // And the decoys are NOT reverse-reached by this seam (forward ≡ reverse on the materialized graph).
        var callersOfContact = FactPathFinder.ReachedBy(materialized, "M:N.ContactEntity.Delete");
        callersOfContact.Keys.ShouldNotContain("M:N.Caller.DoIt");
    }

    [Test]
    public void Materialize_mechanics_clone_substitute_redirect_and_keep_base()
    {
        var graph = MethodGenericGraph();
        var inventory = GenericInstantiationInventory.Build(graph);
        var instId = MonomorphizedNodeId.For("M:N.Repo.SaveServices", Array.Empty<string>(), new[] { BillingRule, "int" });

        var materialized = GenericMonomorphizer.Materialize(graph, inventory, MethodGenericNames);

        // 1. The instantiation node exists, with its Delete body edge's receiver substituted to concrete.
        var instBody = materialized.CallEdges.Where(e => e.Caller == instId).ToList();
        instBody.Count.ShouldBe(1);
        instBody[0].Callee.ShouldBe("M:N.EntityBase.Delete");
        instBody[0].ReceiverType.ShouldBe(BillingRule);

        // 2. The incoming Caller -> SaveServices edge was REDIRECTED to the instId; the original is GONE.
        materialized.CallEdges.ShouldContain(e => e.Caller == "M:N.Caller.DoIt" && e.Callee == instId);
        materialized.CallEdges.ShouldNotContain(e => e.Caller == "M:N.Caller.DoIt" && e.Callee == "M:N.Repo.SaveServices");

        // 3. The base generic method's body edge is STILL present (un-monomorphized callers stay CHA).
        materialized.CallEdges.ShouldContain(e =>
            e.Caller == "M:N.Repo.SaveServices" && e.Callee == "M:N.EntityBase.Delete" && e.ReceiverType == "TEntity"
        );

        // Other fields pass through unchanged.
        materialized.Methods.ShouldBe(graph.Methods);
        materialized.MinedDispatch.ShouldBe(graph.MinedDispatch);
    }

    // ---- Declaring-type-generic scenario (the declaring arm) ----------------------------------------

    private const string AccountRepoBase = "N.RepositoryBase";
    private const string Account = "N.Account";
    private const string Invoice = "N.Invoice";

    // A method `Repository<TKey>.Load()` whose body receiver is the DECLARING-type param "TKey", calling an
    // overridable `RepositoryBase.Fetch`. Account/Invoice both override Fetch. A caller instantiates the
    // declaring type with TKey=Account via DeclaringTypeArgBinding.
    private static FactGraphData DeclaringGenericGraph()
    {
        var edges = new[]
        {
            new CallEdge(
                "M:N.Caller.DoIt",
                "M:N.Repository.Load",
                "invocation",
                "f.cs",
                1,
                DeclaringTypeArgBinding: "[\"C:" + Account + "\"]"
            ),
            new CallEdge("M:N.Repository.Load", "M:N.RepositoryBase.Fetch", "invocation", "f.cs", 9, ReceiverType: "TKey"),
        };
        var bases = new[] { new BaseEdge("T:" + Account, "T:" + AccountRepoBase), new BaseEdge("T:" + Invoice, "T:" + AccountRepoBase) };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.DoIt", "DoIt", "T:N.Caller"),
            new MethodRef("M:N.Repository.Load", "Load", "T:N.Repository"),
            new MethodRef("M:N.RepositoryBase.Fetch", "Fetch", "T:" + AccountRepoBase),
            new MethodRef("M:N.Account.Fetch", "Fetch", "T:" + Account, IsOverride: true),
            new MethodRef("M:N.Invoice.Fetch", "Fetch", "T:" + Invoice, IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.RepositoryBase.Fetch", "M:N.Account.Fetch", "override"),
            new DispatchFact("M:N.RepositoryBase.Fetch", "M:N.Invoice.Fetch", "override"),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);
    }

    // The declaring-type "Repository" has one type-param TKey; the method "Load" is non-generic.
    private static IReadOnlyList<string> DeclaringGenericNames(string symbolId) =>
        symbolId == "T:N.Repository" ? new[] { "TKey" } : Array.Empty<string>();

    [Test]
    public void Declaring_type_generic_narrows_through_the_declaring_arm()
    {
        var graph = DeclaringGenericGraph();

        // Baseline: CHA fans to both overrides on the un-materialized graph.
        var before = FactPathFinder.Reaches(graph, "M:N.Caller.DoIt");
        before.Keys.ShouldContain("M:N.Account.Fetch");
        before.Keys.ShouldContain("M:N.Invoice.Fetch");

        // After materialization, the TKey receiver substitutes to Account -> only Account.Fetch is reached.
        var materialized = Materialized(graph, DeclaringGenericNames);
        var after = FactPathFinder.Reaches(materialized, "M:N.Caller.DoIt");
        after.Keys.ShouldContain("M:N.Account.Fetch");
        after.Keys.ShouldNotContain("M:N.Invoice.Fetch");
    }
}

using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// `GenericInstantiationInventory.Build` — Phase 1 of static monomorphization: the DIRECT, concrete-only,
// capped inventory of reachable generic-method instantiations. Pure over synthetic FactGraphData (mirrors
// DispatchFanReportTests construction). Bindings use the REAL `["C:Type",...]` JSON form. No graph mutation.
public sealed class GenericInstantiationInventoryTests
{
    private const string EntityA = "MedDBase.DataAccessTier.EntityClasses.BillingRuleEntity";
    private const string EntityB = "MedDBase.DataAccessTier.EntityClasses.DebtorEntity";

    private static FactGraphData GraphOf(params CallEdge[] edges) => new(edges, Array.Empty<ImplementsEdge>(), Array.Empty<MethodRef>());

    private static CallEdge MethodGenericEdge(string caller, string callee, string methodBinding, int line = 1) =>
        new(caller, callee, "invocation", "f.cs", line, MethodTypeArgBinding: methodBinding);

    private static CallEdge DeclaringGenericEdge(string caller, string callee, string declaringBinding, int line = 1) =>
        new(caller, callee, "invocation", "f.cs", line, DeclaringTypeArgBinding: declaringBinding);

    [Test]
    public void IsFullyConcrete_true_only_when_every_element_is_C()
    {
        GenericSubstitution.IsFullyConcrete("[\"C:A\",\"C:int\"]").ShouldBeTrue();
    }

    [Test]
    public void IsFullyConcrete_false_for_forwarded_or_unresolved_or_invalid()
    {
        GenericSubstitution.IsFullyConcrete("[\"C:A\",\"M:0\"]").ShouldBeFalse();
        GenericSubstitution.IsFullyConcrete("[\"T:X\"]").ShouldBeFalse();
        GenericSubstitution.IsFullyConcrete("[\"?:_\"]").ShouldBeFalse();
        GenericSubstitution.IsFullyConcrete(null).ShouldBeFalse();
        GenericSubstitution.IsFullyConcrete("").ShouldBeFalse();
        GenericSubstitution.IsFullyConcrete("junk").ShouldBeFalse();
    }

    [Test]
    public void IsFullyConcrete_false_for_empty_array()
    {
        GenericSubstitution.IsFullyConcrete("[]").ShouldBeFalse();
    }

    [Test]
    public void A_concrete_method_generic_edge_yields_one_instantiation_with_parsed_method_binding()
    {
        var graph = GraphOf(
            MethodGenericEdge(caller: "M:N.Caller.Go", callee: "M:N.Repo.SaveServices", methodBinding: "[\"C:" + EntityA + "\",\"C:int\"]")
        );

        var result = GenericInstantiationInventory.Build(graph);

        result.CappedMethods.ShouldBeEmpty();
        result.Instantiations.Count.ShouldBe(1);
        var inst = result.Instantiations[0];
        inst.MethodId.ShouldBe("M:N.Repo.SaveServices");
        inst.MethodBinding.ShouldBe(new[] { EntityA, "int" });
        inst.DeclaringBinding.ShouldBeEmpty();
    }

    [Test]
    public void A_concrete_declaring_generic_edge_records_the_declaring_binding()
    {
        var graph = GraphOf(
            DeclaringGenericEdge(
                caller: "M:N.Caller.Go",
                callee: "M:N.Construct.New",
                declaringBinding: "[\"C:" + EntityA + "\",\"C:int\"]"
            )
        );

        var result = GenericInstantiationInventory.Build(graph);

        result.Instantiations.Count.ShouldBe(1);
        var inst = result.Instantiations[0];
        inst.MethodId.ShouldBe("M:N.Construct.New");
        inst.DeclaringBinding.ShouldBe(new[] { EntityA, "int" });
        inst.MethodBinding.ShouldBeEmpty();
    }

    [Test]
    public void A_forwarded_binding_edge_is_not_in_the_inventory()
    {
        var graph = GraphOf(
            MethodGenericEdge(caller: "M:N.Caller.Go", callee: "M:N.Repo.SaveServices", methodBinding: "[\"M:0\"]"),
            DeclaringGenericEdge(caller: "M:N.Caller.Go2", callee: "M:N.Construct.New", declaringBinding: "[\"T:X\"]")
        );

        var result = GenericInstantiationInventory.Build(graph);

        result.Instantiations.ShouldBeEmpty();
        result.CappedMethods.ShouldBeEmpty();
    }

    [Test]
    public void An_edge_with_no_bindings_is_skipped()
    {
        var graph = GraphOf(new CallEdge("M:N.Caller.Go", "M:N.Plain.Method", "invocation", "f.cs", 1));

        GenericInstantiationInventory.Build(graph).Instantiations.ShouldBeEmpty();
    }

    [Test]
    public void Same_instantiation_from_two_call_sites_is_deduped_to_one()
    {
        var binding = "[\"C:" + EntityA + "\",\"C:int\"]";
        var graph = GraphOf(
            MethodGenericEdge(caller: "M:N.A.Go", callee: "M:N.Repo.SaveServices", methodBinding: binding, line: 1),
            MethodGenericEdge(caller: "M:N.B.Go", callee: "M:N.Repo.SaveServices", methodBinding: binding, line: 2)
        );

        GenericInstantiationInventory.Build(graph).Instantiations.Count.ShouldBe(1);
    }

    [Test]
    public void Two_different_concrete_instantiations_of_the_same_method_yield_two_entries()
    {
        var graph = GraphOf(
            MethodGenericEdge(caller: "M:N.A.Go", callee: "M:N.Repo.SaveServices", methodBinding: "[\"C:" + EntityA + "\"]"),
            MethodGenericEdge(caller: "M:N.B.Go", callee: "M:N.Repo.SaveServices", methodBinding: "[\"C:" + EntityB + "\"]")
        );

        var result = GenericInstantiationInventory.Build(graph);

        result.Instantiations.Count.ShouldBe(2);
        result.Instantiations.Select(i => i.MethodBinding[0]).ShouldBe(new[] { EntityA, EntityB });
    }

    [Test]
    public void A_method_over_the_per_method_cap_is_capped_and_contributes_zero_instantiations()
    {
        // maxPerMethod = 3; create 4 distinct instantiations of one method -> capped, zero contributed.
        var edges = Enumerable
            .Range(0, 4)
            .Select(i =>
                MethodGenericEdge(
                    caller: "M:N.Caller.Go" + i,
                    callee: "M:N.Repo.SaveServices",
                    methodBinding: "[\"C:Entity" + i + "\"]",
                    line: i + 1
                )
            )
            .ToArray();

        var result = GenericInstantiationInventory.Build(GraphOf(edges), maxPerMethod: 3);

        result.Instantiations.ShouldBeEmpty();
        result.CappedMethods.ShouldBe(new[] { "M:N.Repo.SaveServices" });
    }

    [Test]
    public void A_method_at_exactly_the_per_method_cap_is_kept()
    {
        var edges = Enumerable
            .Range(0, 3)
            .Select(i =>
                MethodGenericEdge(
                    caller: "M:N.Caller.Go" + i,
                    callee: "M:N.Repo.SaveServices",
                    methodBinding: "[\"C:Entity" + i + "\"]",
                    line: i + 1
                )
            )
            .ToArray();

        var result = GenericInstantiationInventory.Build(GraphOf(edges), maxPerMethod: 3);

        result.CappedMethods.ShouldBeEmpty();
        result.Instantiations.Count.ShouldBe(3);
    }

    [Test]
    public void Two_Build_runs_on_the_same_graph_produce_equal_ordered_output()
    {
        var graph = GraphOf(
            MethodGenericEdge(caller: "M:N.A.Go", callee: "M:N.Z.Method", methodBinding: "[\"C:" + EntityB + "\"]"),
            MethodGenericEdge(caller: "M:N.B.Go", callee: "M:N.A.Method", methodBinding: "[\"C:" + EntityA + "\"]"),
            DeclaringGenericEdge(caller: "M:N.C.Go", callee: "M:N.A.Method", declaringBinding: "[\"C:" + EntityB + "\"]")
        );

        var first = GenericInstantiationInventory.Build(graph);
        var second = GenericInstantiationInventory.Build(graph);

        first.Instantiations.Select(i => i.MethodId).ShouldBe(second.Instantiations.Select(i => i.MethodId));
        first
            .Instantiations.Select(i => i.MethodId)
            .ShouldBe(first.Instantiations.Select(i => i.MethodId).OrderBy(m => m, StringComparer.Ordinal));
    }
}

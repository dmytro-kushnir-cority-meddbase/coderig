using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// `GenericSubstitution` — the pure string/type substitution primitive for static monomorphization
// (Phase 0 of docs/design-dispatch-precision.md). All fixtures use the REAL fact-store formats captured
// from MedDBase's `BillingRuleHelper.SaveServices<TEntity, Tv>`. No graph, no Roslyn — strings only.
public sealed class GenericSubstitutionTests
{
    // The real `symbol_facts.Signature` for SaveServices<TEntity, Tv>. The method-name type-params are
    // `<TEntity, Tv>`; the parameter list ALSO contains `<TEntity>` (inside EntityCollectionBase<TEntity>)
    // which must NOT be picked up.
    private const string SaveServicesSignature =
        "MedDBase.ServiceLayer.ChargeBand.BillingRules.BillingRuleHelper.SaveServices<TEntity, Tv>("
        + "SD.LLBLGen.Pro.ORMSupportClasses.EntityCollectionBase<TEntity>, "
        + "System.Func<SD.LLBLGen.Pro.ORMSupportClasses.EntityCollectionBase<TEntity>, Tv, "
        + "LanguageExt.Option<TEntity>>, System.Func<Tv, TEntity>, "
        + "System.Collections.Generic.IEnumerable<Tv>)";

    // The real `MethodTypeArgBinding` JSON for the call into SaveServices. `C:` = concrete kind marker.
    private const string SaveServicesBinding =
        "[\"C:MedDBase.DataAccessTier.EntityClasses.BillingRuleDebtorOverrideServiceIncludedEntity\",\"C:int\"]";

    private const string TEntityConcrete = "MedDBase.DataAccessTier.EntityClasses.BillingRuleDebtorOverrideServiceIncludedEntity";

    [Test]
    public void ParseTypeParameterNames_on_real_SaveServices_signature_returns_TEntity_and_Tv()
    {
        GenericSubstitution.ParseTypeParameterNames(SaveServicesSignature).ShouldBe(new[] { "TEntity", "Tv" });
    }

    [Test]
    public void ParseTypeParameterNames_on_non_generic_signature_is_empty()
    {
        GenericSubstitution.ParseTypeParameterNames("Ns.Foo.Bar(int, string)").ShouldBeEmpty();
    }

    [Test]
    public void ParseTypeParameterNames_does_not_pick_up_parameter_list_generics()
    {
        // The parameter list contains EntityCollectionBase<TEntity>, Func<…>, IEnumerable<Tv> — none of
        // those `<...>` groups must leak in. Exactly the 2 method-name type-params.
        var names = GenericSubstitution.ParseTypeParameterNames(SaveServicesSignature);

        names.Count.ShouldBe(2);
        names.ShouldBe(new[] { "TEntity", "Tv" });
    }

    [Test]
    public void ParseBinding_on_real_MethodTypeArgBinding_strips_C_prefix()
    {
        GenericSubstitution.ParseBinding(SaveServicesBinding).ShouldBe(new[] { TEntityConcrete, "int" });
    }

    [Test]
    public void ParseBinding_preserves_commas_inside_generic_args()
    {
        // A single element whose type contains commas inside generic args proves we JSON-parse, not split.
        GenericSubstitution
            .ParseBinding("[\"C:System.Collections.Generic.Dictionary<int,string>\"]")
            .ShouldBe(new[] { "System.Collections.Generic.Dictionary<int,string>" });
    }

    [Test]
    public void ParseBinding_on_null_or_invalid_is_empty()
    {
        GenericSubstitution.ParseBinding(null).ShouldBeEmpty();
        GenericSubstitution.ParseBinding("").ShouldBeEmpty();
        GenericSubstitution.ParseBinding("not json").ShouldBeEmpty();
    }

    [Test]
    public void Substitute_bare_TEntity_end_to_end_yields_concrete()
    {
        var names = GenericSubstitution.ParseTypeParameterNames(SaveServicesSignature);
        var binding = GenericSubstitution.ParseBinding(SaveServicesBinding);

        GenericSubstitution.Substitute(receiverType: "TEntity", typeParameterNames: names, binding: binding).ShouldBe(TEntityConcrete);
    }

    [Test]
    public void Substitute_nested_generic_rewrites_only_the_type_param()
    {
        var names = GenericSubstitution.ParseTypeParameterNames(SaveServicesSignature);
        var binding = GenericSubstitution.ParseBinding(SaveServicesBinding);

        GenericSubstitution
            .Substitute(receiverType: "System.Collections.Generic.List<TEntity>", typeParameterNames: names, binding: binding)
            .ShouldBe("System.Collections.Generic.List<" + TEntityConcrete + ">");
    }

    [Test]
    public void Substitute_array_rewrites_element_type()
    {
        var names = GenericSubstitution.ParseTypeParameterNames(SaveServicesSignature);
        var binding = GenericSubstitution.ParseBinding(SaveServicesBinding);

        GenericSubstitution
            .Substitute(receiverType: "TEntity[]", typeParameterNames: names, binding: binding)
            .ShouldBe(TEntityConcrete + "[]");
    }

    [Test]
    public void Substitute_second_param_Tv_yields_int()
    {
        var names = GenericSubstitution.ParseTypeParameterNames(SaveServicesSignature);
        var binding = GenericSubstitution.ParseBinding(SaveServicesBinding);

        GenericSubstitution.Substitute(receiverType: "Tv", typeParameterNames: names, binding: binding).ShouldBe("int");
    }

    [Test]
    public void Substitute_matches_whole_tokens_only()
    {
        var names = GenericSubstitution.ParseTypeParameterNames(SaveServicesSignature);
        var binding = GenericSubstitution.ParseBinding(SaveServicesBinding);

        // `TEntityCache` contains `TEntity` as a prefix but is a DIFFERENT identifier — unchanged.
        GenericSubstitution.Substitute(receiverType: "TEntityCache", typeParameterNames: names, binding: binding).ShouldBe("TEntityCache");

        // A concrete receiver mentioning no type-param is unchanged.
        GenericSubstitution
            .Substitute(receiverType: "MedDBase.SomeNamespace.SomeConcreteType", typeParameterNames: names, binding: binding)
            .ShouldBe("MedDBase.SomeNamespace.SomeConcreteType");
    }

    [Test]
    public void Substitute_arity_mismatch_leaves_unbound_token_unchanged()
    {
        // Two param names but only one binding entry: `Tv` (index 1) has no concrete -> left as-is, no throw.
        var names = new[] { "TEntity", "Tv" };
        var binding = new[] { TEntityConcrete };

        GenericSubstitution.Substitute(receiverType: "Tv", typeParameterNames: names, binding: binding).ShouldBe("Tv");

        // The in-range param still substitutes within the same call.
        GenericSubstitution.Substitute(receiverType: "TEntity", typeParameterNames: names, binding: binding).ShouldBe(TEntityConcrete);
    }
}

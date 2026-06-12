using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Unit tests for the indexed `type_argument` resource strategy (typeArgumentIndex): a generic
// FACTORY like `Entity.New<Account,int,AccountRecord>(pk)` is a direct call whose signature pins the
// constructed entity to type-arg position 0. typeArgumentIndex:0 resolves the effect to that one type
// AT THE CONCRETE CALL SITE (`entity_cache:read Account`) — not the CHA-fanned 43-entity aggregate
// reached through the cache machinery. Splitting is top-level only, so a tuple/generic arg in any
// position never mis-splits a position. Pure: hand-built invocations + a single rule.
public sealed class FirstTypeArgumentEffectTests
{
    private static FactInvocation Inv(string target, string? typeArgs) =>
        new(target, "M:MedDBase.Foo.Caller", "f.cs", 1, TypeArguments: typeArgs);

    // Mirrors the meddbase entity-factory rule: Entity.New / Entity.Find, declaring type the LLBLGen
    // Entity base, resource = the leading generic type argument (position 0).
    private static readonly FactEffectRule EntityFactoryRule = new(
        "entity_cache",
        "read",
        Methods: ["New", "Find"],
        DeclaringTypes: ["MedDBase.DataAccessTier.Entity"],
        ReceiverTypes: [],
        Resource: "type_argument",
        TypeArgumentIndex: 0
    );

    private const string AccountTarget = "M:MedDBase.DataAccessTier.Entity.New``3(``1)";

    [Fact]
    public void Entity_factory_resolves_to_the_leading_type_arg_at_the_call_site()
    {
        var inv = Inv(
            AccountTarget,
            "MedDBase.DataAccessTier.EntityInstances.Account,int,MedDBase.DataAccessTier.EntityClasses.AccountRecord"
        );

        var effect = FactEffectDeriver.Derive([inv], [EntityFactoryRule]).ShouldHaveSingleItem();

        effect.Provider.ShouldBe("entity_cache");
        effect.Operation.ShouldBe("read");
        effect.ResourceType.ShouldBe("MedDBase.DataAccessTier.EntityInstances.Account"); // just the entity, not the 3-arg combo
        effect.EnclosingSymbolId.ShouldBe("M:MedDBase.Foo.Caller");
    }

    [Fact]
    public void A_tuple_arg_in_a_later_position_does_not_mis_split_position_zero()
    {
        // QuestionnaireQuestionViaHash uses a tuple key: <Entity, (ChamberId, int), Record>. The
        // top-level split must ignore the comma inside the tuple parens.
        var inv = Inv(
            AccountTarget,
            "MedDBase.DataAccessTier.EntityInstances.QuestionnaireQuestion,(MedDBase.ChamberId, int),MedDBase.DataAccessTier.EntityRecords.QuestionnaireQuestionRecord"
        );

        FactEffectDeriver
            .Derive([inv], [EntityFactoryRule])
            .ShouldHaveSingleItem()
            .ResourceType.ShouldBe("MedDBase.DataAccessTier.EntityInstances.QuestionnaireQuestion");
    }

    [Fact]
    public void A_generic_leading_arg_is_kept_whole()
    {
        // If position 0 is itself generic (`Foo<A, B>`), the inner comma must not truncate it.
        var inv = Inv(AccountTarget, "MedDBase.Foo<MedDBase.A, MedDBase.B>,int");

        FactEffectDeriver
            .Derive([inv], [EntityFactoryRule])
            .ShouldHaveSingleItem()
            .ResourceType.ShouldBe("MedDBase.Foo<MedDBase.A, MedDBase.B>");
    }

    [Fact]
    public void No_type_args_drops_the_effect()
    {
        // A non-generic call to a same-named method: nothing to resolve -> no effect (not a blank one).
        FactEffectDeriver.Derive([Inv(AccountTarget, null)], [EntityFactoryRule]).ShouldBeEmpty();
    }

    [Fact]
    public void No_index_keeps_the_whole_combo()
    {
        // type_argument with no index = the full combo (the echo wrapper-contract behavior, unchanged).
        var rule = EntityFactoryRule with
        {
            TypeArgumentIndex = null,
        };
        var inv = Inv(AccountTarget, "App.Reply,App.Msg");

        FactEffectDeriver.Derive([inv], [rule]).ShouldHaveSingleItem().ResourceType.ShouldBe("App.Reply,App.Msg");
    }

    [Fact]
    public void A_non_zero_index_selects_that_position()
    {
        // Index 2 picks TRecord — proving the selector is positional, not hardwired to "first".
        var rule = EntityFactoryRule with
        {
            TypeArgumentIndex = 2,
        };
        var inv = Inv(
            AccountTarget,
            "MedDBase.DataAccessTier.EntityInstances.Account,int,MedDBase.DataAccessTier.EntityClasses.AccountRecord"
        );

        FactEffectDeriver
            .Derive([inv], [rule])
            .ShouldHaveSingleItem()
            .ResourceType.ShouldBe("MedDBase.DataAccessTier.EntityClasses.AccountRecord");
    }

    [Fact]
    public void An_out_of_range_index_drops_the_effect()
    {
        var rule = EntityFactoryRule with { TypeArgumentIndex = 5 };

        FactEffectDeriver.Derive([Inv(AccountTarget, "App.A,App.B")], [rule]).ShouldBeEmpty();
    }
}

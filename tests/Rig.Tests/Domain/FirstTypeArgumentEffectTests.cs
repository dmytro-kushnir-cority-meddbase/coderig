using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class FirstTypeArgumentEffectTests
{
    private static FactInvocation Inv(string target, string? typeArgs) =>
        new(target, "M:MedDBase.Foo.Caller", "f.cs", 1, TypeArguments: typeArgs);

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

    [Test]
    public void Entity_factory_resolves_to_the_leading_type_arg_at_the_call_site()
    {
        var inv = Inv(
            AccountTarget,
            "MedDBase.DataAccessTier.EntityInstances.Account,int,MedDBase.DataAccessTier.EntityClasses.AccountRecord"
        );

        var effect = FactEffectDeriver.Derive([inv], [EntityFactoryRule]).ShouldHaveSingleItem();

        effect.Provider.ShouldBe("entity_cache");
        effect.Operation.ShouldBe("read");
        effect.ResourceType.ShouldBe("MedDBase.DataAccessTier.EntityInstances.Account");
        effect.EnclosingSymbolId.ShouldBe("M:MedDBase.Foo.Caller");
    }

    [Test]
    public void A_tuple_arg_in_a_later_position_does_not_mis_split_position_zero()
    {
        var inv = Inv(
            AccountTarget,
            "MedDBase.DataAccessTier.EntityInstances.QuestionnaireQuestion,(MedDBase.ChamberId, int),MedDBase.DataAccessTier.EntityRecords.QuestionnaireQuestionRecord"
        );

        FactEffectDeriver
            .Derive([inv], [EntityFactoryRule])
            .ShouldHaveSingleItem()
            .ResourceType.ShouldBe("MedDBase.DataAccessTier.EntityInstances.QuestionnaireQuestion");
    }

    [Test]
    public void A_generic_leading_arg_is_kept_whole()
    {
        var inv = Inv(AccountTarget, "MedDBase.Foo<MedDBase.A, MedDBase.B>,int");

        FactEffectDeriver
            .Derive([inv], [EntityFactoryRule])
            .ShouldHaveSingleItem()
            .ResourceType.ShouldBe("MedDBase.Foo<MedDBase.A, MedDBase.B>");
    }

    [Test]
    public void No_type_args_drops_the_effect()
    {
        FactEffectDeriver.Derive([Inv(AccountTarget, null)], [EntityFactoryRule]).ShouldBeEmpty();
    }

    [Test]
    public void No_index_keeps_the_whole_combo()
    {
        var rule = EntityFactoryRule with { TypeArgumentIndex = null };
        var inv = Inv(AccountTarget, "App.Reply,App.Msg");

        FactEffectDeriver.Derive([inv], [rule]).ShouldHaveSingleItem().ResourceType.ShouldBe("App.Reply,App.Msg");
    }

    [Test]
    public void A_non_zero_index_selects_that_position()
    {
        var rule = EntityFactoryRule with { TypeArgumentIndex = 2 };
        var inv = Inv(
            AccountTarget,
            "MedDBase.DataAccessTier.EntityInstances.Account,int,MedDBase.DataAccessTier.EntityClasses.AccountRecord"
        );

        FactEffectDeriver
            .Derive([inv], [rule])
            .ShouldHaveSingleItem()
            .ResourceType.ShouldBe("MedDBase.DataAccessTier.EntityClasses.AccountRecord");
    }

    [Test]
    public void An_out_of_range_index_drops_the_effect()
    {
        var rule = EntityFactoryRule with { TypeArgumentIndex = 5 };

        FactEffectDeriver.Derive([Inv(AccountTarget, "App.A,App.B")], [rule]).ShouldBeEmpty();
    }
}

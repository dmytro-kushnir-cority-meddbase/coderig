using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// nth-argument resource resolution: a rule can point string_argument / argument_name at a positional
// argument past the first via ArgumentIndex, reading from the JSON ArgumentNames / ArgumentTemplates
// lists mined per call site. The motivating case is CertificateEntity.HasRight(cert, Rights.X.Y, txn),
// where the permission right is argument 1.
public sealed class NthArgumentEffectTests
{
    private static FactInvocation Inv(string target, string? argNames = null, string? argTemplates = null) =>
        new(target, "M:App.Caller", "f.cs", 1, ArgumentNames: argNames, ArgumentTemplates: argTemplates);

    private const string HasRightTarget =
        "M:MedDBase.DataAccessTier.MMSEntityClasses.CertificateEntity.HasRight(System.Guid,MedDBase.Configuration.Rights.Wrapper)";

    private static readonly FactEffectRule PermissionRule = new(
        "permission",
        "assert",
        Methods: ["HasRight"],
        DeclaringTypes: ["MedDBase.DataAccessTier.MMSEntityClasses.CertificateEntity"],
        ReceiverTypes: [],
        Resource: "argument_name",
        ArgumentIndex: 1
    );

    [Test]
    public void Argument_name_at_a_non_zero_index_resolves_to_that_argument()
    {
        var inv = Inv(HasRightTarget, argNames: """["cert","Rights.Account.CanViewAccounts","txn"]""");

        var effect = FactEffectDeriver.Derive([inv], [PermissionRule]).ShouldHaveSingleItem();

        effect.Provider.ShouldBe("permission");
        effect.Operation.ShouldBe("assert");
        effect.ResourceType.ShouldBe("Rights.Account.CanViewAccounts");
    }

    [Test]
    public void A_json_null_at_the_indexed_position_drops_the_effect()
    {
        // arg 1 is not a member/identifier (e.g. a composite `A | B` or a literal) -> JSON null -> no resource.
        var inv = Inv(HasRightTarget, argNames: """["cert",null,"txn"]""");

        FactEffectDeriver.Derive([inv], [PermissionRule]).ShouldBeEmpty();
    }

    [Test]
    public void An_out_of_range_index_drops_the_effect()
    {
        var inv = Inv(HasRightTarget, argNames: """["cert"]""");

        FactEffectDeriver.Derive([inv], [PermissionRule]).ShouldBeEmpty();
    }

    [Test]
    public void A_missing_argument_list_drops_the_effect()
    {
        FactEffectDeriver.Derive([Inv(HasRightTarget, argNames: null)], [PermissionRule]).ShouldBeEmpty();
    }

    // The reason the lists are JSON, not a comma-join: a string-literal argument can itself contain a
    // comma. A top-level-comma split would mis-segment ["a, b","seg"] into three elements; JSON keeps
    // element 0 == "a, b" intact and element 1 == "seg".
    [Test]
    public void A_string_argument_literal_containing_a_comma_is_not_mis_split()
    {
        var rule = PermissionRule with { Provider = "x", Operation = "y", Resource = "string_argument", ArgumentIndex = 1 };
        var inv = Inv(HasRightTarget, argTemplates: """["a, b","seg"]""");

        FactEffectDeriver.Derive([inv], [rule]).ShouldHaveSingleItem().ResourceType.ShouldBe("seg");

        var ruleZero = rule with { ArgumentIndex = 0 };
        FactEffectDeriver.Derive([inv], [ruleZero]).ShouldHaveSingleItem().ResourceType.ShouldBe("a, b");
    }

    // Index 0 via ArgumentIndex matches the unindexed first-argument fast path (FirstArgName/Template).
    [Test]
    public void Index_zero_matches_the_first_argument()
    {
        var inv = Inv(HasRightTarget, argNames: """["Rights.First.Thing","other"]""");
        var ruleZero = PermissionRule with { ArgumentIndex = 0 };

        FactEffectDeriver.Derive([inv], [ruleZero]).ShouldHaveSingleItem().ResourceType.ShouldBe("Rights.First.Thing");
    }
}

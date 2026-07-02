using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// string_argument_or_receiver resource resolution: the argument's string template when the call
// site has one, else the receiver/declaring type — never dropped. The motivating case is VS-C4:
// XmlDocument.Save(path) writes the FILE named by arg 0, so a literal path is the resource, but
// Save(Stream)/Save(XmlWriter) overloads and variable paths must keep the receiver-typed effect
// instead of vanishing (plain string_argument drops on a non-literal — the F1a http blind spot).
public sealed class StringArgumentOrReceiverEffectTests
{
    private const string SaveTarget = "M:System.Xml.XmlDocument.Save(System.String)";

    private static readonly FactEffectRule SaveRule = new(
        "io",
        "write",
        Methods: ["Save"],
        DeclaringTypes: ["System.Xml.XmlDocument"],
        ReceiverTypes: [],
        Resource: "string_argument_or_receiver"
    );

    private static FactInvocation Inv(string? firstArgTemplate = null, string? receiver = null, string? argTemplates = null) =>
        new(SaveTarget, "M:App.Caller", "f.cs", 1, Receiver: receiver, FirstArgTemplate: firstArgTemplate, ArgumentTemplates: argTemplates);

    [Test]
    public void A_literal_path_argument_is_the_resource()
    {
        var inv = Inv(firstArgTemplate: "exports/invoice.xml", receiver: "System.Xml.XmlDocument");

        var effect = FactEffectDeriver.Derive([inv], [SaveRule]).ShouldHaveSingleItem();

        effect.Provider.ShouldBe("io");
        effect.Operation.ShouldBe("write");
        effect.ResourceType.ShouldBe("exports/invoice.xml");
    }

    [Test]
    public void A_non_literal_argument_falls_back_to_the_receiver_type_not_a_drop()
    {
        // Save(pathVariable) or the Save(Stream)/Save(XmlWriter) overloads: no string template mined.
        var inv = Inv(firstArgTemplate: null, receiver: "System.Xml.XmlDocument");

        var effect = FactEffectDeriver.Derive([inv], [SaveRule]).ShouldHaveSingleItem();

        effect.ResourceType.ShouldBe("System.Xml.XmlDocument");
    }

    [Test]
    public void No_template_and_no_receiver_falls_back_to_the_declaring_type()
    {
        var effect = FactEffectDeriver.Derive([Inv()], [SaveRule]).ShouldHaveSingleItem();

        effect.ResourceType.ShouldBe("System.Xml.XmlDocument");
    }

    [Test]
    public void ArgumentIndex_selects_the_nth_template_with_the_same_fallback()
    {
        var rule = SaveRule with { ArgumentIndex = 1 };

        FactEffectDeriver
            .Derive([Inv(argTemplates: """["zero","one.xml"]""", receiver: "System.Xml.XmlDocument")], [rule])
            .ShouldHaveSingleItem()
            .ResourceType.ShouldBe("one.xml");
        // A JSON null / out-of-range position at the index falls back instead of dropping.
        FactEffectDeriver
            .Derive([Inv(argTemplates: """["zero",null]""", receiver: "System.Xml.XmlDocument")], [rule])
            .ShouldHaveSingleItem()
            .ResourceType.ShouldBe("System.Xml.XmlDocument");
    }
}

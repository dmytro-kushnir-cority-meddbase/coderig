using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

public sealed class WrapperEffectRuleTests
{
    private static FactInvocation Inv(string target, string enclosing, string? typeArgs = null) =>
        new(target, enclosing, "f.cs", 1, TypeArguments: typeArgs);

    private static readonly FactEffectRule AskWrapperRule = new(
        "echo_publish",
        "ask",
        Methods: [],
        DeclaringTypes: [],
        ReceiverTypes: [],
        Resource: "type_argument",
        TargetCallsMethods: ["Echo.Process.ask"]
    );

    [Test]
    public void Wrapper_call_site_gets_the_concrete_type_arg_combo()
    {
        var invocations = new[]
        {
            Inv("M:Echo.Process.ask``1(Echo.ProcessId,System.Object)", "M:App.Gw.AccountService``2(``1)"),
            Inv("M:App.Gw.AccountService``2(``1)", "M:App.C.Go", typeArgs: "App.PaymentGatewayResponse{App.Acct},App.AcctQuery"),
        };

        var effects = FactEffectDeriver.Derive(invocations, [AskWrapperRule]);

        var wrap = effects.ShouldHaveSingleItem();
        wrap.Provider.ShouldBe("echo_publish");
        wrap.Operation.ShouldBe("ask");
        wrap.ResourceType.ShouldBe("App.PaymentGatewayResponse{App.Acct},App.AcctQuery");
        wrap.EnclosingSymbolId.ShouldBe("M:App.C.Go");
    }

    [Test]
    public void Non_wrapper_calls_and_the_inner_ask_itself_produce_no_wrapper_effect()
    {
        var invocations = new[]
        {
            Inv("M:Echo.Process.ask``1(Echo.ProcessId,System.Object)", "M:App.Gw.AccountService``2(``1)"),
            Inv("M:App.Other.Helper", "M:App.C.Go", typeArgs: "App.Whatever"),
        };

        FactEffectDeriver.Derive(invocations, [AskWrapperRule]).ShouldBeEmpty();
    }

    [Test]
    public void Wrapper_call_with_no_type_args_is_dropped_not_emitted_blank()
    {
        var invocations = new[]
        {
            Inv("M:Echo.Process.tell``1(Echo.ProcessId,``0,Echo.ProcessId)", "M:App.Gw.Notify(System.Object)"),
            Inv("M:App.Gw.Notify(System.Object)", "M:App.C.Go"),
        };
        var tellWrapper = AskWrapperRule with { Operation = "tell", TargetCallsMethods = ["Echo.Process.tell"] };

        FactEffectDeriver.Derive(invocations, [tellWrapper]).ShouldBeEmpty();
    }
}

using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Unit tests for the `targetCallsMethods` wrapper gate in FactEffectDeriver: a request/response WRAPPER
// is any method that itself calls one of the patterns (e.g. Echo.Process.ask). The effect emits at the
// wrapper's CALL SITES, where resource:type_argument resolves to the caller's concrete type-arg combo —
// the message+reply contract the raw `ask<R>(pid, object)` discards. Pure: hand-built invocations.
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

    [Fact]
    public void Wrapper_call_site_gets_the_concrete_type_arg_combo()
    {
        var invocations = new[]
        {
            // W is a wrapper: it calls Echo.Process.ask.
            Inv("M:Echo.Process.ask``1(Echo.ProcessId,System.Object)", "M:App.Gw.AccountService``2(``1)"),
            // C.Go calls W with concrete type args — this is the readable contract.
            Inv("M:App.Gw.AccountService``2(``1)", "M:App.C.Go", typeArgs: "App.PaymentGatewayResponse{App.Acct},App.AcctQuery"),
        };

        var effects = FactEffectDeriver.Derive(invocations, [AskWrapperRule]);

        var wrap = effects.ShouldHaveSingleItem();
        wrap.Provider.ShouldBe("echo_publish");
        wrap.Operation.ShouldBe("ask");
        wrap.ResourceType.ShouldBe("App.PaymentGatewayResponse{App.Acct},App.AcctQuery"); // the concrete <TReply,TMsg> combo
        wrap.EnclosingSymbolId.ShouldBe("M:App.C.Go"); // attached at the wrapper's caller
    }

    [Fact]
    public void Non_wrapper_calls_and_the_inner_ask_itself_produce_no_wrapper_effect()
    {
        var invocations = new[]
        {
            Inv("M:Echo.Process.ask``1(Echo.ProcessId,System.Object)", "M:App.Gw.AccountService``2(``1)"), // the inner ask (Target=ask, not a wrapper)
            Inv("M:App.Other.Helper", "M:App.C.Go", typeArgs: "App.Whatever"), // calls a non-wrapper
        };

        // Neither the inner ask nor a call to a non-wrapper method should yield a wrapper effect.
        FactEffectDeriver.Derive(invocations, [AskWrapperRule]).ShouldBeEmpty();
    }

    [Fact]
    public void Wrapper_call_with_no_type_args_is_dropped_not_emitted_blank()
    {
        var invocations = new[]
        {
            Inv("M:Echo.Process.tell``1(Echo.ProcessId,``0,Echo.ProcessId)", "M:App.Gw.Notify(System.Object)"),
            Inv("M:App.Gw.Notify(System.Object)", "M:App.C.Go"), // non-generic wrapper call: no type args
        };
        var tellWrapper = AskWrapperRule with { Operation = "tell", TargetCallsMethods = ["Echo.Process.tell"] };

        FactEffectDeriver.Derive(invocations, [tellWrapper]).ShouldBeEmpty(); // null type_argument -> no effect
    }
}

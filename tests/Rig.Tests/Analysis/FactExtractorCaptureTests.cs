using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Analysis;

public sealed class FactExtractorCaptureTests
{
    private static FactExtractionResult Extract(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Snippet.cs");
        var compilation = CSharpCompilation.Create(
            "Snippet",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var model = compilation.GetSemanticModel(tree);
        return FactExtractor.Extract(new SourceModel("Snippet", "Snippet.cs", tree, tree.GetRoot(), model));
    }

    [Test]
    public void Captures_generic_type_argument_and_first_argument_member_path_at_call_site()
    {
        var source = """
            namespace App
            {
                public static class PaymentGatewayProcessDns
                {
                    public static int AccountService => 1;
                }

                public sealed class PaymentGatewayResponse<T> { }

                public static class Bus
                {
                    public static T Ask<T>(int target, object msg) => default!;

                    public static void Tell(int target, object msg) { }
                }

                public sealed class Caller
                {
                    public void Go(string m)
                    {
                        Bus.Ask<PaymentGatewayResponse<int>>(PaymentGatewayProcessDns.AccountService, m);
                        Bus.Tell(PaymentGatewayProcessDns.AccountService, m);
                    }
                }
            }
            """;

        var result = Extract(source);

        var ask = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Bus.Ask"));
        ask.TypeArguments.ShouldNotBeNull();
        ask.TypeArguments!.ShouldContain("PaymentGatewayResponse");
        ask.TypeArguments!.ShouldContain("int");
        ask.FirstArgumentName.ShouldBe("PaymentGatewayProcessDns.AccountService");

        var tell = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Bus.Tell"));
        tell.TypeArguments.ShouldBeNull();
        tell.FirstArgumentName.ShouldBe("PaymentGatewayProcessDns.AccountService");
    }

    [Test]
    public void Captures_all_argument_names_and_templates_as_json_lists()
    {
        var source = """
            namespace App
            {
                public static class Rights { public static class Account { public static int CanViewAccounts => 1; } }
                public static class Cert { public static bool HasRight(int a, int right, int t) => true; }
                public static class Api { public static void Get(string path, int x) { } }

                public sealed class Caller
                {
                    public void Go(int account, int txn)
                    {
                        Cert.HasRight(account, Rights.Account.CanViewAccounts, txn);
                        Api.Get("client", 1);
                    }
                }
            }
            """;

        var result = Extract(source);

        // The permission-shape call: the right is a member path at argument 1 (NOT the first argument).
        var hasRight = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Cert.HasRight"));
        var names = System.Text.Json.JsonSerializer.Deserialize<string?[]>(hasRight.ArgumentNames!)!;
        names.Length.ShouldBe(3);
        names[0].ShouldBe("account");
        names[1].ShouldBe("Rights.Account.CanViewAccounts");
        names[2].ShouldBe("txn");

        // A string-literal argument is captured in the templates list at its position.
        var get = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Api.Get"));
        System.Text.Json.JsonSerializer.Deserialize<string?[]>(get.ArgumentTemplates!)![0].ShouldBe("client");
        // arg 0 of Api.Get is a literal, not a member/identifier -> JSON null in the names list.
        System.Text.Json.JsonSerializer.Deserialize<string?[]>(get.ArgumentNames!)![0].ShouldBeNull();
    }

    [Test]
    public void Resolves_a_const_string_argument_to_its_value_in_the_templates_list()
    {
        var source = """
            namespace App
            {
                public static class Keys { public const string Conn = "MedDBase.DataAccessTier.ConnectionString"; }
                public static class Db { public static int GetConnectionString(string key) => 0; }

                public sealed class Caller
                {
                    public void Go() => Db.GetConnectionString(Keys.Conn);
                }
            }
            """;

        var result = Extract(source);

        var call = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Db.GetConnectionString"));
        // The call site only NAMES the constant; the templates list resolves it to its value...
        System.Text.Json.JsonSerializer.Deserialize<string?[]>(call.ArgumentTemplates!)![0]
            .ShouldBe("MedDBase.DataAccessTier.ConnectionString");
        // ...while the names list keeps the const reference path.
        System.Text.Json.JsonSerializer.Deserialize<string?[]>(call.ArgumentNames!)![0].ShouldBe("Keys.Conn");
    }

    // 18b lambda identity: a lambda passed as an argument to a call/ctor gets a synthetic symbol that
    // OWNS its body's calls (so a deferred dispatcher can later promote it to an async entry point),
    // plus a methodGroup edge consumer->lambda carrying the DelegateConsumer (the member it's handed
    // to). Calls OUTSIDE the lambda stay attributed to the enclosing method.
    [Test]
    public void Lambda_argument_becomes_a_synthetic_symbol_owning_its_body_calls()
    {
        var source = """
            namespace App
            {
                public static class Scheduler { public static void Schedule(System.Action work) { } }
                public static class Worker { public static void DoWork() { } public static void Inline() { } }

                public sealed class Caller
                {
                    public void Go()
                    {
                        Scheduler.Schedule(() => Worker.DoWork());
                        Worker.Inline();
                    }
                }
            }
            """;

        var result = Extract(source);

        // A synthetic lambda symbol, contained by Caller.Go.
        var lambda = result.Symbols.SingleOrDefault(s => s.Kind == "lambda" && s.ContainingSymbolId != null && s.ContainingSymbolId.Contains("Caller.Go"));
        lambda.ShouldNotBeNull();

        // A methodGroup edge consumer(Scheduler.Schedule) -> lambda, enclosed by Caller.Go.
        result.References.ShouldContain(r =>
            r.RefKind == "methodGroup"
            && r.TargetSymbolId == lambda!.SymbolId
            && r.DelegateConsumer != null && r.DelegateConsumer.Contains("Scheduler.Schedule")
            && r.EnclosingSymbolId != null && r.EnclosingSymbolId.Contains("Caller.Go"));

        // The call INSIDE the lambda body is attributed to the lambda, NOT Caller.Go.
        var doWork = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Worker.DoWork"));
        doWork.EnclosingSymbolId.ShouldBe(lambda!.SymbolId);

        // The call OUTSIDE the lambda still attributes to Caller.Go.
        var inline = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Worker.Inline"));
        inline.EnclosingSymbolId!.ShouldContain("Caller.Go");
    }

    // 18c part 1: a method-group ASSIGNED to a delegate field/property (not passed as an argument)
    // is a binding — the delegate-as-degenerate-interface "registration". Emit a delegate_bind
    // dispatch fact (slot -> bound target) so the seam resolver can later resolve `_handler()` to it.
    [Test]
    public void Method_group_assigned_to_a_delegate_field_emits_a_delegate_bind_dispatch_fact()
    {
        var source = """
            namespace App
            {
                public static class Worker { public static void DoWork() { } }

                public sealed class Host
                {
                    private System.Action _handler;
                    private System.Action Prop { get; set; }

                    public void Wire()
                    {
                        _handler = Worker.DoWork;     // assignment binding
                        Prop = Worker.DoWork;          // property binding
                    }
                }
            }
            """;

        var result = Extract(source);

        result.Dispatch.ShouldContain(d =>
            d.Kind == "delegate_bind" && d.SourceMember.Contains("Host._handler") && d.TargetMember.Contains("Worker.DoWork"));
        result.Dispatch.ShouldContain(d =>
            d.Kind == "delegate_bind" && d.SourceMember.Contains("Host.Prop") && d.TargetMember.Contains("Worker.DoWork"));
    }

    // 18c part 2: invoking a delegate slot (`_handler()`) emits an invocation edge to the SLOT so the
    // seam resolver can dispatch it to the bound target via the delegate_bind fact. Without this the
    // call is only a field READ and the target is invisible.
    [Test]
    public void Delegate_slot_invocation_emits_an_invocation_edge_to_the_slot()
    {
        var source = """
            namespace App
            {
                public sealed class Host
                {
                    private System.Action _handler;
                    public void Run() { _handler(); }
                }
            }
            """;

        var result = Extract(source);

        result.References.ShouldContain(r =>
            r.RefKind == "invocation"
            && r.TargetSymbolId.Contains("Host._handler")
            && r.EnclosingSymbolId != null && r.EnclosingSymbolId.Contains("Host.Run"));
    }

    [Test]
    public void Captures_bodied_property_accessor_calls_and_skips_auto_properties()
    {
        var source = """
            namespace App
            {
                public sealed class State
                {
                    private int _backing;

                    public int Settings
                    {
                        get { return _backing; }
                        set { _backing = Validate(value); }
                    }

                    public int Auto { get; set; }

                    private int Validate(int v) => v;

                    public void Save(State other, int s)
                    {
                        other.Settings = s;
                        Auto = s;
                    }

                    public int Load(State other)
                    {
                        var x = other.Auto;
                        return other.Settings;
                    }
                }
            }
            """;

        var result = Extract(source);

        result.Symbols.ShouldContain(s => s.Kind == "method" && s.Name == "get_Settings");
        result.Symbols.ShouldContain(s => s.Kind == "method" && s.Name == "set_Settings");
        result.References.ShouldContain(r => r.RefKind == "write" && r.TargetSymbolId.Contains("State.Settings"));

        var setCall = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("set_Settings"));
        setCall.EnclosingSymbolId!.ShouldContain("State.Save");
        setCall.ReceiverType.ShouldBe("App.State");

        var getCall = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("get_Settings"));
        getCall.EnclosingSymbolId!.ShouldContain("State.Load");

        result.Symbols.ShouldNotContain(s => s.Name == "get_Auto" || s.Name == "set_Auto");
        result.References.ShouldNotContain(r =>
            r.RefKind == "invocation" && (r.TargetSymbolId.Contains("get_Auto") || r.TargetSymbolId.Contains("set_Auto"))
        );
    }

    [Test]
    public void Compound_assignment_invokes_both_accessors()
    {
        var source = """
            namespace App
            {
                public sealed class Counter
                {
                    private int _n;
                    public int Value
                    {
                        get { return _n; }
                        set { _n = value; }
                    }

                    public void Bump(Counter c) { c.Value += 1; }
                }
            }
            """;

        var result = Extract(source);

        var bumpCalls = result
            .References.Where(r => r.RefKind == "invocation" && r.EnclosingSymbolId!.Contains("Counter.Bump"))
            .Select(r => r.TargetSymbolId)
            .ToList();
        bumpCalls.ShouldContain(id => id.Contains("get_Value"));
        bumpCalls.ShouldContain(id => id.Contains("set_Value"));
    }

    // Repro probe: invocations that appear directly as a LINQ query `from x in Expr()` clause
    // (the compiler desugars Expr() into a SelectMany/Select collection-selector lambda). The PACS
    // `MoveExternalFile` monadic `Try<T>` chain (`from __ in FileExt.Move(..)`) loses these effects.
    [Test]
    public void Captures_invocations_in_query_clause_expressions()
    {
        var source = """
            namespace App
            {
                public struct Box<T> { }

                // LanguageExt-shape query operators: Select/SelectMany are EXTENSION methods, not instance.
                public static class BoxQuery
                {
                    public static Box<U> Select<T, U>(this Box<T> b, System.Func<T, U> f) => default;
                    public static Box<V> SelectMany<T, U, V>(this Box<T> b, System.Func<T, Box<U>> bind, System.Func<T, U, V> proj) => default;
                }

                public static class Fx
                {
                    public static Box<int> Seed() => default;
                    public static Box<int> Step() => default;
                    public static Box<int> Wrap(System.Func<int> f) => default;
                    public static int Direct() => 0;
                }

                public sealed class Caller
                {
                    public Box<int> Go() =>
                        from a in Fx.Seed()
                        from b in Fx.Step()
                        select a + b;

                    public Box<int> GoLambda() =>
                        from a in Fx.Wrap(() => Fx.Direct())
                        select a;
                }
            }
            """;

        var result = Extract(source);

        bool Invoked(string targetContains, string enclosingContains) =>
            result.References.Any(r =>
                r.RefKind == "invocation"
                && r.TargetSymbolId.Contains(targetContains)
                && r.EnclosingSymbolId is { } e && e.Contains(enclosingContains));

        // Control: the query SOURCE expression (first `from`) and invocations inside an explicit lambda
        // are captured today.
        Invoked("Fx.Seed", "Caller.Go").ShouldBeTrue("query source expression should be captured");
        Invoked("Fx.Direct", "Caller.GoLambda").ShouldBeTrue("invocation inside an explicit lambda should be captured");

        // The gap: the collection-selector expression of a subsequent `from` clause.
        Invoked("Fx.Step", "Caller.Go").ShouldBeTrue("query collection-selector invocation should be captured");
    }

    // F1b: when Roslyn can't fully bind a call it resolves it to a CandidateSymbol (here forced via an
    // arg-count mismatch; the real driver is net48 cross-assembly partial binding). The invocation edge
    // must still be captured — dropping it silently loses effect-bearing edges (e.g. first-party FileExt.Move).
    [Test]
    public void Captures_invocation_resolved_only_to_a_candidate_symbol()
    {
        var source = """
            namespace App
            {
                public static class Io
                {
                    public static int Move(int x) => x;
                }

                public sealed class Caller
                {
                    public void Go()
                    {
                        Io.Move(1, 2);
                    }
                }
            }
            """;

        var result = Extract(source);

        result.References.ShouldContain(r =>
            r.RefKind == "invocation"
            && r.TargetSymbolId.Contains("Io.Move")
            && r.EnclosingSymbolId != null
            && r.EnclosingSymbolId.Contains("Caller.Go"));
    }

    [Test]
    public void Interface_property_dispatch_resolves_to_bodied_impl_accessor()
    {
        var source = """
            namespace App
            {
                public interface IConfig { int Mode { get; set; } }

                public sealed class Config : IConfig
                {
                    private int _m;
                    public int Mode { get => _m; set => _m = value; }
                }
            }
            """;

        var result = Extract(source);

        result.Dispatch.ShouldContain(d =>
            d.Kind == "impl" && d.SourceMember.Contains("IConfig.get_Mode") && d.TargetMember.Contains("Config.get_Mode")
        );
        result.Dispatch.ShouldContain(d =>
            d.Kind == "impl" && d.SourceMember.Contains("IConfig.set_Mode") && d.TargetMember.Contains("Config.set_Mode")
        );
    }
}

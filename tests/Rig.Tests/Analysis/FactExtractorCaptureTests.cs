using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Domain.Data;
using Shouldly;

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
        return FactExtractor.Extract(new SourceModel("Snippet", "Snippet.cs", tree, tree.GetRoot(), model), new SymbolStringCache());
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
    public void Declaring_type_arg_binding_records_concrete_and_forwarded_receiver_args()
    {
        var source = """
            namespace App
            {
                public sealed class Account { }
                public sealed class Invoice { }

                public sealed class QueryPipeline<T, U>
                {
                    public void Enumerate() { }
                }

                public sealed class Caller
                {
                    public void Concrete(QueryPipeline<Account, Invoice> pipeline) => pipeline.Enumerate();
                }

                // Forwarding receiver: args are the enclosing TYPE's params, swapped (Y=1, X=0).
                public sealed class Swapper<X, Y>
                {
                    public void Reversed(QueryPipeline<Y, X> pipeline) => pipeline.Enumerate();
                }
            }
            """;

        var result = Extract(source);

        ReferenceFact Enumerate(string enclosing) =>
            result.References.Single(r =>
                r.RefKind == "invocation"
                && r.TargetSymbolId.Contains("QueryPipeline")
                && r.TargetSymbolId.Contains("Enumerate")
                && r.EnclosingSymbolId!.Contains(enclosing)
            );

        // Open form stays the original definition (for dispatch narrowing); the binding carries rendering.
        var concrete = Enumerate("Concrete");
        concrete.ReceiverType.ShouldBe("App.QueryPipeline<T, U>");
        concrete.DeclaringTypeArgBinding.ShouldBe("""["C:App.Account","C:App.Invoice"]""");
        concrete.MethodTypeArgBinding.ShouldBeNull(); // Enumerate is non-generic

        // Forwarded params encode as enclosing-TYPE ordinals in source order: Y is T:1, X is T:0.
        Enumerate("Reversed").DeclaringTypeArgBinding.ShouldBe("""["T:1","T:0"]""");

        // Binding is gated to first-party callees only (only first-party nodes render).
        result.References.Where(r => r.DeclaringTypeArgBinding is not null).ShouldAllBe(r => r.TargetInSource);
    }

    [Test]
    public void Declaring_binding_is_captured_for_a_property_read_on_a_generic_type()
    {
        // `pipe.Run` reads a property on Pipe<Account, Invoice> (mirrors QueryPipeline's `Func<…> Enumerate`
        // accessed as `pipeline.Enumerate()`). The target is the PROPERTY (not a method), so the declaring
        // binding must come from its owning type's instantiation, not `target as IMethodSymbol`.
        var source = """
            namespace App
            {
                public sealed class Account { }
                public sealed class Invoice { }
                public sealed class Pipe<T, U> { public System.Action Run { get; } = null; }

                public sealed class Caller
                {
                    public void Go(Pipe<Account, Invoice> pipe) => pipe.Run();
                }
            }
            """;

        var result = Extract(source);

        result
            .References.Where(r => r.TargetSymbolId.Contains("Pipe") && r.TargetSymbolId.Contains("Run"))
            .ShouldContain(r => r.DeclaringTypeArgBinding == """["C:App.Account","C:App.Invoice"]""");
    }

    [Test]
    public void Method_type_arg_binding_records_static_factory_and_generic_method_args()
    {
        // Mirrors the MedDBase QueryResult/QueryPipeline static-factory shape: a static generic method whose
        // body forwards a mix of the enclosing TYPE param (TColumn) and its own METHOD param (RRecord).
        var source = """
            namespace App
            {
                public sealed class Account { }
                public sealed class Row { }

                public sealed class QueryPipeline<TRecord, TColumn>
                {
                    public static QueryPipeline<RRecord, TColumn> Create<TEntity, RRecord>() => null;
                }

                public sealed class QueryResult<TRecord, TColumn>
                {
                    public static QueryResult<RRecord, TColumn> Create<TEntity, RRecord>() =>
                        QueryPipeline<RRecord, TColumn>.Create<TEntity, RRecord>() == null ? null : null;
                }

                public sealed class Caller
                {
                    public void Go() => QueryResult<Account, Row>.Create<Account, Account>();
                }
            }
            """;

        var result = Extract(source);

        ReferenceFact Create(string enclosing) =>
            result.References.Single(r =>
                r.RefKind == "invocation"
                && r.TargetSymbolId.Contains("QueryPipeline")
                && r.TargetSymbolId.Contains("Create")
                && r.EnclosingSymbolId!.Contains(enclosing)
            );

        // The ENTRY call pins concretes: QueryResult<Account, Row>.Create<Account, Account>.
        var entry = result.References.Single(r =>
            r.TargetSymbolId.Contains("QueryResult") && r.TargetSymbolId.Contains("Create") && r.EnclosingSymbolId!.Contains("Caller.Go")
        );
        entry.DeclaringTypeArgBinding.ShouldBe("""["C:App.Account","C:App.Row"]""");
        entry.MethodTypeArgBinding.ShouldBe("""["C:App.Account","C:App.Account"]""");

        // The INNER static call forwards: declaring QueryPipeline<RRecord(M:1), TColumn(T:1)>, method <TEntity
        // (M:0), RRecord(M:1)> — exactly the tokens the renderer resolves against the parent's instantiation.
        var inner = Create("QueryResult");
        inner.DeclaringTypeArgBinding.ShouldBe("""["M:1","T:1"]""");
        inner.MethodTypeArgBinding.ShouldBe("""["M:0","M:1"]""");
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
        var names = JsonSerializer.Deserialize<string?[]>(hasRight.ArgumentNames!)!;
        names.Length.ShouldBe(3);
        names[0].ShouldBe("account");
        names[1].ShouldBe("Rights.Account.CanViewAccounts");
        names[2].ShouldBe("txn");

        // A string-literal argument is captured in the templates list at its position.
        var get = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Api.Get"));
        JsonSerializer.Deserialize<string?[]>(get.ArgumentTemplates!)![0].ShouldBe("client");
        // arg 0 of Api.Get is a literal, not a member/identifier -> JSON null in the names list.
        JsonSerializer.Deserialize<string?[]>(get.ArgumentNames!)![0].ShouldBeNull();
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
        JsonSerializer.Deserialize<string?[]>(call.ArgumentTemplates!)![0].ShouldBe("MedDBase.DataAccessTier.ConnectionString");
        // ...while the names list keeps the const reference path.
        JsonSerializer.Deserialize<string?[]>(call.ArgumentNames!)![0].ShouldBe("Keys.Conn");
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
        var lambda = result.Symbols.SingleOrDefault(s =>
            s.Kind == "lambda" && s.ContainingSymbolId != null && s.ContainingSymbolId.Contains("Caller.Go")
        );
        lambda.ShouldNotBeNull();

        // A methodGroup edge consumer(Scheduler.Schedule) -> lambda, enclosed by Caller.Go.
        result.References.ShouldContain(r =>
            r.RefKind == "methodGroup"
            && r.TargetSymbolId == lambda!.SymbolId
            && r.DelegateConsumer != null
            && r.DelegateConsumer.Contains("Scheduler.Schedule")
            && r.EnclosingSymbolId != null
            && r.EnclosingSymbolId.Contains("Caller.Go")
        );

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
            d.Kind == "delegate_bind" && d.SourceMember.Contains("Host._handler") && d.TargetMember.Contains("Worker.DoWork")
        );
        result.Dispatch.ShouldContain(d =>
            d.Kind == "delegate_bind" && d.SourceMember.Contains("Host.Prop") && d.TargetMember.Contains("Worker.DoWork")
        );
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
            && r.EnclosingSymbolId != null
            && r.EnclosingSymbolId.Contains("Host.Run")
        );
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

    // An effect-bearing call inside a property/indexer accessor BODY must be owned by the accessor
    // method (M:get_X / M:set_X) — the symbol the access-site call edge targets and the graph node that
    // is emitted — NOT the property (P:X), which is never a call-graph node. Keying to the property
    // orphaned the effect from reachability: `reaches`/`tree` intersect call-graph method ids against
    // effect enclosing ids, so a P:-keyed effect could never match a reachable accessor node.
    [Test]
    public void Calls_inside_accessor_bodies_are_owned_by_the_accessor_not_the_property()
    {
        var source = """
            namespace App
            {
                public static class Repo { public static int Fetch() => 0; public static void Store(int v) { } }

                public sealed class Model
                {
                    // expression-bodied property: body is the getter's
                    public int Lazy => Repo.Fetch();

                    // full-block accessors
                    public int Block
                    {
                        get { return Repo.Fetch(); }
                        set { Repo.Store(value); }
                    }

                    // expression-bodied accessors
                    public int Arrow
                    {
                        get => Repo.Fetch();
                        set => Repo.Store(value);
                    }
                }
            }
            """;

        var result = Extract(source);

        var fetchEnclosings = result
            .References.Where(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Repo.Fetch"))
            .Select(r => r.EnclosingSymbolId!)
            .ToList();
        // The getter of every form — including the expression-bodied property `Lazy` that used to key to P:.
        fetchEnclosings.ShouldContain(e => e.Contains("get_Lazy"));
        fetchEnclosings.ShouldContain(e => e.Contains("get_Block"));
        fetchEnclosings.ShouldContain(e => e.Contains("get_Arrow"));
        // The regression guard: no accessor-body effect is ever keyed to a property id (P:).
        fetchEnclosings.ShouldAllBe(e => e.StartsWith("M:"));

        var storeEnclosings = result
            .References.Where(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Repo.Store"))
            .Select(r => r.EnclosingSymbolId!)
            .ToList();
        storeEnclosings.ShouldContain(e => e.Contains("set_Block"));
        storeEnclosings.ShouldContain(e => e.Contains("set_Arrow"));
        storeEnclosings.ShouldAllBe(e => e.StartsWith("M:"));
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
                && r.TargetSymbolId.Contains(targetContains, StringComparison.Ordinal)
                && r.EnclosingSymbolId is { } e
                && e.Contains(enclosingContains, StringComparison.Ordinal)
            );

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
            && r.EnclosingSymbolId.Contains("Caller.Go")
        );
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

    // Regression (2026-06-16 path/callers over-reach): a `nameof(Method)` is a compile-time STRING, not a
    // delegate conversion or invocation — it must NOT emit a methodGroup/invocation reference edge that the
    // call graph would walk as a real call. A genuine method-group delegate conversion in the SAME source
    // MUST still produce its methodGroup edge, so the suppression is precise to nameof only.
    [Test]
    public void Nameof_of_a_method_does_not_emit_a_call_or_methodgroup_edge_but_a_real_method_group_does()
    {
        var source = """
            namespace App
            {
                public sealed class Menu
                {
                    public void MarkSent() { }
                    public void RealHandler() { }

                    // nameof(MarkSent): compile-time string only — no call/methodGroup edge to MarkSent.
                    public string MenuLabel = nameof(MarkSent);

                    // genuine method-group conversion: RealHandler IS converted to a delegate -> methodGroup.
                    public void Wire()
                    {
                        System.Action a = RealHandler;
                    }
                }
            }
            """;

        var result = Extract(source);

        // No invocation OR methodGroup reference targets MarkSent (it appears only inside nameof). The
        // call graph (Reads.LoadFactGraphAsync) only turns invocation/methodGroup/ctor refs into edges, so
        // a nameof-classified ref is non-traversable. We also assert the recorded ref kind is `nameof`.
        var markSentRefs = result.References.Where(r => r.TargetSymbolId.Contains("Menu.MarkSent")).ToList();
        markSentRefs.ShouldNotBeEmpty(); // the name WAS referenced — we record it, just not as a call.
        markSentRefs.ShouldAllBe(r => r.RefKind == "nameof");
        markSentRefs.ShouldAllBe(r => r.RefKind != "invocation" && r.RefKind != "methodGroup");

        // The real method-group conversion still produces a methodGroup edge into RealHandler.
        result.References.ShouldContain(r => r.TargetSymbolId.Contains("Menu.RealHandler") && r.RefKind == "methodGroup");
    }

    // A dotted `nameof(Type.Method)` (and `nameof(field)`) likewise yields only a string — the inner
    // Method/field name must not become a traversable reference either.
    [Test]
    public void Nameof_of_a_dotted_member_does_not_emit_a_call_edge()
    {
        var source = """
            namespace App
            {
                public static class Repo { public static int Fetch() => 0; }

                public sealed class User
                {
                    public string Name = nameof(Repo.Fetch);
                }
            }
            """;

        var result = Extract(source);

        var fetchRefs = result.References.Where(r => r.TargetSymbolId.Contains("Repo.Fetch")).ToList();
        fetchRefs.ShouldAllBe(r => r.RefKind == "nameof");
    }
}

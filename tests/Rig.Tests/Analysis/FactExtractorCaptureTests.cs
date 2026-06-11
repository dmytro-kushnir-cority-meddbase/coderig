using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Shouldly;

namespace Rig.Tests.Analysis;

// B1: call-site capture of generic TYPE ARGUMENTS and the first-argument member path — the facts that
// make Echo `ask<TResponse>(target, msg)` / `tell(target, msg)` readable (the asked type + the routing
// target). Compiles a snippet and runs the real FactExtractor — no SQLite, no playground.
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

    [Fact]
    public void Captures_generic_type_argument_and_first_argument_member_path_at_call_site()
    {
        // Mirrors the MedDBase Echo helper shape: ask<PaymentGatewayResponse<T>>(ProcessDns.X, msg).
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
        ask.TypeArguments!.ShouldContain("PaymentGatewayResponse"); // the asked message type, captured at the call site
        ask.TypeArguments!.ShouldContain("int");
        ask.FirstArgumentName.ShouldBe("PaymentGatewayProcessDns.AccountService"); // the routing target

        var tell = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("Bus.Tell"));
        tell.TypeArguments.ShouldBeNull(); // non-generic call — no type arguments
        tell.FirstArgumentName.ShouldBe("PaymentGatewayProcessDns.AccountService");
    }

    // Getter/setter walk: a property access is a call to its accessor. A BODIED accessor must become a
    // graph node (method symbol) AND gain a call edge at every access site, so reach walks the accessor's
    // effects. An AUTO-property accessor (no body, no effect) must produce neither — it would only bloat.
    [Fact]
    public void Captures_bodied_property_accessor_calls_and_skips_auto_properties()
    {
        var source = """
            namespace App
            {
                public sealed class State
                {
                    private int _backing;

                    // Bodied accessors — effectful in real code (validation / lazy fetch / persist).
                    public int Settings
                    {
                        get { return _backing; }
                        set { _backing = Validate(value); }
                    }

                    // Auto-property — no body, no effect. Must NOT be captured as a node or an edge.
                    public int Auto { get; set; }

                    private int Validate(int v) => v;

                    public void Save(State other, int s)
                    {
                        other.Settings = s;       // write site -> set_Settings (receiver: State)
                        Auto = s;                 // auto write -> nothing
                    }

                    public int Load(State other)
                    {
                        var x = other.Auto;       // auto read -> nothing
                        return other.Settings;    // read site -> get_Settings
                    }
                }
            }
            """;

        var result = Extract(source);

        // Accessor methods of the bodied property are emitted as method SYMBOLS (graph nodes).
        result.Symbols.ShouldContain(s => s.Kind == "method" && s.Name == "get_Settings");
        result.Symbols.ShouldContain(s => s.Kind == "method" && s.Name == "set_Settings");
        // The data-flow read/write refs are still emitted (unchanged behavior).
        result.References.ShouldContain(r => r.RefKind == "write" && r.TargetSymbolId.Contains("State.Settings"));

        // The write site emits an invocation EDGE into the bodied setter, carrying the receiver type.
        var setCall = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("set_Settings"));
        setCall.EnclosingSymbolId!.ShouldContain("State.Save");
        setCall.ReceiverType.ShouldBe("App.State");

        // The read site emits an invocation edge into the bodied getter.
        var getCall = result.References.Single(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("get_Settings"));
        getCall.EnclosingSymbolId!.ShouldContain("State.Load");

        // Auto-property: no accessor node, no accessor edge — neither read nor write side.
        result.Symbols.ShouldNotContain(s => s.Name == "get_Auto" || s.Name == "set_Auto");
        result.References.ShouldNotContain(r =>
            r.RefKind == "invocation" && (r.TargetSymbolId.Contains("get_Auto") || r.TargetSymbolId.Contains("set_Auto"))
        );
    }

    // A compound assignment (`+=`) and increment/decrement run BOTH accessors — the edge set must reflect
    // that, or a getter effect reached only through `x.P += …` would be invisible.
    [Fact]
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

    // Interface property members get the same typed dispatch as interface methods: the call to the
    // interface accessor resolves to the concrete impl's bodied accessor (IConfig.set_Mode -> Config.set_Mode).
    [Fact]
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

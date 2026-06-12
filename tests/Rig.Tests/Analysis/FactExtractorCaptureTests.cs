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

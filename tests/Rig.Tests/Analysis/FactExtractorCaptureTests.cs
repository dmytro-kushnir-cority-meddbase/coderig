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
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Domain.Data;
using Shouldly;

namespace Rig.Tests.Analysis;

// A method group `x.M` (a method referenced as a delegate, not invoked) binds the delegate to receiver `x`.
// FactExtractor now records `x`'s type as the methodGroup ReferenceFact's ReceiverType — same as an
// invocation — so the (deferred) call can be receiver-narrowed instead of CHA-fanned. An INSTANCE-receiver
// method group captures the receiver; a STATIC-class method group (`Type.M`) has no value receiver -> null.
// (Before this fix, every methodGroup had a null receiver, which forced the full override fan — e.g. the
// `Retry(cert.Delete)` ×49 phantom in docs/bug-callers-reverse-overreach.md.)
public sealed class MethodGroupReceiverExtractionTests
{
    private static readonly MetadataReference[] FrameworkReferences = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    // Caller passes `w.Do` (instance method group, receiver = Worker) and `Worker.Stat` (static, no receiver)
    // as delegates. Both are method groups, not invocations.
    private const string Source = """
        namespace App;
        public class Worker
        {
            public void Do() { }
            public static void Stat() { }
        }
        public class Caller
        {
            private void Run(System.Action a) { }
            public void Go(Worker w)
            {
                Run(w.Do);
                Run(Worker.Stat);
            }
        }
        """;

    private static IReadOnlyList<ReferenceFact> Extract()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, path: "App.cs");
        var comp = CSharpCompilation.Create(
            assemblyName: "App",
            syntaxTrees: [tree],
            references: FrameworkReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();

        var model = comp.GetSemanticModel(tree);
        var extraction = FactExtractor.Extract(
            new SourceModel(ProjectName: "App", FilePath: "App.cs", Tree: tree, Root: tree.GetRoot(), SemanticModel: model),
            new SymbolStringCache()
        );
        return extraction.References!;
    }

    [Test]
    public void Instance_method_group_captures_the_receiver_type()
    {
        var refs = Extract();

        var mg = refs.Where(r => r.RefKind == "methodGroup" && r.TargetSymbolId.Contains("Worker.Do")).ToList().ShouldHaveSingleItem();
        mg.ReceiverType.ShouldNotBeNull();
        mg.ReceiverType!.ShouldContain("Worker");
    }

    [Test]
    public void Static_method_group_captures_declaring_type_but_it_is_inert()
    {
        // `Worker.Stat` captures the declaring type too, but it's harmless: a static method has no overrides,
        // so dispatch never fans it — the receiver is simply never consumed. (Documents that we don't bother
        // special-casing static qualifiers; capturing the type is inert, not wrong.)
        var refs = Extract();

        var mg = refs.Where(r => r.RefKind == "methodGroup" && r.TargetSymbolId.Contains("Worker.Stat")).ToList().ShouldHaveSingleItem();
        mg.ReceiverType.ShouldNotBeNull();
        mg.ReceiverType!.ShouldContain("Worker");
    }
}

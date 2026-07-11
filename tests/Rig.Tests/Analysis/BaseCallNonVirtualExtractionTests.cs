using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Domain.Data;
using Shouldly;

namespace Rig.Tests.Analysis;

// A `base.M(...)` call is NON-VIRTUAL (C# spec: CIL `call`, not `callvirt`) — it binds to exactly the base
// implementation and can never dispatch to a sibling override. FactExtractor flags such an invocation's
// ReferenceFact with NonVirtual=true (the receiver is the `base` keyword); every other call (an ordinary
// virtual call through an instance receiver, a `this.M()`, a bare call) keeps NonVirtual=false. The flag is
// what lets the stage-2 traversal keep a base call out of the override-dispatch fan, forward and reverse
// (see docs/bug-callers-reverse-overreach.md, the 2026-06-24 root-cause refinement).
public sealed class BaseCallNonVirtualExtractionTests
{
    private static readonly MetadataReference[] FrameworkReferences = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    // Derived.M overrides Base.M and calls `base.M()` (non-virtual). Caller.Go makes an ORDINARY virtual
    // call `someBase.M()` through an instance receiver (virtual). Both invocations target Base/Derived's M.
    private const string Source = """
        namespace App;
        public class Base
        {
            public virtual void M() { }
        }
        public class Derived : Base
        {
            public override void M()
            {
                base.M();
            }
        }
        public class Caller
        {
            public void Go(Base someBase)
            {
                someBase.M();
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
    public void The_base_M_call_is_flagged_non_virtual()
    {
        var refs = Extract();

        var baseCall = refs.Where(r =>
                r.RefKind == "invocation"
                && r.TargetSymbolId.Contains("Base.M")
                && r.EnclosingSymbolId != null
                && r.EnclosingSymbolId.Contains("Derived.M")
            )
            .ToList()
            .ShouldHaveSingleItem();
        baseCall.NonVirtual.ShouldBeTrue();
    }

    [Test]
    public void The_ordinary_virtual_call_is_not_flagged_non_virtual()
    {
        var refs = Extract();

        var virtualCall = refs.Where(r =>
                r.RefKind == "invocation"
                && r.TargetSymbolId.Contains("Base.M")
                && r.EnclosingSymbolId != null
                && r.EnclosingSymbolId.Contains("Caller.Go")
            )
            .ToList()
            .ShouldHaveSingleItem();
        virtualCall.NonVirtual.ShouldBeFalse();
    }
}

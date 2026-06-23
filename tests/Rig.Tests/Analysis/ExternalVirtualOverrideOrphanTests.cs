using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// RED repro for the "external-virtual-override orphan" gap (docs/backlog.md).
//
// Real-world shape (LLBLGen): a parameterless convenience `EntityBase.Save()` trampolines INSIDE the external
// DLL to a virtual `Save(IPredicate, bool)` hatch; first-party entities override the 2-arg hatch (where the
// effect lives). A call `document.Save()` statically binds to the EXTERNAL `EntityBase.Save()`
// (TargetInSource=0); the graph-load filter drops that edge, so the caller never reaches the first-party
// override — even though rig already mined the override chain from the 2-arg virtual down.
//
// The bug ONLY reproduces if the base lives in a SEPARATE (metadata-referenced) assembly — that is what makes
// the base method TargetInSource=0. A single-source fixture would be TargetInSource=1 and show no bug. So this
// test genuinely compiles two assemblies and references the first as metadata.
public sealed class ExternalVirtualOverrideOrphanTests
{
    private static readonly MetadataReference[] FrameworkReferences = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    // The EXTERNAL base: parameterless convenience Save() trampolining to the virtual Save(IPredicate,bool)
    // hatch. Compiled to its OWN assembly and referenced as METADATA → its members are TargetInSource=0.
    private const string ExternalSource = """
        namespace Ext;
        public interface IPredicate { }
        public class EntityBase
        {
            public void Save() => Save(null, true);
            public virtual void Save(IPredicate predicate, bool recurse) { }
        }
        """;

    // The first-party app: overrides the 2-arg hatch (where the effect lives) and calls the 0-arg convenience
    // form — which binds to the EXTERNAL EntityBase.Save().
    private const string AppSource = """
        using Ext;
        namespace App;
        public class CommonEntityBase : EntityBase
        {
            public override void Save(IPredicate predicate, bool recurse) => OnSaved();
            protected virtual void OnSaved() { }
        }
        public class DocumentEntity : CommonEntityBase
        {
            public override void Save(IPredicate predicate, bool recurse) => Webhook();
            private void Webhook() { }
        }
        public class Caller
        {
            public void DoSave(DocumentEntity d) => d.Save();
        }
        """;

    private static MetadataReference CompileExternal()
    {
        var tree = CSharpSyntaxTree.ParseText(ExternalSource, path: "Ext.cs");
        var comp = CSharpCompilation.Create(
            assemblyName: "Ext",
            syntaxTrees: [tree],
            references: FrameworkReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var stream = new MemoryStream();
        var emit = comp.Emit(stream);
        emit.Success.ShouldBeTrue(string.Join(Environment.NewLine, emit.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static AnalysisResult ExtractApp(MetadataReference externalReference)
    {
        var tree = CSharpSyntaxTree.ParseText(AppSource, path: "App.cs");
        var comp = CSharpCompilation.Create(
            assemblyName: "App",
            syntaxTrees: [tree],
            references: [.. FrameworkReferences, externalReference],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();

        var model = comp.GetSemanticModel(tree);
        var extraction = FactExtractor.Extract(
            new SourceModel(ProjectName: "App", FilePath: "App.cs", Tree: tree, Root: tree.GetRoot(), SemanticModel: model),
            new SymbolStringCache()
        );
        return new AnalysisResult(
            SolutionPath: "App",
            SourceFiles: [],
            DiRegistrations: [],
            Symbols: extraction.Symbols,
            References: extraction.References,
            TypeRelations: extraction.TypeRelations,
            DispatchFacts: extraction.Dispatch
        );
    }

    [Test]
    public void Parameterless_save_binds_to_external_base_so_the_firstparty_override_is_orphaned()
    {
        var result = ExtractApp(CompileExternal());

        // Precondition: the `d.Save()` call bound to the EXTERNAL base (TargetInSource=0), enclosed in DoSave.
        result.References!.ShouldContain(r =>
            r.EnclosingSymbolId != null
            && r.EnclosingSymbolId.Contains("Caller.DoSave")
            && r.TargetSymbolId.EndsWith("EntityBase.Save")
            && !r.TargetInSource
        );

        // Sanity: the override chain IS mined ACROSS the assembly boundary (external 2-arg virtual <- first-party
        // override) — the chain a redirect rule will reconnect to.
        result.DispatchFacts!.ShouldContain(d =>
            d.Kind == "override" && d.SourceMember.Contains("EntityBase.Save(") && d.TargetMember.Contains("CommonEntityBase.Save(")
        );

        var graph = FactProjection.GraphData(result);

        // The dropped edge: Caller.DoSave has NO outgoing Save edge in the graph.
        graph.CallEdges.ShouldNotContain(e => e.Caller.Contains("Caller.DoSave") && e.Callee.Contains(".Save"));

        // The first-party override that carries the effect is unreachable from the caller (the orphan).
        FactPathFinder.Find(graph, "Caller.DoSave", "DocumentEntity.Save").ShouldBeNull();
    }

    [Test]
    public void A_redirect_rule_reattaches_the_caller_to_the_firstparty_override()
    {
        var result = ExtractApp(CompileExternal());

        // The virtual-hatch DocID = the SourceMember of the cross-assembly override fact — taken straight from
        // the mined facts, so the test never hard-codes a DocID signature format.
        var virtualHatch = result
            .DispatchFacts!.First(d =>
                d.Kind == "override" && d.SourceMember.Contains("EntityBase.Save(") && d.TargetMember.Contains("CommonEntityBase.Save(")
            )
            .SourceMember;

        // The redirect rule the decompiled trampoline map yields: EntityBase.Save (any convenience overload)
        // -> EntityBase.Save(IPredicate, bool).
        var redirectRules = new[] { new FactRedirectRule(Method: "M:Ext.EntityBase.Save", RedirectTo: virtualHatch) };

        var graph = FactProjection.GraphData(result, redirectRules: redirectRules);

        // GREEN: the redirected (kept) edge to the external virtual is resolved by receiver-narrowed dispatch
        // to the first-party override — so the caller now reaches it (and its effect).
        var path = FactPathFinder.Find(graph, "Caller.DoSave", "DocumentEntity.Save");
        path.ShouldNotBeNull();
        path!.ShouldContain(s => s.SymbolId.Contains("DocumentEntity.Save"));
    }
}

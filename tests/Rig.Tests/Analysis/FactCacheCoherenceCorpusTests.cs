using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Analysis;

// End-to-end (real extraction → fact graph) coverage of the FR-7 cache-coherence graph hazard: a BULK write
// (UpdateMulti) to a CACHED entity whose forward closure does NOT also invalidate that entity's cache is a
// finding; adding the invalidation call clears it. Mirrors the compile+extract pattern in
// ExternalVirtualOverrideOrphanTests (single source assembly here — no cross-assembly boundary needed).
//
// Graph is built via the PRODUCTION FactGraphProjection.FromAnalysis (what `rig derive` runs over), NOT the
// FactProjection test fixture: the fixture deliberately drops ReceiverType on non-redirected edges, and the
// deriver keys the entity off the bulk-write edge's ReceiverType, so the fixture would orphan the finding.
public sealed class FactCacheCoherenceCorpusTests
{
    private static readonly MetadataReference[] FrameworkReferences = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    // BUG case: Importer.Run does a bulk UpdateMulti on an AccountEntityCollection (entity "Account", cached)
    // but never invalidates AccountCache → stale cache.
    private const string BugSource = """
        namespace App;
        public sealed class AccountEntityCollection
        {
            public void UpdateMulti(string filter) { }
        }
        public static class AccountCache
        {
            public static void Clear() { }
        }
        public sealed class Importer
        {
            public void Run(AccountEntityCollection coll) => coll.UpdateMulti("active");
        }
        """;

    // FIX case: Run ALSO invalidates AccountCache → no finding.
    private const string FixSource = """
        namespace App;
        public sealed class AccountEntityCollection
        {
            public void UpdateMulti(string filter) { }
        }
        public static class AccountCache
        {
            public static void Clear() { }
        }
        public sealed class Importer
        {
            public void Run(AccountEntityCollection coll)
            {
                coll.UpdateMulti("active");
                AccountCache.Clear();
            }
        }
        """;

    private static AnalysisResult Extract(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "App.cs");
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
    public void UpdateMulti_receiver_resolves_to_AccountEntityCollection()
    {
        // Precondition the deriver leans on: the bulk-write call's ReceiverType must resolve to the collection
        // type so the suffix-strip recovers entity "Account". Assert it from the extracted facts (no hard-coded
        // DocID format).
        var result = Extract(BugSource);

        result.References!.ShouldContain(r =>
            r.RefKind == "invocation"
            && r.TargetSymbolId.Contains("UpdateMulti")
            && r.ReceiverType != null
            && r.ReceiverType.Contains("AccountEntityCollection")
        );
    }

    [Test]
    public void Bulk_write_without_invalidation_is_a_cache_coherence_finding()
    {
        var graph = FactGraphProjection.FromAnalysis(Extract(BugSource));

        var findings = FactCacheCoherenceDeriver.DeriveCacheCoherence(
            graph: graph,
            cachedEntities: new HashSet<string>(["Account"], StringComparer.Ordinal),
            bulkWriteMethods: ["UpdateMulti"],
            invalidationMethods: ["Clear"]
        );

        var finding = findings.ShouldHaveSingleItem();
        finding.Entity.ShouldBe("Account");
        finding.Method.ShouldContain("Importer.Run");
    }

    [Test]
    public void Bulk_write_with_invalidation_in_the_same_closure_is_clean()
    {
        var graph = FactGraphProjection.FromAnalysis(Extract(FixSource));

        var findings = FactCacheCoherenceDeriver.DeriveCacheCoherence(
            graph: graph,
            cachedEntities: new HashSet<string>(["Account"], StringComparer.Ordinal),
            bulkWriteMethods: ["UpdateMulti"],
            invalidationMethods: ["Clear"]
        );

        findings.ShouldBeEmpty();
    }
}

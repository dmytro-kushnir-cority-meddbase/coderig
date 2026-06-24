using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// End-to-end (real extraction → effects → correlation) coverage of the FR-7 cache-coherence hazard, now
// reframed as the cache-specific INSTANCE of the generic FactCorrelationDeriver. This proves the WHOLE path
// the production `rig derive` runs: ordinary EFFECT RULES (llblgen:bulk_write / cache:invalidate) fire over
// real-extracted facts, and the correlation deriver flags a bulk_write anchor whose forward closure lacks a
// same-key cache:invalidate companion. Adding the invalidation call clears it.
//
// The graph is built via the PRODUCTION FactGraphProjection.FromAnalysis (what `rig derive` runs over), NOT
// the FactProjection test fixture graph: the fixture deliberately drops ReceiverType on non-redirected edges,
// and the reach question is answered over this graph. Effects come from FactProjection.Invocations (the same
// invocation facts the production deriver consumes).
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

    // The two ordinary effect rules the cache-coherence reframe rests on (in the real store these live in the
    // rules JSON as ordinary effect rules; here we construct them in-memory to prove the path):
    //   * llblgen:bulk_write — a UpdateMulti call; resource is the RECEIVER type (the collection).
    //   * cache:invalidate — a Clear() call on a *Cache type; resource is the DECLARING type (the cache).
    private static readonly List<FactEffectRule> EffectRules =
    [
        new("llblgen", "bulk_write", ["UpdateMulti"], [], [], Resource: "receiver_type"),
        new("cache", "invalidate", ["Clear"], [], [], DeclaringTypeNameEndsWith: ["Cache"], Resource: "declaring_type"),
    ];

    private static readonly CorrelationSpec Spec = new(
        Anchor: new EffectPredicate("llblgen", "bulk_write"),
        Companion: new EffectPredicate("cache", "invalidate"),
        AnchorNormalize: new NormalizeSpec(SimpleTypeName: true, StripSuffix: ["EntityCollection", "Collection", "DAO"]),
        CompanionNormalize: new NormalizeSpec(SimpleTypeName: true, StripSuffix: ["Cache"])
    );

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

    private static IReadOnlyList<DerivedEffect> Effects(AnalysisResult result) =>
        FactEffectDeriver.Derive(FactProjection.Invocations(result), EffectRules);

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
    public void Effect_rules_fire_into_bulk_write_and_invalidate_effects()
    {
        // The reframe rests on ordinary effect rules producing the anchor + companion. Assert they fire with the
        // expected resolved ResourceType (the value the spec's normalize must reduce to "Account").
        var bugEffects = Effects(Extract(BugSource));
        bugEffects.ShouldContain(e =>
            e.Provider == "llblgen" && e.Operation == "bulk_write" && e.ResourceType.Contains("AccountEntityCollection")
        );

        var fixEffects = Effects(Extract(FixSource));
        fixEffects.ShouldContain(e =>
            e.Provider == "llblgen" && e.Operation == "bulk_write" && e.ResourceType.Contains("AccountEntityCollection")
        );
        fixEffects.ShouldContain(e => e.Provider == "cache" && e.Operation == "invalidate" && e.ResourceType.Contains("AccountCache"));
    }

    [Test]
    public void Bulk_write_without_invalidation_is_a_cache_coherence_finding()
    {
        var result = Extract(BugSource);
        var graph = FactGraphProjection.FromAnalysis(result);

        var findings = FactCorrelationDeriver.Derive(graph: graph, effects: Effects(result), spec: Spec);

        var finding = findings.ShouldHaveSingleItem();
        finding.ResourceKey.ShouldBe("Account");
        finding.Method.ShouldContain("Importer.Run");
    }

    [Test]
    public void Bulk_write_with_invalidation_in_the_same_closure_is_clean()
    {
        var result = Extract(FixSource);
        var graph = FactGraphProjection.FromAnalysis(result);

        var findings = FactCorrelationDeriver.Derive(graph: graph, effects: Effects(result), spec: Spec);

        findings.ShouldBeEmpty();
    }
}

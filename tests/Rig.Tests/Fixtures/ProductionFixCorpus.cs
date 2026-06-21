using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;

namespace Rig.Tests.Fixtures;

// Executable RCA corpus harness. Each production fix from docs/rca-corpus-meddbase.md is reproduced as a
// self-contained bug/fix snippet; this compiles it IN MEMORY (full framework references) and runs the REAL
// extract -> derive pipeline with the SHIPPED builtin rules, returning the derived effects so a corpus test
// can assert what the detectors fire on the BUG vs the FIX. No store, no playground restore — the snippet IS
// the fixture. The point is to replace prose claims ("rig would catch X") with a test that proves whether it
// does, and to pin the known GAPS (a bug the current detectors miss) as explicit, named expectations.
public static class ProductionFixCorpus
{
    // Reference every assembly the test runtime trusts (System.Collections.Concurrent, Immutable, etc.) so a
    // snippet can use real BCL concurrency types — only third-party idioms (LanguageExt.Atom) need a stub.
    private static readonly MetadataReference[] FrameworkReferences = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    // A minimal LanguageExt.Atom<A> stub with the FQN the shared_state(Atom) rule gates on. Faithful to the
    // !10706 fix surface: Swap(func) is the atomic read-modify-write. Prepended to any snippet that uses it.
    // Option<A> is the FR-6 (RCA #1646) hazard payload: the object-store serializer can write it but cannot
    // read it back (None must be null), so a stored Option<T> is a latent serialization-contract defect.
    public const string LanguageExtStub = """
        namespace LanguageExt
        {
            public sealed class Atom<A>
            {
                private A _value;
                public Atom(A value) => _value = value;
                public A Value => _value;
                public A Swap(System.Func<A, A> f) { _value = f(_value); return _value; }
            }
            public static class Atom
            {
                public static Atom<A> Create<A>(A value) => new Atom<A>(value);
            }
            public struct Option<A>
            {
                public bool IsSome => false;
            }
        }
        """;

    public sealed record CorpusResult(IReadOnlyList<DerivedEffect> Effects)
    {
        // Every effect whose enclosing method DocID contains the marker (a method name distinguishes the bug
        // variant from the fix variant in the same snippet).
        public IReadOnlyList<DerivedEffect> EffectsIn(string enclosingMarker) =>
            Effects.Where(e => (e.EnclosingSymbolId ?? "").Contains(enclosingMarker, StringComparison.Ordinal)).ToList();

        public IReadOnlyList<DerivedEffect> SharedStateMutationsIn(string enclosingMarker) =>
            EffectsIn(enclosingMarker).Where(e => e.Provider == "shared_state" && e.Operation == "mutate").ToList();

        public bool HasGuardEffectIn(string enclosingMarker) => EffectsIn(enclosingMarker).Any(e => e.Provider is "lock" or "async_lock");

        // Every serialization_hazard observation attached to an effect enclosed by the marker method.
        public IReadOnlyList<EffectObservationInfo> SerializationHazardsIn(string enclosingMarker) =>
            EffectsIn(enclosingMarker).SelectMany(e => e.Observations ?? []).Where(o => o.Type == "serialization_hazard").ToList();
    }

    public static CorpusResult Analyze(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Corpus.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "Corpus",
            syntaxTrees: [tree],
            references: FrameworkReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var model = compilation.GetSemanticModel(tree);
        var extraction = FactExtractor.Extract(
            new SourceModel(ProjectName: "Corpus", FilePath: "Corpus.cs", Tree: tree, Root: tree.GetRoot(), SemanticModel: model),
            new SymbolStringCache()
        );

        var result = new AnalysisResult(
            SolutionPath: "Corpus",
            SourceFiles: [],
            DiRegistrations: [],
            Symbols: extraction.Symbols,
            References: extraction.References,
            TypeRelations: extraction.TypeRelations,
            DispatchFacts: extraction.Dispatch
        );

        var rules = LoadBuiltinRules();
        var epData = FactProjection.EntryPointData(result);
        var effects = FactEffectDeriver.Derive(
            FactProjection.Invocations(result),
            rules.Effects,
            providerFilter: null,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs,
            observationRules: rules.Observations,
            throwRefs: FactProjection.ThrowRefs(result),
            staticFieldWriteRefs: StaticFieldWriteRefs(result)
        );
        return new CorpusResult(effects);
    }

    // Builtin-only rule set (no colocated/global overlay): load rooted at an empty temp dir so the only rules
    // are the shipped builtin-rules.json — the corpus measures what we SHIP, not a dev's local rules.
    private static Rig.Domain.Data.RuleSet LoadBuiltinRules()
    {
        var tempDir = Directory.CreateTempSubdirectory("rig-corpus-rules-").FullName;
        try
        {
            return RuleSetLoader.Load(tempDir);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException) { }
        }
    }

    // Mirror of Reads.LoadStaticFieldWriteRefsAsync over the in-memory facts: write refs whose target is a
    // STATIC field/auto-property slot (gated via the symbol's modifiers), deduped by site. This is the FR-1(b)
    // input population — and it only works because the field-emission fix now emits class field symbols.
    private static IReadOnlyList<FactFieldWrite> StaticFieldWriteRefs(AnalysisResult result)
    {
        var staticSlots = (result.Symbols ?? [])
            .Where(s => s.Modifiers.Contains("static", StringComparison.Ordinal))
            .Select(s => s.SymbolId)
            .ToHashSet(StringComparer.Ordinal);

        return (result.References ?? [])
            .Where(r =>
                r.RefKind == RefKinds.Write && r.TargetInSource && r.EnclosingSymbolId != null && staticSlots.Contains(r.TargetSymbolId)
            )
            .GroupBy(r => (r.FilePath, r.Line, r.TargetSymbolId))
            .Select(g => g.First())
            .Select(r => new FactFieldWrite(
                Target: r.TargetSymbolId,
                Enclosing: r.EnclosingSymbolId,
                FilePath: r.FilePath,
                Line: r.Line,
                LoopKind: r.EnclosingLoopKind,
                LoopDetail: r.EnclosingLoopDetail,
                EnclosingInvocations: r.EnclosingInvocations,
                CatchTypes: r.EnclosingCatchTypes,
                EnclosingScopes: r.EnclosingScopes
            ))
            .ToList();
    }
}

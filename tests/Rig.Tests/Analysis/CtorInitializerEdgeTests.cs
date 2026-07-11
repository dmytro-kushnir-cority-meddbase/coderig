using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Analysis;

// `: this(...)` / `: base(...)` ctor chaining carries no creation or name syntax, so pre-fix the
// extractor emitted NO reference for it and the declaring-ctor -> chained-ctor call edge was missing —
// reverse reachability (`callers`) silently stopped at the invoked overload (the MedDBase
// InvoiceEntity ctor-chain false negative, docs/backlog/todo/ctor-initializer-call-edges.md).
public sealed class CtorInitializerEdgeTests
{
    // Mirrors the InvoiceEntity shape: an overload chain ending in a body that calls the worker,
    // a caller constructing via the SHORT overload, and a derived type chaining via `: base(...)`.
    private const string Source = """
        namespace App
        {
            public class Invoice
            {
                public Invoice(int a) : this(a, 0) { }
                public Invoice(int a, int b) : this(a, b, 0) { }
                public Invoice(int a, int b, int c) { Initialise(); }
                protected void Initialise() { GroupByAccount(); }
                private void GroupByAccount() { }
            }

            public class Payments
            {
                public void MakePaymentFee() { _ = new Invoice(1); }
            }

            public class CreditNote : Invoice
            {
                public CreditNote() : base(2) { }
            }
        }
        """;

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

    private static FactGraphData Graph(FactExtractionResult extracted) =>
        FactGraphProjection.FromAnalysis(
            new AnalysisResult(
                SolutionPath: "Snippet",
                SourceFiles: [],
                DiRegistrations: [],
                Symbols: extracted.Symbols,
                References: extracted.References,
                TypeRelations: extracted.TypeRelations,
                DispatchFacts: extracted.Dispatch
            )
        );

    [Test]
    public void This_initializer_emits_a_ctor_ref_from_the_declaring_ctor_to_the_chained_overload()
    {
        var result = Extract(Source);

        var chained = result.References.Single(r =>
            r.RefKind == RefKinds.Ctor
            && r.EnclosingSymbolId == "M:App.Invoice.#ctor(System.Int32)"
            && r.TargetSymbolId == "M:App.Invoice.#ctor(System.Int32,System.Int32)"
        );
        chained.TargetInSource.ShouldBeTrue();
    }

    [Test]
    public void Base_initializer_emits_a_ctor_ref_from_the_derived_ctor_to_the_base_ctor()
    {
        var result = Extract(Source);

        var chained = result.References.Single(r =>
            r.RefKind == RefKinds.Ctor
            && r.EnclosingSymbolId == "M:App.CreditNote.#ctor"
            && r.TargetSymbolId == "M:App.Invoice.#ctor(System.Int32)"
        );
        chained.TargetInSource.ShouldBeTrue();
    }

    [Test]
    public void Reverse_reach_traverses_the_ctor_overload_chain_to_the_new_site()
    {
        var callers = FactPathFinder.ReachedBy(Graph(Extract(Source)), "M:App.Invoice.GroupByAccount");

        // Pre-fix: the walk climbed GroupByAccount <- Initialise <- 3-arg ctor and STOPPED — the
        // 3-arg -> 2-arg -> 1-arg chain hops did not exist, so neither caller below was reported.
        callers.Keys.ShouldContain("M:App.Payments.MakePaymentFee"); // via `new Invoice(1)` -> `: this` chain
        callers.Keys.ShouldContain("M:App.CreditNote.#ctor"); // via `: base(2)` -> `: this` chain
    }
}

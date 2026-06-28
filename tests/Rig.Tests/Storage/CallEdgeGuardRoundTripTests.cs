using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Domain.Data;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Shouldly;

namespace Rig.Tests.Storage;

// Regression for the branch-aware-effects (tree --guards) round-trip. Increment 2 wired
// ReferenceFact.EnclosingGuards through reference_facts (Writes/Reads), but the DERIVED call_edges view —
// built by GraphMaterializer and read by SqlReachability.LoadBoundedGraphAsync, which is the path `tree`/
// `reaches` actually load when graph views exist — had no guards column, so every guard was silently
// dropped at query time (the glyph never rendered). This pins the full materialize -> bounded-load
// round-trip: a guarded call SITE keeps its frozen guard set, and a must-run site stays unguarded.
public sealed class CallEdgeGuardRoundTripTests
{
    private static AnalysisResult Analyze(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Snippet.cs");
        var compilation = CSharpCompilation.Create(
            "Snippet",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var model = compilation.GetSemanticModel(tree);
        var extracted = FactExtractor.Extract(
            new SourceModel("Snippet", "Snippet.cs", tree, tree.GetRoot(), model),
            new SymbolStringCache()
        );
        return new AnalysisResult(
            SolutionPath: "Snippet",
            SourceFiles: [],
            DiRegistrations: [],
            Symbols: extracted.Symbols,
            References: extracted.References,
            TypeRelations: extracted.TypeRelations,
            DispatchFacts: extracted.Dispatch
        );
    }

    [Test]
    public async Task Guards_survive_the_call_edges_materialize_and_bounded_load()
    {
        // Always() is unconditional (must-run); Guarded() runs only under `if (flag)`.
        var result = Analyze(
            """
            namespace App
            {
                public sealed class Svc
                {
                    public void Handle(bool flag)
                    {
                        Always();
                        if (flag) Guarded();
                    }

                    private void Always() {}
                    private void Guarded() {}
                }
            }
            """
        );

        var directory = Path.Combine(Path.GetTempPath(), "rig-guard-roundtrip-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "rig.db");
        try
        {
            await using (var write = new RigDbContext(databasePath, pooling: false))
            {
                await Writes.SaveAsync(write, result);
            }

            await using (var build = new RigDbContext(databasePath, pooling: false))
            {
                await GraphMaterializer.BuildAsync(build);
            }

            await using (var read = new RigDbContext(databasePath, pooling: false))
            {
                // The path `tree`/`reaches` load: the SQL-bounded graph from the materialized call_edges view.
                var graph = await SqlReachability.LoadBoundedGraphAsync(read, "Svc.Handle", SqlReachability.Direction.Forward);

                var toAlways = graph.CallEdges.FirstOrDefault(e => e.Callee.Contains("Always", StringComparison.Ordinal));
                var toGuarded = graph.CallEdges.FirstOrDefault(e => e.Callee.Contains("Guarded", StringComparison.Ordinal));
                toAlways.ShouldNotBeNull("the unconditional Always() edge must be in the bounded graph");
                toGuarded.ShouldNotBeNull("the guarded Guarded() edge must be in the bounded graph");

                // Must-run keeps no guard; the guarded site round-trips its predicate + polarity through SQL.
                toAlways!.EnclosingGuards.ShouldBeNull();
                toGuarded!.EnclosingGuards.ShouldNotBeNull("the guard set was dropped by the call_edges view");
                var decoded = FactStructuralContext.DecodeGuards(toGuarded.EnclosingGuards);
                decoded.Count.ShouldBe(1);
                decoded[0].Predicate.ShouldContain("flag");
                decoded[0].WhenTrue.ShouldBeTrue();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            { /* best-effort cleanup */
            }
        }
    }
}

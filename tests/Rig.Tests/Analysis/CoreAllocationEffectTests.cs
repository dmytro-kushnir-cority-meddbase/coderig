using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Cli.Effects;
using Rig.Domain.Data;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Shouldly;

namespace Rig.Tests.Analysis;

public sealed class CoreAllocationEffectTests
{
    private static readonly FactObservationRules EmptyObservations = new([], [], [], [], [], []);

    private static FactExtractionResult Extract(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Snippet.cs");
        var compilation = CSharpCompilation.Create(
            "Snippet",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );
        var diagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        diagnostics.ShouldBeEmpty(string.Join(Environment.NewLine, diagnostics));
        var model = compilation.GetSemanticModel(tree);
        return FactExtractor.Extract(new SourceModel("Snippet", "Snippet.cs", tree, tree.GetRoot(), model), new SymbolStringCache());
    }

    [Test]
    public void Reference_object_and_array_sites_are_core_facts_but_value_and_stackalloc_sites_are_not()
    {
        var facts = Extract(
            """
            namespace App;
            public sealed class RefType { }
            public struct ValueType { }
            public sealed class Cases
            {
                public unsafe void Run()
                {
                    RefType explicitObject = new RefType();
                    RefType targetTyped = new();
                    ValueType value = new ValueType();
                    int[] explicitArray = new int[2];
                    int[] implicitArray = new[] { 1, 2 };
                    int* stack = stackalloc int[2];
                }
            }
            """
        );

        facts.Allocations.Count(a => a.Operation == "object" && a.ResourceType == "App.RefType").ShouldBe(2);
        facts.Allocations.Count(a => a.Operation == "array" && a.ResourceType == "int[]").ShouldBe(2);
        facts.Allocations.ShouldNotContain(a => a.ResourceType.Contains("ValueType", StringComparison.Ordinal));
        facts.Allocations.Count.ShouldBe(4);
    }

    [Test]
    public void Boxing_is_based_on_roslyn_conversion_semantics()
    {
        var facts = Extract(
            """
            namespace App;
            public interface IMarker { }
            public struct Value : IMarker { }
            public sealed class Cases
            {
                private static T Identity<T>(T value) where T : struct => value;

                public void Run()
                {
                    Value value = default;
                    object boxedObject = value;
                    IMarker boxedInterface = value;
                    object referenceConversion = "text";
                    Value genericControl = Identity(value);
                }
            }
            """
        );

        var boxing = facts.Allocations.Where(a => a.Operation == "boxing").ToList();
        boxing.Count.ShouldBe(2);
        boxing.ShouldAllBe(a => a.ResourceType == "App.Value");
    }

    [Test]
    public void Attribute_metadata_does_not_emit_runtime_allocation_facts()
    {
        var facts = Extract(
            """
            using System;
            namespace App;
            public sealed class ValuesAttribute : Attribute
            {
                public ValuesAttribute(object value, int[] values) { }
            }
            [Values(42, new[] { 1, 2 })]
            public sealed class Cases { }
            """
        );

        facts.Allocations.ShouldBeEmpty();
    }

    [Test]
    public void Loop_and_guard_evidence_flow_to_the_ordinary_effect_shape()
    {
        var facts = Extract(
            """
            namespace App;
            public sealed class Item { }
            public sealed class Cases
            {
                public void Run(bool enabled)
                {
                    Item hoisted = new();
                    for (var i = 0; i < 2; i++)
                    {
                        Item looped = new();
                    }
                    if (enabled)
                    {
                        Item guarded = new();
                    }
                }
            }
            """
        );

        var effects = EffectDerivation.DeriveEffects(
            effectRules: [],
            observationRules: EmptyObservations,
            invocations: [],
            baseEdges: [],
            ctorRefs: [],
            throwRefs: [],
            allocationFacts: facts.Allocations
        );

        effects.Count.ShouldBe(3);
        effects.Single(e => e.Line == 7).Observations.ShouldBeEmpty();
        effects.Single(e => e.Line == 10).Observations!.ShouldContain(o => o.Type == "looped_effect" && o.Context == "for");
        var guarded = effects.Single(e => e.Line == 14);
        guarded.EnclosingGuards.ShouldNotBeNull();
        FactStructuralContext.DecodeGuards(guarded.EnclosingGuards).ShouldContain(g => g.Predicate.Contains("enabled"));
    }

    [Test]
    public void User_effect_rules_do_not_change_core_allocation_effects()
    {
        var allocation = new AllocationFact("object", "App.Item", "M:App.Cases.Run", "Snippet.cs", 4);
        var unrelated = new FactEffectRule("custom", "write", ["Save"], ["App.Repository"], []);

        IReadOnlyList<DerivedEffect> Derive(IReadOnlyList<FactEffectRule> rules) =>
            EffectDerivation.DeriveEffects(
                effectRules: rules,
                observationRules: EmptyObservations,
                invocations: [],
                baseEdges: [],
                ctorRefs: [],
                throwRefs: [],
                allocationFacts: [allocation]
            );

        DerivedEffect Key(DerivedEffect effect) => effect with { Observations = null };
        Derive([]).Select(Key).ShouldBe(Derive([unrelated]).Select(Key));
    }

    [Test]
    public async Task Allocation_facts_round_trip_through_whole_store_and_bounded_reach_inputs()
    {
        var extracted = Extract(
            """
            namespace App;
            public sealed class Item { }
            public sealed class Cases
            {
                public void Root() { Item item = new(); }
                public void Unreachable() { Item item = new(); }
            }
            """
        );
        var result = new AnalysisResult(
            SolutionPath: "Snippet",
            SourceFiles: [],
            DiRegistrations: [],
            Symbols: extracted.Symbols,
            References: extracted.References,
            TypeRelations: extracted.TypeRelations,
            DispatchFacts: extracted.Dispatch,
            AllocationFacts: extracted.Allocations
        );
        var directory = Path.Combine(Path.GetTempPath(), "rig-allocation-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "rig.db");

        try
        {
            await using (var write = new RigDbContext(databasePath, pooling: false))
            {
                await Writes.SaveAsync(write, result);
            }
            await using (var graph = new RigDbContext(databasePath, pooling: false))
            {
                await GraphMaterializer.BuildAsync(graph);
            }
            await using (var read = new RigDbContext(databasePath, pooling: false))
            {
                var whole = await Reads.LoadAllocationFactsAsync(read);
                whole.Count.ShouldBe(2);

                var bounded = await SqlReachability.LoadReachInputsAsync(read, "Cases.Root", SqlReachability.Direction.Forward);
                bounded.AllocationFacts.Count.ShouldBe(1);
                bounded.AllocationFacts[0].EnclosingSymbolId.ShouldContain("Root");
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

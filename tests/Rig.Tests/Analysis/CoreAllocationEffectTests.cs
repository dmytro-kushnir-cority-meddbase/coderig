using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Cli.Effects;
using Rig.Domain.Data;
using Rig.Domain.Functions;
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
        facts.Allocations.Where(a => a.Operation == "object").ShouldAllBe(a => a.Mechanism == "object_creation");
        facts.Allocations.Where(a => a.Operation == "array").ShouldAllBe(a => a.Mechanism == "array_creation");
        facts.Allocations.ShouldAllBe(a => a.Cardinality == "per_evaluation" && a.ShallowSizeBytes > 0);
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
        boxing.ShouldAllBe(a => a.Mechanism == "boxing" && a.ShallowSizeBytes == 24);
    }

    [Test]
    public void Nullable_boxing_is_conditional_and_boxes_the_underlying_value_type()
    {
        var facts = Extract(
            """
            namespace App;
            public sealed class Cases
            {
                public object Run(int? value) => value;
            }
            """
        );

        var boxing = facts.Allocations.Single(a => a.Mechanism == "boxing");
        boxing.ResourceType.ShouldBe("int");
        boxing.Cardinality.ShouldBe("conditional");
        boxing.ShallowSizeBytes.ShouldBe(24);
    }

    [Test]
    public void Cached_first_use_delegate_inside_a_loop_is_not_reported_as_loop_amplified()
    {
        var facts = Extract(
            """
            using System;
            namespace App;
            public sealed class Cases
            {
                private static void Target() { }
                public void Run()
                {
                    for (var i = 0; i < 2; i++)
                    {
                        Action action = Target;
                        action();
                    }
                }
            }
            """
        );

        var effects = CoreAllocationEffectDeriver.Derive(facts.Allocations, EmptyObservations);
        var cached = effects.Single(e => e.Mechanism == "delegate");
        cached.Cardinality.ShouldBe("cached_first_use");
        (cached.Observations ?? []).ShouldNotContain(observation => observation.Type == "looped_effect");
        facts.Allocations.Single(a => a.Mechanism == "delegate").EnclosingLoopKind.ShouldBe("for");
    }

    [Test]
    public void Expanded_params_arrays_are_detected_but_existing_and_omitted_arrays_are_not()
    {
        var facts = Extract(
            """
            namespace App;
            public sealed class Cases
            {
                private static int Sum(params int[] values) => values.Length;
                public int Expanded() => Sum(1, 2, 3);
                public int Existing(int[] values) => Sum(values);
                public int Omitted() => Sum();
            }
            """
        );

        var allocation = facts.Allocations.Single();
        allocation.Operation.ShouldBe("array");
        allocation.Mechanism.ShouldBe("implicit_params");
        allocation.Cardinality.ShouldBe("per_evaluation");
        allocation.EnclosingSymbolId.ShouldContain("Expanded");
        allocation.ShallowSizeBytes.ShouldBe(40);
    }

    [Test]
    public void Iterator_allocation_is_owned_by_the_caller_and_async_is_not_inferred()
    {
        var facts = Extract(
            """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            namespace App;
            public sealed class Cases
            {
                private static IEnumerable<int> Values() { yield return 1; }
                public IEnumerable<int> Call() => Values();
                public async Task<int> AsyncControl() { await Task.Yield(); return 1; }
            }
            """
        );

        var allocation = facts.Allocations.Single(a => a.Mechanism == "iterator_state_machine");
        allocation.EnclosingSymbolId.ShouldContain("Call");
        allocation.ResourceType.ShouldContain("Values");
        allocation.ResourceType.ShouldContain("~iterator");
        allocation.ShallowSizeBytes.ShouldBeNull();
        facts.Allocations.ShouldNotContain(a => a.EnclosingSymbolId.Contains("AsyncControl", StringComparison.Ordinal));
    }

    [Test]
    public void Delegate_and_closure_evidence_has_outer_ownership_and_honest_cardinality()
    {
        var facts = Extract(
            """
            using System;
            namespace App;
            public sealed class Cases
            {
                private static int StaticTarget() => 1;
                private int InstanceTarget() => 2;
                public Func<int> Capturing(int value) => () => value;
                public (Func<int>, Func<int>) SharedClosure(int value) => (() => value, () => value + 1);
                public Func<int> CapturingLocalFunction(int value)
                {
                    int Local() => value;
                    return Local;
                }
                public (Func<int>, Func<int>) SeparateScopes()
                {
                    Func<int> first;
                    { int value = 1; first = () => value; }
                    Func<int> second;
                    { int value = 2; second = () => value; }
                    return (first, second);
                }
                public Func<int> NonCapturing() => static () => 1;
                public Func<int> StaticGroup() => StaticTarget;
                public Func<int> InstanceGroup() => InstanceTarget;
                public Func<int> Explicit() => new Func<int>(StaticTarget);
            }
            """
        );

        var allocations = facts.Allocations;
        allocations.Count(a => a.Mechanism == "closure").ShouldBe(5);
        allocations.Where(a => a.Mechanism == "closure").ShouldAllBe(a => a.Cardinality == "per_scope");
        allocations.Count(a => a.Mechanism == "closure" && a.EnclosingSymbolId.Contains("SharedClosure")).ShouldBe(1);
        allocations.Count(a => a.Mechanism == "closure" && a.EnclosingSymbolId.Contains("CapturingLocalFunction")).ShouldBe(1);
        allocations.Count(a => a.Mechanism == "closure" && a.EnclosingSymbolId.Contains("SeparateScopes")).ShouldBe(2);
        allocations
            .Single(a => a.Mechanism == "delegate" && a.EnclosingSymbolId.Contains(".Capturing(", StringComparison.Ordinal))
            .Cardinality.ShouldBe("per_evaluation");
        allocations
            .Single(a => a.Mechanism == "delegate" && a.EnclosingSymbolId.Contains("CapturingLocalFunction", StringComparison.Ordinal))
            .Cardinality.ShouldBe("per_evaluation");
        allocations
            .Single(a => a.Mechanism == "delegate" && a.EnclosingSymbolId.Contains("NonCapturing"))
            .Cardinality.ShouldBe("cached_first_use");
        allocations
            .Single(a => a.Mechanism == "delegate" && a.EnclosingSymbolId.Contains("StaticGroup"))
            .Cardinality.ShouldBe("cached_first_use");
        allocations
            .Single(a => a.Mechanism == "delegate" && a.EnclosingSymbolId.Contains("InstanceGroup"))
            .Cardinality.ShouldBe("per_evaluation");
        allocations.Count(a => a.EnclosingSymbolId.Contains("Explicit")).ShouldBe(1);
        allocations.Single(a => a.EnclosingSymbolId.Contains("Explicit")).Mechanism.ShouldBe("object_creation");
    }

    [Test]
    public void String_range_concat_and_interpolation_are_detected_with_constant_and_span_controls()
    {
        var facts = Extract(
            """
            using System;
            namespace App;
            public sealed class Cases
            {
                public string Range(string raw) => raw[7..];
                public string RangeVariable(string raw, Range range) => raw[range];
                public string ConstantRange() => "raw-end-tag"[4..7];
                public string FullRange(string raw) => raw[..];
                public string EmptyRange(string raw) => raw[1..1];
                public ReadOnlySpan<char> Span(string raw) => raw.AsSpan(7);
                public string Concat(string raw, int n) => (raw + ":") + n;
                public string Interpolate(string raw, int n) => $"{raw}:{n}";
                public FormattableString Formattable(string raw, int n) => $"{raw}:{n}";
                public string Constants()
                {
                    const string suffix = "tag";
                    const string interpolation = $"raw{suffix}";
                    return "raw" + "tag" + interpolation;
                }
            }
            """
        );

        facts.Allocations.Count(a => a.Mechanism == "string_range").ShouldBe(3);
        facts.Allocations.Count(a => a.Mechanism == "string_concat").ShouldBe(1);
        facts.Allocations.Count(a => a.Mechanism == "string_interpolation").ShouldBe(1);
        facts
            .Allocations.Where(a => a.Mechanism!.StartsWith("string_", StringComparison.Ordinal))
            .ShouldAllBe(a => (a.ResourceType == "string" || a.ResourceType == "System.String") && a.Cardinality == "conditional");
        facts.Allocations.ShouldNotContain(a => a.EnclosingSymbolId.Contains("Span", StringComparison.Ordinal));
        facts.Allocations.ShouldNotContain(a => a.EnclosingSymbolId.Contains("FullRange", StringComparison.Ordinal));
        facts.Allocations.ShouldNotContain(a => a.EnclosingSymbolId.Contains("EmptyRange", StringComparison.Ordinal));
        facts.Allocations.ShouldNotContain(a => a.EnclosingSymbolId.Contains("Constants", StringComparison.Ordinal));
        facts.Allocations.ShouldNotContain(a =>
            a.Mechanism == "string_interpolation" && a.EnclosingSymbolId.Contains("Formattable", StringComparison.Ordinal)
        );
        var knownRange = facts.Allocations.Single(a => a.EnclosingSymbolId.Contains("ConstantRange", StringComparison.Ordinal));
        knownRange.ShallowSizeBytes.ShouldBe(32);
        knownRange.SizeConfidence.ShouldBe("estimated");
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
                bounded.AllocationFacts[0].Mechanism.ShouldBe("object_creation");
                bounded.AllocationFacts[0].Cardinality.ShouldBe("per_evaluation");
                bounded.AllocationFacts[0].ShallowSizeBytes.ShouldNotBeNull();
                bounded.AllocationFacts[0].SizeConfidence.ShouldBe("estimated");
                bounded.AllocationFacts[0].SizeBasis.ShouldNotBeNull();
                bounded.AllocationFacts[0].SizeBasis!.ShouldContain("x64");
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

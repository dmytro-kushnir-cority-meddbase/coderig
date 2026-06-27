using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Rig.Analysis.Extraction;

namespace Rig.Benchmarks;

// Wallclock AND allocation profile of the control-dependence engine on a single method's CFG — the unit
// of work the extractor will run per effect-bearing body. No store/IO: the CFG is built once in setup; the
// [Benchmark]s measure pure compute. [MemoryDiagnoser] is the point — allocations are a wallclock proxy on
// the hot path, and this is the evidence for the production algorithm choice (the current reachability
// "delete-test" allocates a HashSet+Stack per query and is ~O(V^2*E); a dominator/post-dominator tree
// computed once per CFG should flatten both wallclock and allocs, especially as Branches grows).
//
//   dotnet run -c Release --project bench/Rig.Benchmarks -- --filter *ControlDependence*
[MemoryDiagnoser]
public class ControlDependenceBenchmarks
{
    // Branch count drives CFG size — the axis where the delete-test's O(V^2*E) should bend.
    [Params(8, 32, 128)]
    public int Branches;

    private ControlFlowGraph _cfg = null!;
    private int[] _effectBlocks = null!; // distinct blocks holding a call-site (the per-effect guard workload)

    [GlobalSetup]
    public void Setup()
    {
        // A realistic-ish body: a run of independent guard clauses + one nested region + a loop + a switch
        // — a spread of branch shapes, each gating a call (the effect). Helpers are generated to match.
        var body = new StringBuilder();
        var helpers = new StringBuilder();
        for (var i = 0; i < Branches; i++)
        {
            body.AppendLine($"        if (p{i % 4}) Call{i}();");
            helpers.AppendLine($"    private void Call{i}() {{}}");
        }
        body.AppendLine("        for (int j = 0; j < n; j++) Loop();");
        body.AppendLine("        switch (k) { case 1: One(); break; case 2: Two(); break; default: Def(); break; }");
        helpers.AppendLine("    private void Loop() {} private void One() {} private void Two() {} private void Def() {}");

        var source = $$"""
            public sealed class C
            {
                public void M(bool p0, bool p1, bool p2, bool p3, int n, int k)
                {
            {{body}}
                }
            {{helpers}}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName: "Bench",
            syntaxTrees: [tree],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "M");
        _cfg = ControlFlowGraph.Create((IMethodBodyOperation)model.GetOperation(method)!);

        _effectBlocks = method
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(inv => ControlDependence.BlockOf(_cfg, inv))
            .Where(b => b >= 0)
            .Distinct()
            .ToArray();
    }

    // The must-run spine alone (dominator query for every block).
    [Benchmark]
    public int MustRun() => ControlDependence.MustRunBlocks(_cfg).Count;

    // The guard set for every effect-bearing call-site (post-dominance queries) — the heavier half.
    [Benchmark]
    public int GuardsForEveryEffect()
    {
        var total = 0;
        foreach (var block in _effectBlocks)
        {
            total += ControlDependence.GuardsOf(_cfg, block).Count;
        }

        return total;
    }

    // The full per-method extraction workload: spine + every effect's guard set. This is the number that
    // multiplies across ~every effect-bearing method in the monorepo at index time.
    [Benchmark(Baseline = true)]
    public int FullMethodAnalysis()
    {
        var total = ControlDependence.MustRunBlocks(_cfg).Count;
        foreach (var block in _effectBlocks)
        {
            total += ControlDependence.GuardsOf(_cfg, block).Count;
        }

        return total;
    }
}

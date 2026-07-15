using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rig.Analysis;
using Rig.Analysis.Extraction;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// The delegate-field join: a delegate FIELD invoked as `field(...)` reaches the callable(s) ASSIGNED to
// that field, as a DIRECT invoking-method -> callable edge (Kind = "delegate-field") — the seam the
// forward walk otherwise cuts at (the motivating DFS `saveFunc()` -> the assigned Azure-blob lambda). The
// fixtures mirror that shape: a static delegate field, a callable assigned in one method, the field
// invoked from another method of the SAME type. Assertions run over the SHIPPED engine
// (FactGraphProjection.FromAnalysis + FactPathFinder.Reaches), not a hand-rolled walk.
public sealed class DelegateFieldJoinTests
{
    private static readonly MetadataReference[] FrameworkReferences = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    private static FactGraphData Graph(string source)
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

        var result = new AnalysisResult(
            SolutionPath: "App",
            SourceFiles: [],
            DiRegistrations: [],
            Symbols: extraction.Symbols,
            References: extraction.References,
            TypeRelations: extraction.TypeRelations,
            DispatchFacts: extraction.Dispatch
        );
        return FactGraphProjection.FromAnalysis(result);
    }

    private static IReadOnlyList<CallEdge> DelegateFieldEdges(FactGraphData graph) =>
        graph.CallEdges.Where(e => e.Kind == EdgeKinds.DelegateField).ToList();

    private static string MethodId(FactGraphData graph, string nameContains) =>
        graph.Methods.Single(m => m.SymbolId.Contains(nameContains, StringComparison.Ordinal)).SymbolId;

    // The DFS shape, minimized: a static delegate field assigned a LAMBDA in Init, invoked from Save.
    private const string LambdaFixture = """
        namespace App
        {
            public static class Dfs
            {
                static System.Func<int, int> saveFunc;
                static void Init() { saveFunc = x => Sink.Blob(x); }
                public static int Save(int v) => saveFunc(v);
            }

            public static class Sink { public static int Blob(int x) => x; }
        }
        """;

    [Test]
    public void Join_edge_connects_the_invoking_method_to_the_assigned_lambda()
    {
        var graph = Graph(LambdaFixture);

        var join = DelegateFieldEdges(graph).ShouldHaveSingleItem();
        join.Caller.ShouldContain("Dfs.Save");
        join.Callee.ShouldContain("Dfs.Init"); // the lambda's synthetic id is rooted on its enclosing member
        join.Callee.ShouldContain("~λ");
    }

    [Test]
    public void Effect_inside_the_lambda_is_reachable_from_the_invoking_method()
    {
        var graph = Graph(LambdaFixture);

        var reach = FactPathFinder.Reaches(graph, MethodId(graph, "Dfs.Save"));

        // The lambda node AND the effectful call inside its body both surface across the join edge.
        reach.Keys.ShouldContain(k => k.Contains("Dfs.Init") && k.Contains("~λ"));
        reach.Keys.ShouldContain(k => k.Contains("Sink.Blob"));
    }

    [Test]
    public void Method_group_assigned_to_the_field_joins_too()
    {
        var graph = Graph(
            """
            namespace App
            {
                public static class Dfs
                {
                    static System.Func<int, int> saveFunc;
                    static void Init() { saveFunc = Sink.Blob; }
                    public static int Save(int v) => saveFunc(v);
                }

                public static class Sink { public static int Blob(int x) => x; }
            }
            """
        );

        var join = DelegateFieldEdges(graph).ShouldHaveSingleItem();
        join.Caller.ShouldContain("Dfs.Save");
        join.Callee.ShouldContain("Sink.Blob");

        FactPathFinder.Reaches(graph, MethodId(graph, "Dfs.Save")).Keys.ShouldContain(k => k.Contains("Sink.Blob"));
    }

    [Test]
    public void No_join_when_an_assignment_site_lies_outside_the_declaring_type()
    {
        // saveFunc is public and assigned from Configurer (a DIFFERENT type) — an escape. The field is no
        // longer a controlled seam, so the cut is left in place (disclosed residual).
        var graph = Graph(
            """
            namespace App
            {
                public static class Dfs
                {
                    public static System.Func<int, int> saveFunc;
                    public static int Save(int v) => saveFunc(v);
                }

                public static class Configurer
                {
                    public static void Wire() { Dfs.saveFunc = x => Sink.Blob(x); }
                }

                public static class Sink { public static int Blob(int x) => x; }
            }
            """
        );

        DelegateFieldEdges(graph).ShouldBeEmpty();
    }

    [Test]
    public void No_join_for_an_event_field()
    {
        // An event is modeled by event_raise, not this join — never joined here, even raised + subscribed
        // entirely within the declaring type.
        var graph = Graph(
            """
            namespace App
            {
                public static class Dfs
                {
                    public static event System.Action Fired;
                    public static void Wire() { Fired += () => Sink.Ping(); }
                    public static void Raise() { Fired(); }
                }

                public static class Sink { public static void Ping() { } }
            }
            """
        );

        DelegateFieldEdges(graph).ShouldBeEmpty();
    }
}

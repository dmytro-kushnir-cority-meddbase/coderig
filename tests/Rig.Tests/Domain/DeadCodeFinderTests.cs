using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Unit tests for the unreachable-symbol / dead-code finder (powers `rig dead`, Task #7). Pure
// in-memory graph + method metadata — no Roslyn, no SQLite. Validates: reachability from roots
// (incl. dispatch via FactPathFinder), accessibility-based tiering, the dead-cluster invariant, the
// application-vs-library root treatment, and the off-graph exclusions (ctor/accessor/operator/
// abstract/override/generated).
public sealed class DeadCodeFinderTests
{
    private static DeadCodeFinder.MethodMeta Meta(
        string id,
        string name,
        string modifiers,
        bool isOverride = false,
        bool generated = false
    ) => new(id, name, modifiers, "f.cs", 1, isOverride, generated);

    private static FactGraphData Graph(MethodRef[] methods, params CallEdge[] edges) =>
        new(edges, System.Array.Empty<ImplementsEdge>(), methods);

    private static MethodRef M(string id, string name) => new(id, name, null);

    [Fact]
    public void Method_reached_from_a_root_is_not_dead_transitively()
    {
        // Root -> Used -> AlsoUsed ; Orphan is reached by nobody.
        var methods = new[] { M("M:Root.Run", "Run"), M("M:A.Used", "Used"), M("M:A.AlsoUsed", "AlsoUsed"), M("M:A.Orphan", "Orphan") };
        var graph = Graph(
            methods,
            new CallEdge("M:Root.Run", "M:A.Used", "invocation", "f.cs", 1),
            new CallEdge("M:A.Used", "M:A.AlsoUsed", "invocation", "f.cs", 2)
        );
        var meta = new[]
        {
            Meta("M:Root.Run", "Run", "public"),
            Meta("M:A.Used", "Used", "private"),
            Meta("M:A.AlsoUsed", "AlsoUsed", "private"),
            Meta("M:A.Orphan", "Orphan", "private"),
        };

        var dead = DeadCodeFinder.Find(graph, new[] { "M:Root.Run" }, meta, treatExternallyVisibleAsRoots: false);

        dead.Select(d => d.SymbolId).ShouldBe(new[] { "M:A.Orphan" });
        dead[0].Tier.ShouldBe(DeadCodeFinder.Tier.High); // private + uncalled
        dead[0].Reason.ShouldBe("uncalled");
    }

    [Fact]
    public void Dead_cluster_callees_are_flagged_as_reached_only_by_dead_code()
    {
        // DeadHead (uncalled) -> DeadTail. Neither is reachable from the root, but DeadTail HAS a caller.
        var methods = new[] { M("M:Root.Run", "Run"), M("M:D.DeadHead", "DeadHead"), M("M:D.DeadTail", "DeadTail") };
        var graph = Graph(methods, new CallEdge("M:D.DeadHead", "M:D.DeadTail", "invocation", "f.cs", 1));
        var meta = new[]
        {
            Meta("M:Root.Run", "Run", "public"),
            Meta("M:D.DeadHead", "DeadHead", "private"),
            Meta("M:D.DeadTail", "DeadTail", "private"),
        };

        var dead = DeadCodeFinder.Find(graph, new[] { "M:Root.Run" }, meta, treatExternallyVisibleAsRoots: false);

        var head = dead.Single(d => d.SymbolId == "M:D.DeadHead");
        var tail = dead.Single(d => d.SymbolId == "M:D.DeadTail");
        head.DirectCallers.ShouldBe(0); // cluster root — the actionable head
        head.Reason.ShouldBe("uncalled");
        tail.DirectCallers.ShouldBe(1);
        tail.Reason.ShouldBe("only reached by dead code");
    }

    [Fact]
    public void Public_method_is_flagged_in_application_mode_but_is_a_root_in_library_mode()
    {
        var methods = new[] { M("M:Api.Exported", "Exported") };
        var graph = Graph(methods);
        var meta = new[] { Meta("M:Api.Exported", "Exported", "public") };

        var asApp = DeadCodeFinder.Find(graph, System.Array.Empty<string>(), meta, treatExternallyVisibleAsRoots: false);
        asApp.Single().Tier.ShouldBe(DeadCodeFinder.Tier.Low); // possible external API -> low confidence

        var asLib = DeadCodeFinder.Find(graph, System.Array.Empty<string>(), meta, treatExternallyVisibleAsRoots: true);
        asLib.ShouldBeEmpty(); // public = root, not flagged
    }

    [Fact]
    public void Internal_uncalled_method_is_medium_confidence()
    {
        var methods = new[] { M("M:A.Helper", "Helper") };
        var graph = Graph(methods);
        var meta = new[] { Meta("M:A.Helper", "Helper", "internal") };

        var dead = DeadCodeFinder.Find(graph, System.Array.Empty<string>(), meta, treatExternallyVisibleAsRoots: false);

        dead.Single().Tier.ShouldBe(DeadCodeFinder.Tier.Medium);
    }

    [Fact]
    public void Off_graph_and_dispatch_members_are_excluded()
    {
        // None reachable from any root, but each must be skipped for a structural reason.
        var methods = new[]
        {
            M("M:T.#ctor", ".ctor"),
            M("M:T.get_X", "get_X"),
            M("M:T.op_Addition", "op_Addition"),
            M("M:T.Contract", "Contract"),
            M("M:T.OnSave", "OnSave"),
            M("M:T.Gen", "Gen"),
        };
        var graph = Graph(methods);
        var meta = new[]
        {
            Meta("M:T.#ctor", ".ctor", "private"),
            Meta("M:T.get_X", "get_X", "private"),
            Meta("M:T.op_Addition", "op_Addition", "public"),
            Meta("M:T.Contract", "Contract", "public abstract"),
            Meta("M:T.OnSave", "OnSave", "private", isOverride: true),
            Meta("M:T.Gen", "Gen", "private", generated: true),
        };

        var dead = DeadCodeFinder.Find(graph, System.Array.Empty<string>(), meta, treatExternallyVisibleAsRoots: false);

        dead.ShouldBeEmpty();
    }

    [Fact]
    public void Override_member_is_flagged_only_when_dispatch_members_are_included()
    {
        var methods = new[] { M("M:T.OnSave", "OnSave") };
        var graph = Graph(methods);
        var meta = new[] { Meta("M:T.OnSave", "OnSave", "private", isOverride: true) };

        DeadCodeFinder.Find(graph, System.Array.Empty<string>(), meta, treatExternallyVisibleAsRoots: false).ShouldBeEmpty();
        DeadCodeFinder
            .Find(graph, System.Array.Empty<string>(), meta, treatExternallyVisibleAsRoots: false, includeDispatchMembers: true)
            .Select(d => d.SymbolId)
            .ShouldBe(new[] { "M:T.OnSave" });
    }
}

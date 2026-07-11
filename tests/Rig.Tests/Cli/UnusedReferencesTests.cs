using Rig.Cli.Services;
using Shouldly;

namespace Rig.Tests.Cli;

// Unit tests for the PURE graph-algebra behind `rig refs --unused` (UnusedReferenceAnalyzer): the
// csproj->assembly attribution (owning-dir + modal) and the declared-vs-observed edge diff. Pure functions
// over hand-built in-memory inputs — NO indexing, NO DB, NO filesystem read (paths are just strings the
// attribution normalises with Path.GetFullPath).
public sealed class UnusedReferencesTests
{
    // Root under the temp dir so every csproj/file path is absolute (Path.GetFullPath is then idempotent)
    // and the test is cross-platform (Path.Combine picks the platform separator).
    private static readonly string Root = Path.Combine(Path.GetTempPath(), $"rig-unused-{Guid.NewGuid():N}");

    [Test]
    public void BuildCsprojToAssembly_picks_the_modal_assembly_per_project()
    {
        var csprojA = Path.Combine(Root, "A", "A.csproj");
        var files = new List<(string, string)>
        {
            (Path.Combine(Root, "A", "Foo.cs"), "AsmA"),
            (Path.Combine(Root, "A", "Baz.cs"), "AsmA"),
            (Path.Combine(Root, "A", "Qux.cs"), "AsmX"), // minority — must NOT win
        };

        var map = UnusedReferenceAnalyzer.BuildCsprojToAssembly(csprojPaths: [csprojA], files: files);

        map[csprojA].ShouldBe("AsmA");
    }

    // A file under a NESTED project's directory must attribute to the nested project (longest owning-dir
    // prefix wins), not the parent whose directory is also a prefix.
    [Test]
    public void BuildCsprojToAssembly_respects_nearest_project_when_projects_nest()
    {
        var csprojA = Path.Combine(Root, "A", "A.csproj");
        var csprojB = Path.Combine(Root, "A", "Sub", "B.csproj");
        var files = new List<(string, string)>
        {
            (Path.Combine(Root, "A", "Foo.cs"), "AsmA"),
            (Path.Combine(Root, "A", "Sub", "Bar.cs"), "AsmB"), // under BOTH A/ and A/Sub/ — B (longer) wins
        };

        var map = UnusedReferenceAnalyzer.BuildCsprojToAssembly(csprojPaths: [csprojA, csprojB], files: files);

        map[csprojA].ShouldBe("AsmA");
        map[csprojB].ShouldBe("AsmB");
    }

    // A csproj with no owned indexed files has NO assembly entry (it was not indexed) and is excluded.
    [Test]
    public void BuildCsprojToAssembly_omits_projects_with_no_owned_indexed_files()
    {
        var csprojA = Path.Combine(Root, "A", "A.csproj");
        var csprojEmpty = Path.Combine(Root, "Empty", "Empty.csproj");
        var files = new List<(string, string)> { (Path.Combine(Root, "A", "Foo.cs"), "AsmA") };

        var map = UnusedReferenceAnalyzer.BuildCsprojToAssembly(csprojPaths: [csprojA, csprojEmpty], files: files);

        map.ContainsKey(csprojA).ShouldBeTrue();
        map.ContainsKey(csprojEmpty).ShouldBeFalse();
    }

    [Test]
    public void FindUnused_returns_a_declared_edge_with_no_usage_and_skips_one_that_is_used()
    {
        var declared = new Dictionary<string, List<string>> { ["a.csproj"] = ["b.csproj", "c.csproj"] };
        var csprojToAsm = new Dictionary<string, string>
        {
            ["a.csproj"] = "A",
            ["b.csproj"] = "B",
            ["c.csproj"] = "C",
        };
        var usage = new HashSet<(string, string)> { ("A", "B") };

        var result = UnusedReferenceAnalyzer.FindUnused(declared, csprojToAsm, usage);

        result.ShouldHaveSingleItem();
        result[0].ShouldBe(("A", "C")); // A->B is used and dropped; A->C is the unused candidate
    }

    // An edge whose endpoint (here the target D) has no known assembly — its csproj was not indexed — cannot
    // be diffed and is EXCLUDED, even though it has no usage edge.
    [Test]
    public void FindUnused_excludes_edges_where_an_endpoint_has_no_assembly()
    {
        var declared = new Dictionary<string, List<string>> { ["a.csproj"] = ["b.csproj", "d.csproj"] };
        var csprojToAsm = new Dictionary<string, string> { ["a.csproj"] = "A", ["b.csproj"] = "B" }; // d.csproj unindexed
        var usage = new HashSet<(string, string)>(); // nothing used

        var result = UnusedReferenceAnalyzer.FindUnused(declared, csprojToAsm, usage);

        result.ShouldHaveSingleItem();
        result[0].ShouldBe(("A", "B")); // A->D dropped (D has no assembly); A->B is the only candidate
    }

    // A declaring project with no known assembly yields no candidates at all.
    [Test]
    public void FindUnused_excludes_edges_whose_declaring_project_has_no_assembly()
    {
        var declared = new Dictionary<string, List<string>> { ["e.csproj"] = ["b.csproj"] };
        var csprojToAsm = new Dictionary<string, string> { ["b.csproj"] = "B" }; // e.csproj unindexed
        var usage = new HashSet<(string, string)>();

        UnusedReferenceAnalyzer.FindUnused(declared, csprojToAsm, usage).ShouldBeEmpty();
    }

    // Two csprojs that resolve to the SAME assembly (an assembly split across projects) form a self-edge,
    // which is not a real reference and must be ignored.
    [Test]
    public void FindUnused_ignores_self_edges_same_assembly()
    {
        var declared = new Dictionary<string, List<string>> { ["a1.csproj"] = ["a2.csproj"] };
        var csprojToAsm = new Dictionary<string, string> { ["a1.csproj"] = "A", ["a2.csproj"] = "A" };
        var usage = new HashSet<(string, string)>();

        UnusedReferenceAnalyzer.FindUnused(declared, csprojToAsm, usage).ShouldBeEmpty();
    }

    // The real-shaped finding: PACS declares references to Common AND ServiceLayer, but only uses Common —
    // ServiceLayer is the exact prunable candidate.
    [Test]
    public void FindUnused_realistic_case_flags_exactly_the_unused_reference()
    {
        var declared = new Dictionary<string, List<string>> { ["pacs.csproj"] = ["common.csproj", "service.csproj"] };
        var csprojToAsm = new Dictionary<string, string>
        {
            ["pacs.csproj"] = "MedDBase.PACS",
            ["common.csproj"] = "MedDBase.Common",
            ["service.csproj"] = "MedDBase.ServiceLayer",
        };
        var usage = new HashSet<(string, string)> { ("MedDBase.PACS", "MedDBase.Common") };

        var result = UnusedReferenceAnalyzer.FindUnused(declared, csprojToAsm, usage);

        result.ShouldHaveSingleItem();
        result[0].DeclaringAsm.ShouldBe("MedDBase.PACS");
        result[0].UnusedAsm.ShouldBe("MedDBase.ServiceLayer");
    }
}

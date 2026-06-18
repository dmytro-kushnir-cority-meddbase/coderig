using Rig.Cli.Commands;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Cli;

// The symbol-granular blast-radius gate behind `rig impact` (ImpactCommand.SelectChangedMethods +
// ParseUnifiedDiff): a changed file is narrowed to the methods whose source extent [Line, EndLine] overlaps
// a changed line range, but ONLY when that can be proved — otherwise the whole file is taken. These tests
// pin both the parser and every fallback (no ranges / unknown span / out-of-method edit).
public sealed class ImpactBlastRadiusTests
{
    private const string File = "C:/repo/A.cs"; // already Norm-shaped (forward slashes), so it joins as-is

    private static DeadCodeFinder.MethodMeta Method(string id, int line) =>
        new(SymbolId: id, Name: id, Modifiers: "public", FilePath: File, Line: line, IsOverride: false, IsGenerated: false);

    // --- ParseUnifiedDiff -----------------------------------------------------------------------------

    [Test]
    public void ParseUnifiedDiff_reads_new_side_ranges_and_skips_non_cs()
    {
        var diff =
            "diff --git a/A.cs b/A.cs\n"
            + "--- a/A.cs\n"
            + "+++ b/A.cs\n"
            + "@@ -10,2 +10,3 @@\n" // modify: new-side [10,12]
            + "@@ -40 +41 @@\n" // single line (no len): new-side [41,41]
            + "diff --git a/note.txt b/note.txt\n"
            + "--- a/note.txt\n"
            + "+++ b/note.txt\n"
            + "@@ -1 +1 @@\n"; // non-.cs => ignored

        var ranges = ImpactCommand.ParseUnifiedDiff(diff);

        ranges.ContainsKey("note.txt").ShouldBeFalse();
        ranges["A.cs"].ShouldBe(new (int, int)[] { (10, 12), (41, 41) });
    }

    [Test]
    public void ParseUnifiedDiff_models_a_pure_deletion_as_the_seam()
    {
        // `+20,0` (no new lines) => deletion in the seam between new lines 20 and 21.
        var ranges = ImpactCommand.ParseUnifiedDiff("+++ b/A.cs\n@@ -20,3 +20,0 @@\n");

        ranges["A.cs"].ShouldBe(new (int, int)[] { (20, 21) });
    }

    [Test]
    public void ParseUnifiedDiff_ignores_a_wholesale_deletion()
    {
        // new side is /dev/null => nothing in the new tree to attribute.
        var ranges = ImpactCommand.ParseUnifiedDiff("+++ /dev/null\n@@ -1,5 +0,0 @@\n");

        ranges.ShouldBeEmpty();
    }

    // --- SelectChangedMethods -------------------------------------------------------------------------

    private static readonly IReadOnlySet<string> ChangedFile = new HashSet<string> { File };

    [Test]
    public void No_committed_ranges_falls_back_to_file_granular()
    {
        var methods = new[] { Method("M:A.One", 1), Method("M:A.Two", 20) };
        var endLines = new Dictionary<string, int> { ["M:A.One"] = 10, ["M:A.Two"] = 30 };

        var set = ImpactCommand.SelectChangedMethods(
            methods,
            endLines,
            ChangedFile,
            new Dictionary<string, IReadOnlyList<(int, int)>>() // file changed but no trusted ranges (e.g. dirty)
        );

        set.Methods.Select(m => m.SymbolId).ShouldBe(new[] { "M:A.One", "M:A.Two" }, ignoreOrder: true);
        set.PreciseFileCount.ShouldBe(0);
        set.FileGranularFileCount.ShouldBe(1);
    }

    [Test]
    public void A_range_inside_one_method_selects_only_that_method()
    {
        var methods = new[] { Method("M:A.One", 1), Method("M:A.Two", 20) };
        var endLines = new Dictionary<string, int> { ["M:A.One"] = 10, ["M:A.Two"] = 30 };
        var ranges = new Dictionary<string, IReadOnlyList<(int, int)>> { [File] = new[] { (22, 23) } };

        var set = ImpactCommand.SelectChangedMethods(methods, endLines, ChangedFile, ranges);

        set.Methods.Select(m => m.SymbolId).ShouldBe(new[] { "M:A.Two" });
        set.PreciseFileCount.ShouldBe(1);
        set.FileGranularFileCount.ShouldBe(0);
    }

    [Test]
    public void An_edit_outside_every_method_takes_the_whole_file()
    {
        // Line 15 sits between One [1,10] and Two [20,30] — a field/using/attribute change → whole file.
        var methods = new[] { Method("M:A.One", 1), Method("M:A.Two", 20) };
        var endLines = new Dictionary<string, int> { ["M:A.One"] = 10, ["M:A.Two"] = 30 };
        var ranges = new Dictionary<string, IReadOnlyList<(int, int)>> { [File] = new[] { (15, 15) } };

        var set = ImpactCommand.SelectChangedMethods(methods, endLines, ChangedFile, ranges);

        set.Methods.Select(m => m.SymbolId).ShouldBe(new[] { "M:A.One", "M:A.Two" }, ignoreOrder: true);
        set.FileGranularFileCount.ShouldBe(1);
    }

    [Test]
    public void A_missing_end_line_forces_file_granular()
    {
        // One has no EndLine (a pre-EndLine store, or a symbol we couldn't span) → can't prove precision.
        var methods = new[] { Method("M:A.One", 1), Method("M:A.Two", 20) };
        var endLines = new Dictionary<string, int> { ["M:A.Two"] = 30 };
        var ranges = new Dictionary<string, IReadOnlyList<(int, int)>> { [File] = new[] { (22, 23) } };

        var set = ImpactCommand.SelectChangedMethods(methods, endLines, ChangedFile, ranges);

        set.Methods.Select(m => m.SymbolId).ShouldBe(new[] { "M:A.One", "M:A.Two" }, ignoreOrder: true);
        set.FileGranularFileCount.ShouldBe(1);
    }
}

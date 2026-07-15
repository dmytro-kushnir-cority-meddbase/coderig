using Rig.Analysis.Inventory;
using Shouldly;

namespace Rig.Tests.Analysis;

// projects.exclude used to be applied only AFTER the excluded projects' design-time builds were paid
// for, and silently — a production project in the exclude list (MedDBase: `dfs`) simply vanished
// between "Assembling workspace from N" and "Loaded M C# project(s)", so the store looked complete
// while whole assemblies were untraversable. The fix filters before the builds and names what it
// drops; FormatProjectList renders that one progress line, capped so a big test-glob exclusion
// doesn't flood the log.
public sealed class ExcludedProjectReportingTests
{
    [Test]
    public void Short_list_shows_every_name()
    {
        SolutionSourceLoader.FormatProjectList(["dfs", "MMS.Net"]).ShouldBe("dfs, MMS.Net");
    }

    [Test]
    public void List_at_the_cap_shows_every_name_without_a_more_suffix()
    {
        var names = Enumerable.Range(1, 10).Select(i => $"P{i}").ToArray();

        var line = SolutionSourceLoader.FormatProjectList(names);

        line.ShouldBe("P1, P2, P3, P4, P5, P6, P7, P8, P9, P10");
    }

    [Test]
    public void Long_list_caps_at_ten_names_and_counts_the_rest()
    {
        var names = Enumerable.Range(1, 14).Select(i => $"P{i}").ToArray();

        var line = SolutionSourceLoader.FormatProjectList(names);

        line.ShouldStartWith("P1, P2");
        line.ShouldContain("P10");
        line.ShouldNotContain("P11");
        line.ShouldEndWith("… (+4 more)");
    }
}

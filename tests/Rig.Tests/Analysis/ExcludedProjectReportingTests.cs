using Rig.Analysis.Inventory;
using Shouldly;

namespace Rig.Tests.Analysis;

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

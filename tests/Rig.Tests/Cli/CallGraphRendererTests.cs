using Rig.Analysis;
using Rig.Cli.Rendering;
using Shouldly;

namespace Rig.Tests.Cli;

public sealed class CallGraphRendererTests
{
    [Fact]
    public void Focused_mode_keeps_effect_paths_and_hides_boundaries()
    {
        var output = Render(fullMode: false, summaryMode: false);

        output.ShouldContain("Callgraph: [3] minapi GET /teams (focused)");
        output.ShouldContain("Nodes: 2 / 3 on effect paths");
        output.ShouldContain("Controller.Handle");
        output.ShouldContain("Repository.Load");
        output.ShouldContain("EFFECT efcore read  ToListAsync  AppDbContext.Teams");
        output.ShouldNotContain("BOUNDARY external HttpClient.GetStringAsync");
        output.ShouldNotContain("Telemetry.Track");
    }

    [Fact]
    public void Full_mode_keeps_boundaries_and_non_effect_branches()
    {
        var output = Render(fullMode: true, summaryMode: false);

        output.ShouldContain("Callgraph: [3] minapi GET /teams (full)");
        output.ShouldContain("Nodes: 3");
        output.ShouldContain("BOUNDARY external HttpClient.GetStringAsync");
        output.ShouldContain("Telemetry.Track");
    }

    [Fact]
    public void Summary_mode_deduplicates_effects_and_preserves_counts()
    {
        var output = Render(fullMode: false, summaryMode: true);

        output.ShouldContain("Summary: [3] minapi GET /teams");
        output.ShouldContain("Nodes: 3  Effects: 2");
        output.ShouldContain("efcore");
        output.ShouldContain("[x2]");
    }

    private static string Render(bool fullMode, bool summaryMode)
    {
        var writer = new StringWriter();
        CallGraphRenderer.Render(CreateGraph(), entryPointIndex: 3, fullMode, summaryMode, writer);
        return writer.ToString();
    }

    private static CallGraphInfo CreateGraph()
    {
        var effect = new EffectInfo(
            "efcore",
            "read",
            "AppDbContext.Teams",
            "ToListAsync",
            @"C:\repo\Repository.cs",
            20,
            "high",
            "compilation+profile",
            "ef_read",
            []);

        return new CallGraphInfo(
            "minapi GET /teams",
            [
                new CallGraphNodeInfo(
                    "Controller.Handle",
                    @"C:\repo\Controller.cs",
                    10,
                    "high",
                    "compilation",
                    "entry",
                    ["Repository.Load", "Telemetry.Track"],
                    [new BoundaryCallInfo("external", "System.Net.Http.HttpClient.GetStringAsync", "HttpClient.GetStringAsync", @"C:\repo\Controller.cs", 11, "high", "compilation", "external_symbol")],
                    []),
                new CallGraphNodeInfo(
                    "Repository.Load",
                    @"C:\repo\Repository.cs",
                    20,
                    "high",
                    "compilation",
                    "direct_symbol_match",
                    [],
                    [],
                    [effect, effect]),
                new CallGraphNodeInfo(
                    "Telemetry.Track",
                    @"C:\repo\Telemetry.cs",
                    30,
                    "high",
                    "compilation",
                    "direct_symbol_match",
                    [],
                    [],
                    [])
            ]);
    }
}

using FluentAssertions;
using Rig.Analysis;

namespace Rig.Tests.Analysis;

public sealed class PlaygroundAnalysisTests
{
    [Fact]
    public async Task Entry_point_effects_playground_tracks_entrypoints_and_effects()
    {
        var solutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "playgrounds",
            "EntryPointEffects",
            "EntryPointEffects.slnx"));

        var result = await SolutionAnalyzer.AnalyzeAsync(solutionPath);

        result.EntryPoints.Select(entryPoint => entryPoint.DisplayName).Should().BeEquivalentTo(
            "minapi GET /minapi/teams/{id}",
            "minapi POST /minapi/teams",
            "mvc GET api/teams/{id}",
            "mvc POST api/teams");

        result.Effects.Should().Contain(effect =>
            effect.Provider == "http" &&
            effect.Operation == "GET" &&
            effect.Resource == "billing.example/invoices/{teamId}");

        result.Effects.Should().Contain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "read" &&
            effect.Resource == "AppDbContext.Teams");

        result.Effects.Should().Contain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "commit" &&
            effect.Resource == "AppDbContext");

        result.Effects.Should().Contain(effect =>
            effect.Provider == "redis" &&
            effect.Operation == "read" &&
            effect.Resource == "team:{teamId}");

        result.Effects.Should().Contain(effect =>
            effect.Provider == "redis" &&
            effect.Operation == "write" &&
            effect.Resource == "team:{name}");

        result.Effects.Should().Contain(effect =>
            effect.Provider == "redis" &&
            effect.Resource == "team:{relatedTeamId}" &&
            effect.Observations.Any(observation =>
                observation.Type == "looped_effect" &&
                observation.Context == "foreach"));

        result.Effects.Should().Contain(effect =>
            effect.Provider == "http" &&
            effect.Resource == "billing.example/invoices/{teamId}" &&
            effect.Observations.Any(observation =>
                observation.Type == "parallel_fanout" &&
                observation.Context == "Task.WhenAll"));

        var minApiGetGraph = result.CallGraphs.Should().ContainSingle(graph =>
            graph.EntryPoint == "minapi GET /minapi/teams/{id}").Subject;

        minApiGetGraph.Nodes.Select(node => node.Symbol).Should().Contain(
            "minapi GET /minapi/teams/{id}",
            "TeamWorkflow.LoadTeamSummaryAsync",
            "BillingClient.LoadInvoiceAsync",
            "BillingClient.LoadInvoicesAsync");

        minApiGetGraph.Nodes.Should().Contain(node =>
            node.Symbol == "TeamWorkflow.LoadTeamSummaryAsync" &&
            node.Effects.Any(effect => effect.Provider == "efcore" && effect.Operation == "read") &&
            node.Effects.Any(effect => effect.Provider == "redis" && effect.Resource == "team:{relatedTeamId}"));

        minApiGetGraph.Nodes.Should().Contain(node =>
            node.Symbol == "BillingClient.LoadInvoicesAsync" &&
            node.Effects.Any(effect => effect.Observations.Any(observation =>
                observation.Type == "parallel_fanout")));
    }
}

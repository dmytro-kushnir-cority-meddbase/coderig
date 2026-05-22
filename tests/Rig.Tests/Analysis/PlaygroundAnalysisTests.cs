using Shouldly;
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

        result.SourceFiles.ShouldContain(sourceFile =>
            sourceFile.Status == "skipped" &&
            sourceFile.Basis == "profile" &&
            sourceFile.Reason == "generated_fixture" &&
            sourceFile.FilePath.EndsWith("GeneratedEndpoint.g.cs", StringComparison.OrdinalIgnoreCase));

        result.EntryPoints.Select(entryPoint => entryPoint.DisplayName).ShouldBe(
            new[]
            {
                "minapi GET /minapi/teams/{id}",
                "minapi POST /minapi/teams",
                "mvc GET api/teams/{id}",
                "mvc POST api/teams",
                "fastendpoint POST /fastendpoints/teams"
            },
            ignoreOrder: true);

        result.Effects.ShouldContain(effect =>
            effect.Provider == "http" &&
            effect.Operation == "GET" &&
            effect.Resource == "billing.example/invoices/{teamId}");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "read" &&
            effect.Resource == "AppDbContext.Teams");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "schema" &&
            effect.Resource == "AppDbContext.Database");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "raw_sql" &&
            effect.Resource == "AppDbContext.Database");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "smtp" &&
            effect.Operation == "send" &&
            effect.Resource == "MailKit.Net.Smtp.SmtpClient");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "repository" &&
            effect.Operation == "write" &&
            effect.Resource == "Ardalis.SharedKernel.IRepository<T>");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "commit" &&
            effect.Resource == "AppDbContext");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "redis" &&
            effect.Operation == "read" &&
            effect.Resource == "team:{teamId}");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "redis" &&
            effect.Operation == "write" &&
            effect.Resource == "team:{name}");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "redis" &&
            effect.Resource == "team:{relatedTeamId}" &&
            effect.Observations.Any(observation =>
                observation.Type == "looped_effect" &&
                observation.Context == "foreach"));

        result.Effects.ShouldContain(effect =>
            effect.Provider == "http" &&
            effect.Resource == "billing.example/invoices/{teamId}" &&
            effect.Observations.Any(observation =>
                observation.Type == "parallel_fanout" &&
                observation.Context == "Task.WhenAll"));

        result.MethodObservations.ShouldContain(observation =>
            observation.DisplayName == "TeamWorkflow.LoadTeamSummaryAsync" &&
            observation.Symbol.Contains("EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync", StringComparison.Ordinal));

        result.InvocationObservations.ShouldContain(observation =>
            observation.ContainingMethodSymbol.Contains("EntryPointEffects.Api.Controllers.TeamsController.Get", StringComparison.Ordinal) &&
            observation.TargetSymbol.Contains("EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync", StringComparison.Ordinal) &&
            observation.Basis == "compilation");

        result.DiRegistrations.ShouldContain(registration =>
            registration.ServiceType.Contains("EntryPointEffects.Api.Services.TeamWorkflow", StringComparison.Ordinal) &&
            registration.Lifetime == "scoped" &&
            registration.Reason == "msdi_addscoped");

        result.DiRegistrations.ShouldContain(registration =>
            registration.RegistrationKind == "http_client" &&
            registration.ImplementationType != null &&
            registration.ImplementationType.Contains("EntryPointEffects.Api.Services.BillingClient", StringComparison.Ordinal));

        var minApiGetGraph = result.CallGraphs
            .Where(graph => graph.EntryPoint == "minapi GET /minapi/teams/{id}")
            .ShouldHaveSingleItem();

        var symbols = minApiGetGraph.Nodes.Select(node => node.Symbol);
        symbols.ShouldContain("minapi GET /minapi/teams/{id}");
        symbols.ShouldContain("TeamWorkflow.LoadTeamSummaryAsync");
        symbols.ShouldContain("TeamWorkflow.ObserveDynamicBoundary");
        symbols.ShouldContain("BillingClient.LoadInvoiceAsync");
        symbols.ShouldContain("BillingClient.LoadInvoicesAsync");

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol == "TeamWorkflow.LoadTeamSummaryAsync" &&
            node.Effects.Any(effect => effect.Provider == "efcore" && effect.Operation == "read") &&
            node.Effects.Any(effect => effect.Provider == "redis" && effect.Resource == "team:{relatedTeamId}"));

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol == "BillingClient.LoadInvoicesAsync" &&
            node.Effects.Any(effect => effect.Observations.Any(observation =>
                observation.Type == "parallel_fanout")));

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol == "BillingClient.LoadInvoiceAsync" &&
            node.BoundaryCalls.Any(call =>
                call.Kind == "external" &&
                call.Method == "HttpClient.GetStringAsync"));

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol == "TeamWorkflow.ObserveDynamicBoundary" &&
            node.BoundaryCalls.Any(call =>
                call.Kind == "unresolved" &&
                call.Reason == "unresolved_call_target"));
    }
}

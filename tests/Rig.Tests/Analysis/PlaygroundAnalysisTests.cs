using Shouldly;
using Rig.Analysis;
using Rig.Tests.Fixtures;

namespace Rig.Tests.Analysis;

[Collection(RoslynIntegrationCollection.Name)]
public sealed class PlaygroundAnalysisTests
{
    [Fact]
    public async Task Entry_point_effects_playground_tracks_entrypoints_and_effects()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();

        var result = await SolutionAnalyzer.AnalyzeAsync(playground.SolutionPath);

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
                "mvc GET api/teams/via-interface",
                "mvc POST api/teams/via-interface",
                "mvc GET api/teams/via-method-group",
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

        result.Effects.ShouldContain(effect =>
            effect.Provider == "redis" &&
            effect.Operation == "read" &&
            effect.Observations.Any(observation =>
                observation.Type == "parallel_fanout" &&
                observation.Context == "Parallel.ForEach"));

        result.Effects.ShouldContain(effect =>
            effect.Provider == "redis" &&
            effect.Operation == "read" &&
            effect.Observations.Any(observation =>
                observation.Type == "parallel_fanout" &&
                observation.Context == "Parallel.ForEachAsync"));

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
            registration.Reason == "msdi_addscoped" &&
            registration.Evidence.Contains("project=EntryPointEffects.Api", StringComparison.Ordinal));

        result.DiRegistrations.ShouldContain(registration =>
            registration.ServiceType.Contains("EntryPointEffects.Api.Services.ITeamRepository", StringComparison.Ordinal) &&
            registration.ImplementationType != null &&
            registration.ImplementationType.Contains("EntryPointEffects.Api.Services.TeamRepository", StringComparison.Ordinal));

        result.DiRegistrations.ShouldContain(registration =>
            registration.RegistrationKind == "http_client" &&
            registration.ImplementationType != null &&
            registration.ImplementationType.Contains("EntryPointEffects.Api.Services.BillingClient", StringComparison.Ordinal));

        var minApiGetGraph = result.CallGraphs
            .Where(graph => graph.EntryPoint == "minapi GET /minapi/teams/{id}")
            .ShouldHaveSingleItem();

        var symbols = minApiGetGraph.Nodes.Select(node => node.Symbol);
        symbols.ShouldContain("minapi GET /minapi/teams/{id}");
        symbols.ShouldContain(symbol => symbol.Contains("global::EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync", StringComparison.Ordinal));
        symbols.ShouldContain(symbol => symbol.Contains("global::EntryPointEffects.Api.Services.TeamWorkflow.ObserveDynamicBoundary", StringComparison.Ordinal));
        symbols.ShouldContain(symbol => symbol.Contains("global::EntryPointEffects.Api.Services.BillingClient.LoadInvoiceAsync", StringComparison.Ordinal));
        symbols.ShouldContain(symbol => symbol.Contains("global::EntryPointEffects.Api.Services.BillingClient.LoadInvoicesAsync", StringComparison.Ordinal));

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol.Contains("global::EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync", StringComparison.Ordinal) &&
            node.Effects.Any(effect => effect.Provider == "efcore" && effect.Operation == "read") &&
            node.Effects.Any(effect => effect.Provider == "redis" && effect.Resource == "team:{relatedTeamId}"));

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol.Contains("global::EntryPointEffects.Api.Services.BillingClient.LoadInvoicesAsync", StringComparison.Ordinal) &&
            node.Effects.Any(effect => effect.Observations.Any(observation =>
                observation.Type == "parallel_fanout")));

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol.Contains("global::EntryPointEffects.Api.Services.BillingClient.LoadInvoiceAsync", StringComparison.Ordinal) &&
            node.BoundaryCalls.Any(call =>
                call.Kind == "external" &&
                call.Method == "HttpClient.GetStringAsync"));

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol.Contains("global::EntryPointEffects.Api.Services.TeamWorkflow.ObserveDynamicBoundary", StringComparison.Ordinal) &&
            node.BoundaryCalls.Any(call =>
                call.Kind == "unresolved" &&
                call.Reason == "unresolved_call_target"));

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol == "minapi GET /minapi/teams/{id}" &&
            node.Calls.Any(call => call.Contains("global::EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Eshop_grpc_services_are_entrypoints_and_reach_redis_effects()
    {
        var solutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "playgrounds", "eShop", "eShop.slnx"));

        var result = await SolutionAnalyzer.AnalyzeAsync(solutionPath);

        result.EntryPoints.ShouldContain(entryPoint =>
            entryPoint.Kind == "grpc" &&
            entryPoint.DisplayName.Contains("BasketService.GetBasket", StringComparison.Ordinal));
        result.EntryPoints.ShouldContain(entryPoint =>
            entryPoint.Kind == "grpc" &&
            entryPoint.DisplayName.Contains("BasketService.UpdateBasket", StringComparison.Ordinal));
        result.EntryPoints.ShouldContain(entryPoint =>
            entryPoint.Kind == "grpc" &&
            entryPoint.DisplayName.Contains("BasketService.DeleteBasket", StringComparison.Ordinal));

        var getBasketGraph = result.CallGraphs
            .Single(graph => graph.EntryPoint.Contains("BasketService.GetBasket", StringComparison.Ordinal));
        getBasketGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "redis" && effect.Operation == "read"));

        var updateBasketGraph = result.CallGraphs
            .Single(graph => graph.EntryPoint.Contains("BasketService.UpdateBasket", StringComparison.Ordinal));
        updateBasketGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "redis" && effect.Operation == "write"));

        var deleteBasketGraph = result.CallGraphs
            .Single(graph => graph.EntryPoint.Contains("BasketService.DeleteBasket", StringComparison.Ordinal));
        deleteBasketGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "redis" && effect.Operation == "write"));
    }

    [Fact]
    public async Task Eshop_background_workers_and_event_handlers_are_entrypoints()
    {
        var solutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "playgrounds", "eShop", "eShop.slnx"));

        var result = await SolutionAnalyzer.AnalyzeAsync(solutionPath);

        result.EntryPoints.ShouldContain(entryPoint =>
            entryPoint.Kind == "background" &&
            entryPoint.DisplayName.Contains("GracePeriodManagerService.ExecuteAsync", StringComparison.Ordinal));
        result.EntryPoints.ShouldContain(entryPoint =>
            entryPoint.Kind == "eventhandler" &&
            entryPoint.DisplayName.Contains("OrderStatusChangedToStockConfirmedIntegrationEventHandler.Handle", StringComparison.Ordinal));

        var gracePeriodGraph = result.CallGraphs
            .Single(graph => graph.EntryPoint.Contains("GracePeriodManagerService.ExecuteAsync", StringComparison.Ordinal));
        gracePeriodGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "eventbus" && effect.Operation == "publish"));

        var paymentHandlerGraphs = result.CallGraphs
            .Where(graph => graph.EntryPoint.Contains("OrderStatusChangedToStockConfirmedIntegrationEventHandler.Handle", StringComparison.Ordinal))
            .ToArray();
        paymentHandlerGraphs.ShouldNotBeEmpty();
        paymentHandlerGraphs.ShouldContain(graph =>
            graph.Nodes.Any(node => node.Effects.Any(effect => effect.Provider == "eventbus" && effect.Operation == "publish")));
        paymentHandlerGraphs.ShouldContain(graph =>
            graph.Nodes.Any(node => node.Effects.Any(effect =>
                effect.Provider == "rabbitmq" &&
                effect.Operation == "publish" &&
                effect.Method == "BasicPublishAsync")));
    }
}

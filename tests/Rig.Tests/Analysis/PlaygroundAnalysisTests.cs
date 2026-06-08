using Rig.Analysis;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

[Collection(RoslynIntegrationCollection.Name)]
public sealed class PlaygroundAnalysisTests(AnalyzedPlaygrounds playgrounds)
{
    private readonly AnalyzedPlaygrounds _playgrounds = playgrounds;

    [Fact]
    public async Task Llblgen_entity_constructor_fetch_is_extracted_at_index_time()
    {
        var playground = await _playgrounds.LegacyNet48Async();

        var result = playground.Result;

        // G5: `new InvoiceEntity(pk)` and `new InvoiceEntity(pk, txn)` in EntityFetcher.Load are
        // llblgen constructor fetches; the empty `new InvoiceEntity { InvoiceId = ... }` initializer
        // is NOT. This guards the INDEX path (EffectExtractor -> result.Effects). The fact-layer
        // FactDerivationTests assert the deriver directly and never exercised this path, which is
        // why constructor fetches were silently absent from `rig effects`.
        var fetches = result
            .Effects.Where(effect =>
                effect.Provider == "llblgen"
                && effect.Operation == "fetch"
                && effect.Resource.Contains("InvoiceEntity", StringComparison.Ordinal)
                && effect.FilePath.Contains("EntityFetcher", StringComparison.Ordinal)
            )
            .ToArray();

        fetches.Length.ShouldBe(2);
    }

    [Fact]
    public async Task Clientpage_proxy_effects_are_base_type_gated_at_index_time()
    {
        var playground = await _playgrounds.LegacyNet48Async();

        var result = playground.Result;

        var proxyEffects = result.Effects.Where(effect => effect.Provider == "clientpage_proxy").ToArray();

        // InvoiceEditProxy derives ProxyBase, so `proxy.ShowDialog(...)` in InvoiceMain.SubmitInvoice
        // IS a navigation effect.
        proxyEffects.ShouldContain(effect =>
            effect.Operation == "show" && effect.Resource.Contains("InvoiceEditProxy", StringComparison.Ordinal)
        );

        // InvoiceServiceProxy's name ends in "Proxy" but it does NOT derive ProxyBase. The rule's
        // declaringTypeBaseTypes gate must exclude `notAProxy.ShowDialog(...)`. EffectExtractor
        // previously ignored declaringTypeBaseTypes on invocations, so this false positive leaked
        // into `rig effects` (while the fact path correctly excluded it).
        proxyEffects.ShouldNotContain(effect =>
            effect.Resource.Contains("InvoiceServiceProxy", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Entry_point_effects_playground_tracks_entrypoints_and_effects()
    {
        var playground = await _playgrounds.EntryPointEffectsAsync();

        var result = playground.Result;

        result.SourceFiles.ShouldContain(sourceFile =>
            sourceFile.Status == "skipped"
            && sourceFile.Basis == "profile"
            && sourceFile.Reason == "generated_fixture"
            && sourceFile.FilePath.EndsWith("GeneratedEndpoint.g.cs", StringComparison.OrdinalIgnoreCase)
        );

        result
            .EntryPoints.Select(entryPoint => entryPoint.DisplayName)
            .ShouldBe(
                new[]
                {
                    "minapi GET /minapi/teams/{id}",
                    "minapi POST /minapi/teams",
                    "minapi GET /minapi/cycles/self",
                    "minapi GET /minapi/cycles/mutual",
                    "minapi GET /minapi/cycles/three-step",
                    "mvc GET api/teams/{id}",
                    "mvc POST api/teams",
                    "mvc GET api/teams/via-interface",
                    "mvc POST api/teams/via-interface",
                    "mvc GET api/teams/via-method-group",
                    "fastendpoint POST /fastendpoints/teams",
                },
                ignoreOrder: true
            );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "http" && effect.Operation == "GET" && effect.Resource == "billing.example/invoices/{teamId}"
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" && effect.Operation == "read" && effect.Resource == "AppDbContext.Teams"
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" && effect.Operation == "schema" && effect.Resource == "AppDbContext.Database"
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" && effect.Operation == "raw_sql" && effect.Resource == "AppDbContext.Database"
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "smtp" && effect.Operation == "send" && effect.Resource == "MailKit.Net.Smtp.SmtpClient"
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "repository" && effect.Operation == "write" && effect.Resource == "Ardalis.SharedKernel.IRepository<T>"
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" && effect.Operation == "commit" && effect.Resource == "AppDbContext"
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "redis" && effect.Operation == "read" && effect.Resource == "team:{teamId}"
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "redis" && effect.Operation == "write" && effect.Resource == "team:{name}"
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "redis"
            && effect.Resource == "team:{relatedTeamId}"
            && effect.Observations.Any(observation => observation.Type == "looped_effect" && observation.Context == "foreach")
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "http"
            && effect.Resource == "billing.example/invoices/{teamId}"
            && effect.Observations.Any(observation => observation.Type == "parallel_fanout" && observation.Context == "Task.WhenAll")
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "redis"
            && effect.Operation == "read"
            && effect.Observations.Any(observation => observation.Type == "parallel_fanout" && observation.Context == "Parallel.ForEach")
        );

        result.Effects.ShouldContain(effect =>
            effect.Provider == "redis"
            && effect.Operation == "read"
            && effect.Observations.Any(observation =>
                observation.Type == "parallel_fanout" && observation.Context == "Parallel.ForEachAsync"
            )
        );

        result.MethodObservations.ShouldContain(observation =>
            observation.DisplayName == "TeamWorkflow.LoadTeamSummaryAsync"
            && observation.Symbol.Contains("EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync", StringComparison.Ordinal)
        );

        result.InvocationObservations.ShouldContain(observation =>
            observation.ContainingMethodSymbol.Contains("EntryPointEffects.Api.Controllers.TeamsController.Get", StringComparison.Ordinal)
            && observation.TargetSymbol.Contains(
                "EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync",
                StringComparison.Ordinal
            )
            && observation.Basis == "compilation"
        );

        result.DiRegistrations.ShouldContain(registration =>
            registration.ServiceType.Contains("EntryPointEffects.Api.Services.TeamWorkflow", StringComparison.Ordinal)
            && registration.Lifetime == "scoped"
            && registration.Reason == "msdi_addscoped"
            && registration.Evidence.Contains("project=EntryPointEffects.Api", StringComparison.Ordinal)
        );

        result.DiRegistrations.ShouldContain(registration =>
            registration.ServiceType.Contains("EntryPointEffects.Api.Services.ITeamRepository", StringComparison.Ordinal)
            && registration.ImplementationType != null
            && registration.ImplementationType.Contains("EntryPointEffects.Api.Services.TeamRepository", StringComparison.Ordinal)
        );

        result.DiRegistrations.ShouldContain(registration =>
            registration.RegistrationKind == "http_client"
            && registration.ImplementationType != null
            && registration.ImplementationType.Contains("EntryPointEffects.Api.Services.BillingClient", StringComparison.Ordinal)
        );

        var minApiGetGraph = result.CallGraphs.Where(graph => graph.EntryPoint == "minapi GET /minapi/teams/{id}").ShouldHaveSingleItem();

        var symbols = minApiGetGraph.Nodes.Select(node => node.Symbol);
        symbols.ShouldContain("minapi GET /minapi/teams/{id}");
        symbols.ShouldContain(symbol =>
            symbol.Contains("global::EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync", StringComparison.Ordinal)
        );
        symbols.ShouldContain(symbol =>
            symbol.Contains("global::EntryPointEffects.Api.Services.TeamWorkflow.ObserveDynamicBoundary", StringComparison.Ordinal)
        );
        symbols.ShouldContain(symbol =>
            symbol.Contains("global::EntryPointEffects.Api.Services.BillingClient.LoadInvoiceAsync", StringComparison.Ordinal)
        );
        symbols.ShouldContain(symbol =>
            symbol.Contains("global::EntryPointEffects.Api.Services.BillingClient.LoadInvoicesAsync", StringComparison.Ordinal)
        );

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol.Contains("global::EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync", StringComparison.Ordinal)
            && node.Effects.Any(effect => effect.Provider == "efcore" && effect.Operation == "read")
            && node.Effects.Any(effect => effect.Provider == "redis" && effect.Resource == "team:{relatedTeamId}")
        );

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol.Contains("global::EntryPointEffects.Api.Services.BillingClient.LoadInvoicesAsync", StringComparison.Ordinal)
            && node.Effects.Any(effect => effect.Observations.Any(observation => observation.Type == "parallel_fanout"))
        );

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol.Contains("global::EntryPointEffects.Api.Services.BillingClient.LoadInvoiceAsync", StringComparison.Ordinal)
            && node.BoundaryCalls.Any(call => call.Kind == "external" && call.Method == "HttpClient.GetStringAsync")
        );

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol.Contains("global::EntryPointEffects.Api.Services.TeamWorkflow.ObserveDynamicBoundary", StringComparison.Ordinal)
            && node.BoundaryCalls.Any(call => call.Kind == "unresolved" && call.Reason == "unresolved_call_target")
        );

        minApiGetGraph.Nodes.ShouldContain(node =>
            node.Symbol == "minapi GET /minapi/teams/{id}"
            && node.Calls.Any(call =>
                call.Contains("global::EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync", StringComparison.Ordinal)
            )
        );

        var selfCycleGraph = result.CallGraphs.Single(graph => graph.EntryPoint == "minapi GET /minapi/cycles/self");
        selfCycleGraph
            .Cycles.ShouldHaveSingleItem()
            .Path.ShouldSatisfyAllConditions(
                path => path.Count.ShouldBe(2),
                path => path[0].ShouldContain("CycleFixture.SelfRecursive"),
                path => path[1].ShouldContain("CycleFixture.SelfRecursive")
            );

        var mutualCycleGraph = result.CallGraphs.Single(graph => graph.EntryPoint == "minapi GET /minapi/cycles/mutual");
        var mutualCycle = mutualCycleGraph.Cycles.ShouldHaveSingleItem().Path;
        mutualCycle.Count.ShouldBe(3);
        mutualCycle.ShouldContain(symbol => symbol.Contains("CycleFixture.MutualA", StringComparison.Ordinal));
        mutualCycle.ShouldContain(symbol => symbol.Contains("CycleFixture.MutualB", StringComparison.Ordinal));
        mutualCycle.First().ShouldBe(mutualCycle.Last());

        var threeStepCycleGraph = result.CallGraphs.Single(graph => graph.EntryPoint == "minapi GET /minapi/cycles/three-step");
        var threeStepCycle = threeStepCycleGraph.Cycles.ShouldHaveSingleItem().Path;
        threeStepCycle.Count.ShouldBe(4);
        threeStepCycle.ShouldContain(symbol => symbol.Contains("CycleFixture.ThreeStepA", StringComparison.Ordinal));
        threeStepCycle.ShouldContain(symbol => symbol.Contains("CycleFixture.ThreeStepB", StringComparison.Ordinal));
        threeStepCycle.ShouldContain(symbol => symbol.Contains("CycleFixture.ThreeStepC", StringComparison.Ordinal));
        threeStepCycle.First().ShouldBe(threeStepCycle.Last());
    }

    [Fact(Skip = "requires eShop playground to be cloned")]
    public async Task Eshop_grpc_services_are_entrypoints_and_reach_redis_effects()
    {
        var solutionPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "playgrounds", "eShop", "eShop.slnx")
        );

        var result = await SolutionAnalyzer.AnalyzeAsync(solutionPath);

        result.EntryPoints.ShouldContain(entryPoint =>
            entryPoint.Kind == "grpc" && entryPoint.DisplayName.Contains("BasketService.GetBasket", StringComparison.Ordinal)
        );
        result.EntryPoints.ShouldContain(entryPoint =>
            entryPoint.Kind == "grpc" && entryPoint.DisplayName.Contains("BasketService.UpdateBasket", StringComparison.Ordinal)
        );
        result.EntryPoints.ShouldContain(entryPoint =>
            entryPoint.Kind == "grpc" && entryPoint.DisplayName.Contains("BasketService.DeleteBasket", StringComparison.Ordinal)
        );

        var getBasketGraph = result.CallGraphs.Single(graph =>
            graph.EntryPoint.Contains("BasketService.GetBasket", StringComparison.Ordinal)
        );
        getBasketGraph.Nodes.ShouldContain(node => node.Effects.Any(effect => effect.Provider == "redis" && effect.Operation == "read"));

        var updateBasketGraph = result.CallGraphs.Single(graph =>
            graph.EntryPoint.Contains("BasketService.UpdateBasket", StringComparison.Ordinal)
        );
        updateBasketGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "redis" && effect.Operation == "write")
        );

        var deleteBasketGraph = result.CallGraphs.Single(graph =>
            graph.EntryPoint.Contains("BasketService.DeleteBasket", StringComparison.Ordinal)
        );
        deleteBasketGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "redis" && effect.Operation == "write")
        );
    }

    [Fact(Skip = "requires eShop playground to be cloned")]
    public async Task Eshop_background_workers_and_event_handlers_are_entrypoints()
    {
        var solutionPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "playgrounds", "eShop", "eShop.slnx")
        );

        var result = await SolutionAnalyzer.AnalyzeAsync(solutionPath);

        result.EntryPoints.ShouldContain(entryPoint =>
            entryPoint.Kind == "background"
            && entryPoint.DisplayName.Contains("GracePeriodManagerService.ExecuteAsync", StringComparison.Ordinal)
        );
        result.EntryPoints.ShouldContain(entryPoint =>
            entryPoint.Kind == "eventhandler"
            && entryPoint.DisplayName.Contains("OrderStatusChangedToStockConfirmedIntegrationEventHandler.Handle", StringComparison.Ordinal)
        );

        var gracePeriodGraph = result.CallGraphs.Single(graph =>
            graph.EntryPoint.Contains("GracePeriodManagerService.ExecuteAsync", StringComparison.Ordinal)
        );
        gracePeriodGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "eventbus" && effect.Operation == "publish")
        );
        gracePeriodGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "rabbitmq" && effect.Operation == "channel_open")
        );
        gracePeriodGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "rabbitmq" && effect.Operation == "declare_exchange")
        );
        gracePeriodGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "db_connection" && effect.Operation == "open")
        );
        gracePeriodGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "db_reader" && effect.Operation == "row_read")
        );
        gracePeriodGraph.Nodes.ShouldContain(node =>
            node.Effects.Any(effect => effect.Provider == "resilience" && effect.Operation == "execute")
        );

        var paymentHandlerGraphs = result
            .CallGraphs.Where(graph =>
                graph.EntryPoint.Contains("OrderStatusChangedToStockConfirmedIntegrationEventHandler.Handle", StringComparison.Ordinal)
            )
            .ToArray();
        paymentHandlerGraphs.ShouldNotBeEmpty();
        paymentHandlerGraphs.ShouldContain(graph =>
            graph.Nodes.Any(node => node.Effects.Any(effect => effect.Provider == "eventbus" && effect.Operation == "publish"))
        );
        paymentHandlerGraphs.ShouldContain(graph =>
            graph.Nodes.Any(node =>
                node.Effects.Any(effect =>
                    effect.Provider == "rabbitmq" && effect.Operation == "publish" && effect.Method == "BasicPublishAsync"
                )
            )
        );
    }
}

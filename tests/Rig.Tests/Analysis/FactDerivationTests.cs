using Rig.Analysis;
using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// End-to-end tests for the fact-layer derivers (FactEntryPointDeriver / FactEffectDeriver)
// against the LegacyNet48Web fixture. Ground truth is the fixture source itself — these assert
// the EXACT derived set, including the negative cases that must be EXCLUDED. No SQLite: the
// fixture is analyzed in-memory and its facts projected into the deriver inputs.
[Collection(RoslynIntegrationCollection.Name)]
public sealed class FactDerivationTests(AnalyzedPlaygrounds playgrounds)
{
    private readonly AnalyzedPlaygrounds _playgrounds = playgrounds;

    [Fact]
    public async Task Entry_points_are_gated_by_the_ClientPage_base_type()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var rules = FactEntryPointRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        var entryPoints = FactEntryPointDeriver.Derive(FactProjection.EntryPointData(result), rules);

        // --- ACTION entry points: a [ClientAction] method counts ONLY when its declaring type is
        //     a ClientPage subtype. Coverage:
        //       * InvoiceMain reaches ClientPage in two NON-generic hops (via InvoiceMainBase).
        //       * ReferralPane reaches it through a GENERIC base (WorkflowPaneBase<ReferralMaster>),
        //         and WorkflowPaneBase.Save is a [ClientAction] on that generic abstract base —
        //         inherited/dispatchable, so it is kept (declaring type is a ClientPage subtype).
        //       * InvoiceGrid inherits ClientControl (NOT ClientPage), so its [ClientAction] methods
        //         must NOT be derived — the only genuine non-ClientPage action shape.
        var actionRoutes = entryPoints.Where(e => e.Kind == "action").Select(e => e.Route).OrderBy(r => r).ToArray();
        actionRoutes.ShouldBe(
            new[]
            {
                "Accounts/InvoiceMain.RefreshList",
                "Accounts/InvoiceMain.SubmitInvoice",
                "Workflows/ReferralPane.Submit",
                "Workflows/WorkflowPaneBase.Save",
            });
        actionRoutes.ShouldNotContain(r => r.Contains("InvoiceGrid", StringComparison.Ordinal));

        // --- PAGE entry points: every CONCRETE ClientPage subtype, one per constructor (Login has
        //     two), or one at the type declaration when there is no explicit constructor.
        //     Abstract bases (InvoiceMainBase, WorkflowPaneBase`1) are not navigable and EXCLUDED;
        //     ReferralPane (concrete, via a generic base) IS included.
        var pageRoutes = entryPoints.Where(e => e.Kind == "page").Select(e => e.Route).ToArray();
        pageRoutes.ShouldNotContain("Accounts/InvoiceMainBase");
        pageRoutes.ShouldNotContain(r => r.Contains("WorkflowPaneBase", StringComparison.Ordinal));
        pageRoutes.OrderBy(r => r, StringComparer.Ordinal).ShouldBe(
            new[]
            {
                "Account/Public/LegacyLogin", // PageBase reflection page (G2) — also a "page" kind
                "Account/Public/Login",
                "Account/Public/Login",
                "Account/Public/Main",
                "Accounts/InvoiceMain",
                "Accounts/TermsAndConditions",
                "Admin/UserManagement",
                "Workflows/ReferralPane",
            });
    }

    [Fact]
    public async Task PageBase_reflection_pages_are_entry_points()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var pageRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        var classRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        var entryPoints = FactEntryPointDeriver.Derive(FactProjection.EntryPointData(result), pageRules, classRules);

        // G2: LegacyLogin : PageBase (not a ClientPage) is a navigable page entry point...
        entryPoints.ShouldContain(e => e.Kind == "page" && e.Route == "Account/Public/LegacyLogin");
        // ...and its reflection-invoked lifecycle hooks are handler entry points.
        entryPoints.ShouldContain(e => e.Kind == "pagehandler" && e.Route.EndsWith("LegacyLogin.Initialise", StringComparison.Ordinal));
        entryPoints.ShouldContain(e => e.Kind == "pagehandler" && e.Route.EndsWith("LegacyLogin.OnAction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Class_inheritance_backend_entry_points_are_derived()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var pageRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        var classRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        var entryPoints = FactEntryPointDeriver.Derive(FactProjection.EntryPointData(result), pageRules, classRules);

        // classInheritance entry points = everything that isn't a page/action.
        var backend = entryPoints
            .Where(e => e.Kind is not ("page" or "action"))
            .Select(e => (e.Kind, e.Route))
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ToArray();

        // Coverage:
        //  * ReportGeneratorService : ServiceBase           -> base-edge closure + handlerMethods["Startup"]
        //  * DataSyncProcess : IBackgroundProcess           -> INTERFACE-edge closure + handlerMethods["Process"]
        //  * InvoiceWorkflowController : WorkflowControllerBase -> requireOverride: only the OVERRIDE OnSave
        //  * ClaimsService.SubmitClaim                       -> baseTypes:["*"] gated by [OperationContract]
        backend.ShouldBe(
            new[]
            {
                ("background", "LegacyNet48Web.Background.DataSyncProcess.Process"),
                ("workflow",   "LegacyNet48Web.Background.InvoiceWorkflowController.OnSave"),
                ("background", "LegacyNet48Web.Background.ReportGeneratorService.Startup"),
                ("wcf",        "LegacyNet48Web.Background.ClaimsService.SubmitClaim"),
                // PageBase reflection-page handlers (G2) — classInheritance on Initialise/OnAction.
                ("pagehandler", "LegacyNet48Web.Pages.Account.Public.LegacyLogin.Initialise"),
                ("pagehandler", "LegacyNet48Web.Pages.Account.Public.LegacyLogin.OnAction"),
            }.OrderBy(e => e.Item2, StringComparer.Ordinal).ToArray());

        // Negative cases that must be EXCLUDED:
        var routes = entryPoints.Select(e => e.Route).ToArray();
        // The abstract handler on the ROOT base type (ServiceBase.Startup) is not an entry point.
        routes.ShouldNotContain(r => r.EndsWith("ServiceBase.Startup", StringComparison.Ordinal));
        routes.ShouldNotContain(r => r.Contains("IBackgroundProcess", StringComparison.Ordinal));
        // requireOverride: a plain (non-override) method matching a handler name is excluded.
        routes.ShouldNotContain(r => r.EndsWith("InvoiceWorkflowController.HelperSave", StringComparison.Ordinal));
        routes.ShouldNotContain(r => r.Contains("WorkflowControllerBase", StringComparison.Ordinal));
        // WCF attribute gate: a method without [OperationContract] is excluded.
        routes.ShouldNotContain(r => r.EndsWith("ClaimsService.Helper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Call_graph_dispatches_base_virtual_to_override()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var graph = FactProjection.GraphData(result);

        // WorkflowPaneBase`1.Save is a VIRTUAL with an empty body — no call edges leave it. The only
        // way to reach ReferralPane.Save (its override, which writes to the DB) is base-virtual ->
        // override dispatch. This is the G6/G3 hop that makes framework virtuals / abstract
        // [ClientAction] reach the effects in their concrete overrides. The base is GENERIC, so this
        // also exercises generic-stripped dispatch.
        var reachable = FactPathFinder.Reaches(graph, "WorkflowPaneBase`1.Save");

        reachable.Keys.ShouldContain(k => k.Contains("ReferralPane.Save", StringComparison.Ordinal));
        // ...and through the override it reaches the llblgen write (SaveEntity).
        reachable.Keys.ShouldContain(k => k.Contains("SaveEntity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Throw_sites_are_extracted_and_derived_as_effects()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        // Extraction: `throw new AccessDeniedException()` in PermissionGuard.AssertRight produces a
        // throw ref whose target is the exception TYPE, enclosed by AssertRight. (The same site also
        // yields a ctor ref, but that carries no "thrown" meaning — only the throw ref does.)
        var throwRefs = FactProjection.ThrowRefs(result);
        throwRefs.ShouldContain(t =>
            t.Target.EndsWith("AccessDeniedException", StringComparison.Ordinal)
            && t.Enclosing != null && t.Enclosing.Contains("AssertRight", StringComparison.Ordinal));

        // Derivation: a MatchThrow rule gated on the exception's simple-name suffix yields an effect
        // whose resource is the thrown type. No invocation/ctor rule could ever surface this guard.
        var rule = new FactEffectRule(
            "authz", "deny",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            DeclaringTypeNameEndsWith: new[] { "AccessDeniedException" },
            MatchThrow: true,
            Resource: "receiver_type");

        var effects = FactEffectDeriver.Derive(
            FactProjection.Invocations(result), new[] { rule }, throwRefs: throwRefs);

        var authz = effects.Where(e => e.Provider == "authz").ToList();
        authz.ShouldNotBeEmpty();
        authz.ShouldAllBe(e => e.Operation == "deny");
        authz.ShouldContain(e => e.ResourceType.EndsWith("AccessDeniedException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invocation_reference_facts_carry_receiver_type()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        // InvoiceController.GetAll calls `_db.GetMultiAsync(...)`, where `_db` is a DataAdapter.
        // Stage-1 facts must now carry the receiver's static type so the effect deriver can gate
        // receiverTypes on the real receiver instead of approximating it with the declaring type.
        var getMulti = result
            .References.Where(r =>
                r.RefKind == "invocation" && r.TargetSymbolId.Contains("GetMultiAsync", StringComparison.Ordinal)
            )
            .ToArray();

        getMulti.ShouldNotBeEmpty();
        getMulti.ShouldContain(r =>
            r.ReceiverType != null && r.ReceiverType.Contains("DataAdapter", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Invocation_reference_facts_carry_first_argument_literal_and_type()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        // OutboundGateway.SendEverything calls `new HealthcodeServiceProxy().SubmitBill("<bill/>")`.
        // Stage-1 facts must now carry the first argument's string template (for http_argument /
        // string_argument resource resolution) and its static type (for argument_type), so the
        // stage-2 effect deriver can resolve the same `resource` strings the Roslyn pass does (P2a).
        var submitBill = result
            .References.Where(r =>
                r.RefKind == "invocation" && r.TargetSymbolId.Contains("SubmitBill", StringComparison.Ordinal)
            )
            .ToArray();

        submitBill.ShouldNotBeEmpty();
        submitBill.ShouldContain(r => r.FirstArgumentTemplate == "<bill/>");
        submitBill.ShouldContain(r =>
            r.FirstArgumentType != null && r.FirstArgumentType.Contains("String", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Invocation_reference_facts_carry_structural_context()
    {
        var playground = await _playgrounds.EntryPointEffectsAsync();
        var result = playground.Result;

        var invocations = result.References.Where(r => r.RefKind == "invocation").ToArray();

        // looped_effect: TeamWorkflow.LoadTeamSummaryAsync reads redis inside a `foreach` —
        // `await _redis.StringGetAsync($"team:{relatedTeamId}")`. The fact must carry the nearest
        // enclosing loop so the stage-2 observation deriver (P2b) can emit looped_effect/foreach.
        invocations.ShouldContain(r =>
            r.TargetSymbolId.Contains("StringGetAsync", StringComparison.Ordinal)
            && r.EnclosingLoopKind == "foreach"
            && r.EnclosingLoopDetail != null
            && r.EnclosingLoopDetail.Contains("relatedTeamId", StringComparison.Ordinal)
        );

        // parallel_fanout (Parallel.ForEach): TeamWorkflow.ProcessBatchAsync reads redis inside a
        // `Parallel.ForEach(teamIds, id => _redis.StringGet(...))`. The fact must carry the
        // enclosing-invocation chain so P2b can match the fanout wrapper by receiver text + method.
        // (StackExchange.Redis StringGet survives indexing; HttpClient/Task.WhenAll do not — System.*
        // targets are dropped by the runtime-assembly filter, so the Task.WhenAll(http) fanout is
        // blocked on framework-ref opt-in (decision Q2), orthogonal to this structural capture.)
        invocations.ShouldContain(r =>
            r.TargetSymbolId.Contains("StringGet", StringComparison.Ordinal)
            && r.EnclosingInvocations != null
            && r.EnclosingInvocations.Contains("Parallel", StringComparison.Ordinal)
            && r.EnclosingInvocations.Contains("ForEach", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Mvc_and_minapi_entry_point_route_literals_are_captured()
    {
        var playground = await _playgrounds.EntryPointEffectsAsync();
        var result = playground.Result;

        // MinAPI: `app.MapGet("/minapi/teams/{id}", ...)` — already an invocation ref (P1a) carrying
        // its route literal (P1b). The fact-side MinAPI deriver (P2) reads method name + first-arg.
        result.References.ShouldContain(r =>
            r.RefKind == "invocation"
            && r.TargetSymbolId.Contains("MapGet", StringComparison.Ordinal)
            && r.FirstArgumentTemplate == "/minapi/teams/{id}"
        );

        // MVC: an attribute usage resolves to the attribute constructor and is recorded as a "ctor"
        // ref. P1d captures the attribute's first positional arg, exposing the route literals.
        // Controller-level `[Route("api/[controller]")]` enclosed by TeamsController:
        result.References.ShouldContain(r =>
            r.RefKind == "ctor"
            && r.TargetSymbolId.Contains("RouteAttribute", StringComparison.Ordinal)
            && r.FirstArgumentTemplate == "api/[controller]"
            && r.EnclosingSymbolId != null
            && r.EnclosingSymbolId.Contains("TeamsController", StringComparison.Ordinal)
        );

        // Method-level `[HttpGet("{id}")]`:
        result.References.ShouldContain(r =>
            r.RefKind == "ctor"
            && r.TargetSymbolId.Contains("HttpGetAttribute", StringComparison.Ordinal)
            && r.FirstArgumentTemplate == "{id}"
        );
    }

    [Fact]
    public async Task External_provider_effects_are_derived_from_rules()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var rules = FactEffectRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        var effects = FactEffectDeriver.Derive(FactProjection.Invocations(result), rules, providerFilter: null,
            baseEdges: FactProjection.EntryPointData(result).BaseEdges);

        // G4: SOAP / HTTP-print / queue / LLM are pure rule data — the deriver matches them with no
        // code change. OutboundGateway.SendEverything invokes one of each.
        (string Provider, string Operation)[] expected =
        {
            ("soap", "submit"),
            ("http_print", "render"),
            ("queue", "enqueue"),
            ("llm", "complete"),
        };
        foreach (var (provider, operation) in expected)
        {
            effects.ShouldContain(
                e => e.Provider == provider && e.Operation == operation
                    && e.EnclosingSymbolId!.Contains("OutboundGateway.SendEverything", StringComparison.Ordinal),
                $"expected a {provider}/{operation} effect from OutboundGateway.SendEverything");
        }
    }

    [Fact]
    public async Task Derived_effect_resource_is_resolved_from_facts()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var rules = FactEffectRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        var effects = FactEffectDeriver.Derive(FactProjection.Invocations(result), rules, providerFilter: null,
            baseEdges: FactProjection.EntryPointData(result).BaseEdges);

        // receiver_type (P1a): `new HealthcodeServiceProxy().SubmitBill("<bill/>")` resolves the
        // effect resource to the receiver's static type FQN.
        effects.ShouldContain(e =>
            e.Provider == "soap" && e.Operation == "submit"
            && e.ResourceType == "LegacyNet48Web.External.HealthcodeServiceProxy"
        );

        // argument_type (P1b): `_db.StartTransaction(System.Data.IsolationLevel.ReadCommitted, ...)`
        // resolves to the FIRST ARGUMENT's type — NOT the declaring DataAccessAdapterBase. This pins
        // that the deriver resolves the resource from facts rather than using the declaring type.
        effects.ShouldContain(e =>
            e.Provider == "llblgen" && e.Operation == "tx_begin"
            && e.ResourceType == "System.Data.IsolationLevel"
        );
    }

    [Fact]
    public async Task Derived_effects_carry_structural_observations()
    {
        var playground = await _playgrounds.EntryPointEffectsAsync();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var effectRules = FactEffectRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        var observationRules = FactObservationRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        var epData = FactProjection.EntryPointData(result);
        var effects = FactEffectDeriver.Derive(
            FactProjection.Invocations(result), effectRules, providerFilter: null,
            baseEdges: epData.BaseEdges, ctorRefs: epData.CtorRefs, observationRules: observationRules);

        // looped_effect (P1c facts -> P2b deriver): TeamWorkflow.LoadTeamSummaryAsync reads redis
        // inside a `foreach`, so the redis read effect there carries a looped_effect/foreach note.
        effects.ShouldContain(e =>
            e.Provider == "redis"
            && e.Observations != null
            && e.Observations.Any(o => o.Type == "looped_effect" && o.Context == "foreach")
        );

        // parallel_fanout: TeamWorkflow.ProcessBatchAsync reads redis inside `Parallel.ForEach(...)`.
        effects.ShouldContain(e =>
            e.Provider == "redis"
            && e.Observations != null
            && e.Observations.Any(o => o.Type == "parallel_fanout" && o.Context == "Parallel.ForEach")
        );
    }

    [Fact]
    public async Task Llblgen_entity_constructor_fetches_are_derived()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var rules = FactEffectRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        var epData = FactProjection.EntryPointData(result);
        var effects = FactEffectDeriver.Derive(
            FactProjection.Invocations(result), rules, providerFilter: null,
            baseEdges: epData.BaseEdges, ctorRefs: epData.CtorRefs);

        // G5: new InvoiceEntity(pk) and new InvoiceEntity(pk, txn) are llblgen fetches; the empty
        // `new InvoiceEntity { ... }` ctor is NOT. ResourceType for a ctor-fetch is the entity type.
        var entityFetches = effects
            .Where(e => e.Provider == "llblgen" && e.Operation == "fetch"
                && e.ResourceType.Contains("InvoiceEntity", StringComparison.Ordinal))
            .ToArray();

        // Exactly the two with-argument constructors in EntityFetcher.Load — the empty ctors
        // (object initializers in EntityFetcher / WorkflowHandlers / ReferralPane) are excluded.
        entityFetches.Length.ShouldBe(2);
        entityFetches.ShouldAllBe(e => e.EnclosingSymbolId!.Contains("EntityFetcher.Load", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clientpage_proxy_effects_exclude_non_proxy_ShowDialog()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var rules = FactEffectRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory, [rulesPath]);
        // Base edges feed the ProxyBase base-type gate (the faithful generated-proxy discriminator).
        var baseEdges = FactProjection.EntryPointData(result).BaseEdges;
        var effects = FactEffectDeriver.Derive(FactProjection.Invocations(result), rules, providerFilter: null, baseEdges: baseEdges);

        var proxyEffects = effects.Where(e => e.Provider == "clientpage_proxy").ToArray();

        // The genuine navigation: InvoiceMain.SubmitInvoice -> new InvoiceEditProxy().ShowDialog().
        // InvoiceEditProxy derives ProxyBase, so its ShowDialog IS a clientpage_proxy effect.
        proxyEffects.ShouldContain(e =>
            e.Operation == "show"
            && e.ResourceType.Contains("InvoiceEditProxy", StringComparison.Ordinal)
            && e.EnclosingSymbolId!.Contains("InvoiceMain.SubmitInvoice", StringComparison.Ordinal));

        // FP #1: TermsAndConditions (a ClientPage) declares its OWN ShowDialog() and calls it.
        // It does not derive ProxyBase, so it must NOT be a clientpage_proxy effect.
        proxyEffects.ShouldNotContain(e =>
            e.ResourceType.Contains("TermsAndConditions", StringComparison.Ordinal));

        // FP #2: InvoiceServiceProxy's name ends in "Proxy" but it does NOT derive ProxyBase.
        // The base-type gate (not the name suffix) must exclude it.
        proxyEffects.ShouldNotContain(e =>
            e.ResourceType.Contains("InvoiceServiceProxy", StringComparison.Ordinal));
    }
}

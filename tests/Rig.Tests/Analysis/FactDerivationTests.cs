using Rig.Analysis;
using Rig.Analysis.Rules;
using Rig.Domain.Functions;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// End-to-end tests for the fact-layer derivers (FactEntryPointDeriver / FactEffectDeriver)
// against the LegacyNet48Web fixture. Ground truth is the fixture source itself — these assert
// the EXACT derived set, including the negative cases that must be EXCLUDED. No SQLite: the
// fixture is analyzed in-memory and its facts projected into the deriver inputs.
[Collection(RoslynIntegrationCollection.Name)]
public sealed class FactDerivationTests
{
    [Fact]
    public async Task Entry_points_are_gated_by_the_ClientPage_base_type()
    {
        using var playground = await TempPlayground.CreateLegacyNet48Async();
        var result = await SolutionAnalyzer.AnalyzeAsync(playground.SolutionPath);

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
        using var playground = await TempPlayground.CreateLegacyNet48Async();
        var result = await SolutionAnalyzer.AnalyzeAsync(playground.SolutionPath);

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
        using var playground = await TempPlayground.CreateLegacyNet48Async();
        var result = await SolutionAnalyzer.AnalyzeAsync(playground.SolutionPath);

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
        using var playground = await TempPlayground.CreateLegacyNet48Async();
        var result = await SolutionAnalyzer.AnalyzeAsync(playground.SolutionPath);

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
    public async Task External_provider_effects_are_derived_from_rules()
    {
        using var playground = await TempPlayground.CreateLegacyNet48Async();
        var result = await SolutionAnalyzer.AnalyzeAsync(playground.SolutionPath);

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
    public async Task Llblgen_entity_constructor_fetches_are_derived()
    {
        using var playground = await TempPlayground.CreateLegacyNet48Async();
        var result = await SolutionAnalyzer.AnalyzeAsync(playground.SolutionPath);

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
        using var playground = await TempPlayground.CreateLegacyNet48Async();
        var result = await SolutionAnalyzer.AnalyzeAsync(playground.SolutionPath);

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

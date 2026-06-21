using Rig.Analysis.Rules;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class FactDerivationTests(AnalyzedPlaygrounds playgrounds)
{
    [Test]
    public async Task Entry_points_are_gated_by_the_ClientPage_base_type()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var rules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]);
        var entryPoints = FactEntryPointDeriver.Derive(FactProjection.EntryPointData(result), rules.EntryPoints);

        var actionRoutes = entryPoints
            .Where(e => e.Kind == "action")
            .Select(e => e.Route)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        actionRoutes.ShouldBe(
            new[]
            {
                "Accounts/InvoiceMain.RefreshList",
                "Accounts/InvoiceMain.SubmitInvoice",
                "Workflows/ReferralPane.Submit",
                "Workflows/WorkflowPaneBase.Save",
            }
        );
        actionRoutes.ShouldNotContain(r => r.Contains("InvoiceGrid", StringComparison.Ordinal));

        var pageRoutes = entryPoints.Where(e => e.Kind == "page").Select(e => e.Route).ToArray();
        pageRoutes.ShouldNotContain("Accounts/InvoiceMainBase");
        pageRoutes.ShouldNotContain(r => r.Contains("WorkflowPaneBase", StringComparison.Ordinal));
        pageRoutes
            .OrderBy(r => r, StringComparer.Ordinal)
            .ShouldBe(
                new[]
                {
                    "Account/Public/LegacyLogin",
                    "Account/Public/Login",
                    "Account/Public/Login",
                    "Account/Public/Main",
                    "Accounts/InvoiceMain",
                    "Accounts/TermsAndConditions",
                    "Admin/UserManagement",
                    "Workflows/ReferralPane",
                }
            );
    }

    [Test]
    public async Task PageBase_reflection_pages_are_entry_points()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var pageRules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).EntryPoints;
        var classRules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).ClassInheritance;
        var entryPoints = FactEntryPointDeriver.Derive(FactProjection.EntryPointData(result), pageRules, classRules);

        entryPoints.ShouldContain(e => e.Kind == "page" && e.Route == "Account/Public/LegacyLogin");
        entryPoints.ShouldContain(e => e.Kind == "pagehandler" && e.Route.EndsWith("LegacyLogin.Initialise", StringComparison.Ordinal));
        entryPoints.ShouldContain(e => e.Kind == "pagehandler" && e.Route.EndsWith("LegacyLogin.OnAction", StringComparison.Ordinal));
    }

    [Test]
    public async Task Class_inheritance_backend_entry_points_are_derived()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var pageRules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).EntryPoints;
        var classRules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).ClassInheritance;
        var entryPoints = FactEntryPointDeriver.Derive(FactProjection.EntryPointData(result), pageRules, classRules);

        var backend = entryPoints
            .Where(e => e.Kind is not ("page" or "action"))
            .Select(e => (e.Kind, e.Route))
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ToArray();

        backend.ShouldBe(new[]
            {
                ("background", "LegacyNet48Web.Background.DataSyncProcess.Process"),
                ("workflow", "LegacyNet48Web.Background.InvoiceWorkflowController.OnSave"),
                ("background", "LegacyNet48Web.Background.ReportGeneratorService.Startup"),
                ("wcf", "LegacyNet48Web.Background.ClaimsService.SubmitClaim"),
                // Web API controller actions — detected by the builtin dotnet.webapi.controller rule
                // (System.Web.Http.ApiController + public methods = actions). Added with F2.
                ("http", "LegacyNet48Web.Controllers.InvoiceController.Approve"),
                ("http", "LegacyNet48Web.Controllers.InvoiceController.GetAll"),
                ("http", "LegacyNet48Web.Controllers.PatientController.Create"),
                ("http", "LegacyNet48Web.Controllers.PatientController.Delete"),
                ("http", "LegacyNet48Web.Controllers.PatientController.GetAll"),
                ("http", "LegacyNet48Web.Controllers.PatientController.GetById"),
                ("http", "LegacyNet48Web.Controllers.PatientController.Update"),
                ("pagehandler", "LegacyNet48Web.Pages.Account.Public.LegacyLogin.Initialise"),
                ("pagehandler", "LegacyNet48Web.Pages.Account.Public.LegacyLogin.OnAction"),
            }.OrderBy(e => e.Item2, StringComparer.Ordinal).ToArray());

        var routes = entryPoints.Select(e => e.Route).ToArray();
        routes.ShouldNotContain(r => r.EndsWith("ServiceBase.Startup", StringComparison.Ordinal));
        routes.ShouldNotContain(r => r.Contains("IBackgroundProcess", StringComparison.Ordinal));
        routes.ShouldNotContain(r => r.EndsWith("InvoiceWorkflowController.HelperSave", StringComparison.Ordinal));
        routes.ShouldNotContain(r => r.Contains("WorkflowControllerBase", StringComparison.Ordinal));
        routes.ShouldNotContain(r => r.EndsWith("ClaimsService.Helper", StringComparison.Ordinal));
    }

    [Test]
    public async Task Call_graph_dispatches_base_virtual_to_override()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var graph = FactProjection.GraphData(result);

        var reachable = FactPathFinder.Reaches(graph, "WorkflowPaneBase`1.Save");

        reachable.Keys.ShouldContain(k => k.Contains("ReferralPane.Save", StringComparison.Ordinal));
        reachable.Keys.ShouldContain(k => k.Contains("SaveEntity", StringComparison.Ordinal));
    }

    // End-to-end generic monomorphization (mine -> store -> graph -> render). Guards GenericPipeline.cs.
    private static string RenderTree(FactGraphData graph, string fromPattern)
    {
        var output = new StringWriter();
        var noEffects = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var root in FactPathFinder.BuildTree(graph, fromPattern))
        {
            TreeRenderer.RenderTreeNode(
                root,
                "",
                isLast: true,
                isRoot: true,
                noEffects,
                prune: false,
                FactRenderRules.Empty,
                noEffects,
                output
            );
        }

        return output.ToString();
    }

    // INSTANCE forwarding: a concrete entry pins QueryResult<PatientEntity, InvoiceEntity>, and its OPEN
    // forwarding receivers (QueryPipeline, OrderedPipeline — `<T, U>` in source) render concretely down the
    // chain. The .#ctor nodes resolve too (declaring binding comes from the constructed containing type).
    [Test]
    public async Task Tree_resolves_forwarded_instance_generic_receivers_from_the_concrete_entry()
    {
        var graph = FactProjection.GraphData((await playgrounds.LegacyNet48Async()).Result);

        var text = RenderTree(graph, "GenericPipelineDemo.RunConcretePipeline");

        text.ShouldContain("QueryResult<PatientEntity, InvoiceEntity>.Enumerate");
        text.ShouldContain("QueryPipeline<PatientEntity, InvoiceEntity>.Run");
        text.ShouldContain("OrderedPipeline<PatientEntity, InvoiceEntity>.Sort");
        text.ShouldNotContain("QueryPipeline<T, U>");
    }

    // STATIC-FACTORY + generic-method monomorphization (the MedDBase QueryResult/QueryPipeline.Create shape):
    // no value receiver — concretes flow through method type-argument inference and a mix of forwarded TYPE
    // (TColumn) and METHOD (RRecord) params. The whole static chain must monomorphize, methods included.
    [Test]
    public async Task Tree_monomorphizes_a_static_factory_chain_through_method_type_args()
    {
        var graph = FactProjection.GraphData((await playgrounds.LegacyNet48Async()).Result);

        var text = RenderTree(graph, "StaticPipelineDemo.RunStaticPipeline");

        text.ShouldContain("StaticResult<InvoiceEntity, PatientEntity>.Build<DataAdapter, InvoiceEntity>");
        text.ShouldContain("StaticPipeline<InvoiceEntity, PatientEntity>.Build<DataAdapter, InvoiceEntity>");
        text.ShouldContain("StaticOrderedPipeline<InvoiceEntity, PatientEntity>.Sort<DataAdapter>");
        text.ShouldNotContain("StaticPipeline<T");
    }

    [Test]
    public async Task Mined_dispatch_resolves_same_arity_overloads_exactly()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var graph = FactProjection.GraphData(playground.Result);

        var overloadEdges = FactPathFinder
            .AllDispatchEdges(graph)
            .Where(e => e.From.Contains("IDispatchWorkflows.Register(", StringComparison.Ordinal))
            .ToList();
        overloadEdges.Count.ShouldBe(2);
        overloadEdges.ShouldAllBe(e => e.Basis == "roslyn");
        foreach (var edge in overloadEdges)
        {
            edge.To.ShouldBe(edge.From.Replace("IDispatchWorkflows", "WorkflowRegistry"));
        }

        var reach = FactPathFinder.Reaches(graph, "WorkflowCaller.RegisterController");
        reach.Keys.ShouldContain(k => k.Contains("WorkflowRegistry.Register(System.Int32", StringComparison.Ordinal));
        reach.Keys.ShouldContain(k => k.Contains("ControllerRegistered", StringComparison.Ordinal));
        reach.Keys.ShouldNotContain(k => k.Contains("WorkflowRegistry.Register(System.String", StringComparison.Ordinal));
        reach.Keys.ShouldNotContain(k => k.Contains("MasterRegistered", StringComparison.Ordinal));
    }

    [Test]
    public async Task Mined_dispatch_maps_generic_interface_members_exactly()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        (result.DispatchFacts ?? []).ShouldContain(d =>
            d.Kind == "impl"
            && d.SourceMember == "M:LegacyNet48Web.Dispatch.IRepo`1.Store(`0)"
            && d.TargetMember == "M:LegacyNet48Web.Dispatch.IntRepo.Store(System.Int32)"
        );

        var graph = FactProjection.GraphData(result);
        var edge = FactPathFinder.AllDispatchEdges(graph).Single(e => e.From == "M:LegacyNet48Web.Dispatch.IRepo`1.Store(`0)");
        edge.To.ShouldBe("M:LegacyNet48Web.Dispatch.IntRepo.Store(System.Int32)");
        edge.Basis.ShouldBe("roslyn");

        var reach = FactPathFinder.Reaches(graph, "RepoCaller.Use");
        reach.Keys.ShouldContain("M:LegacyNet48Web.Dispatch.IntRepo.Store(System.Int32)");
        reach.Keys.ShouldContain(k => k.Contains("StoredInt", StringComparison.Ordinal));
    }

    [Test]
    public async Task Mined_dispatch_covers_override_chains_via_forward_closure()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var facts = result.DispatchFacts ?? [];
        facts.ShouldContain(d =>
            d.Kind == "override"
            && d.SourceMember.Contains("AlertBase.Raise", StringComparison.Ordinal)
            && d.TargetMember.Contains("EmailAlert.Raise", StringComparison.Ordinal)
        );
        facts.ShouldContain(d =>
            d.Kind == "override"
            && d.SourceMember.Contains("EmailAlert.Raise", StringComparison.Ordinal)
            && d.TargetMember.Contains("PagerAlert.Raise", StringComparison.Ordinal)
        );

        var graph = FactProjection.GraphData(result);
        var fromBase = FactPathFinder
            .AllDispatchEdges(graph)
            .Where(e => e.From.Contains("AlertBase.Raise", StringComparison.Ordinal))
            .ToList();
        fromBase
            .Select(e => e.To)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ShouldBe(new[] { "M:LegacyNet48Web.Dispatch.EmailAlert.Raise", "M:LegacyNet48Web.Dispatch.PagerAlert.Raise" });
        fromBase.ShouldAllBe(e => e.Basis == "roslyn");

        var reach = FactPathFinder.Reaches(graph, "AlertCaller.Fire");
        reach.Keys.ShouldContain("M:LegacyNet48Web.Dispatch.EmailAlert.Raise");
        reach.Keys.ShouldContain("M:LegacyNet48Web.Dispatch.PagerAlert.Raise");
    }

    [Test]
    public async Task Throw_sites_are_extracted_and_derived_as_effects()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var throwRefs = FactProjection.ThrowRefs(result);
        throwRefs.ShouldContain(t =>
            t.Target.EndsWith("AccessDeniedException", StringComparison.Ordinal)
            && t.Enclosing != null
            && t.Enclosing.Contains("AssertRight", StringComparison.Ordinal)
        );

        var rule = new FactEffectRule(
            "authz",
            "deny",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            DeclaringTypeNameEndsWith: new[] { "AccessDeniedException" },
            MatchThrow: true,
            Resource: "receiver_type"
        );

        var effects = FactEffectDeriver.Derive(FactProjection.Invocations(result), new[] { rule }, throwRefs: throwRefs);

        var authz = effects.Where(e => e.Provider == "authz").ToList();
        authz.ShouldNotBeEmpty();
        authz.ShouldAllBe(e => e.Operation == "deny");
        authz.ShouldContain(e => e.ResourceType.EndsWith("AccessDeniedException", StringComparison.Ordinal));
    }

    // Codifies the matching semantics behind the Xero/OpenAI/CefSharp effect-rule fixes (2026-06-14):
    // each rule silently produced ZERO effects because it was typed against a guessed SDK surface.
    // The three traps, locked in here:
    //   (1) the declaring-type gate must match the INTERFACE the call dispatches through
    //       (Xero `IAccountingApiAsync`), not the concrete `AccountingApi` the docs name;
    //   (2) method matching is EXACT — `GetAccounts` does NOT match `GetAccountsAsyncWithHttpInfo`;
    //   (3) resource:"declaring_type" resolves even when the receiver is statically unminable
    //       (the fluent/extension/interface case) — resource:"receiver_type" would DROP the effect,
    //       which is exactly why Flurl/OpenAI/CefSharp/Xero are all keyed on declaring_type.
    [Test]
    public void Effect_rule_matching_is_exact_and_declaring_type_resource_survives_unminable_receiver()
    {
        // A Xero-shaped call site: the app wrapper invokes the generated *interface*, async-with-http-info surface.
        var inv = new FactInvocation(
            Target: "M:Xero.NetStandard.OAuth2.Api.IAccountingApiAsync.CreateInvoicesAsyncWithHttpInfo(System.String)",
            Enclosing: "M:App.Xero2ClientIO.CreateInvoices",
            FilePath: "Xero2ClientIO.cs",
            Line: 63,
            Receiver: null
        ); // fluent/interface receiver not statically minable

        static FactEffectRule Rule(string[] methods, string[] declaringTypes, string resource) =>
            new("xero", "write", methods, declaringTypes, Array.Empty<string>(), Resource: resource);

        // OLD (broken) rule — concrete class + bare method name: misses on BOTH gates.
        FactEffectDeriver
            .Derive([inv], [Rule(["CreateInvoices"], ["Xero.NetStandard.OAuth2.Api.AccountingApi"], "declaring_type")])
            .ShouldBeEmpty();

        // Right type, wrong (non-exact) name -> still dropped.
        FactEffectDeriver
            .Derive([inv], [Rule(["CreateInvoices"], ["Xero.NetStandard.OAuth2.Api.IAccountingApiAsync"], "declaring_type")])
            .ShouldBeEmpty();

        // FIXED rule — interface declaring type + exact async-with-http-info name + declaring_type resource.
        var effect = FactEffectDeriver
            .Derive(
                [inv],
                [Rule(["CreateInvoicesAsyncWithHttpInfo"], ["Xero.NetStandard.OAuth2.Api.IAccountingApiAsync"], "declaring_type")]
            )
            .ShouldHaveSingleItem();
        effect.Provider.ShouldBe("xero");
        effect.Operation.ShouldBe("write");
        effect.ResourceType.ShouldBe("Xero.NetStandard.OAuth2.Api.IAccountingApiAsync");
        effect.EnclosingSymbolId.ShouldNotBeNull().ShouldContain("Xero2ClientIO.CreateInvoices");

        // Same correct gates but resource:"receiver_type" -> dropped, because the receiver is unminable.
        FactEffectDeriver
            .Derive(
                [inv],
                [Rule(["CreateInvoicesAsyncWithHttpInfo"], ["Xero.NetStandard.OAuth2.Api.IAccountingApiAsync"], "receiver_type")]
            )
            .ShouldBeEmpty();
    }

    // F1a: an `http_argument` rule must NOT drop the effect when the URL is a variable (the common
    // case). It keeps the literal host/path when present, else falls back to the receiver type.
    [Test]
    public void Http_argument_resource_falls_back_to_receiver_when_url_is_not_a_literal()
    {
        var rule = new FactEffectRule(
            "http",
            "POST",
            ["PostAsync"],
            ["System.Net.Http.HttpClient"],
            Array.Empty<string>(),
            Resource: "http_argument"
        );

        // Variable URL (no literal template) with a known receiver -> effect kept, resource = receiver type.
        var variableUrl = new FactInvocation(
            Target: "M:System.Net.Http.HttpClient.PostAsync(System.Uri,System.Net.Http.HttpContent)",
            Enclosing: "M:App.WebhookHttpClient.Send",
            FilePath: "WebhookHttpClient.cs",
            Line: 46,
            Receiver: "System.Net.Http.HttpClient"
        );
        var kept = FactEffectDeriver.Derive([variableUrl], [rule]).ShouldHaveSingleItem();
        kept.Provider.ShouldBe("http");
        kept.ResourceType.ShouldBe("System.Net.Http.HttpClient");

        // A literal URL template still yields the normalized host/path (precision preserved).
        var literalUrl = variableUrl with
        {
            FirstArgTemplate = "https://api.example.com/hook/",
        };
        FactEffectDeriver.Derive([literalUrl], [rule]).ShouldHaveSingleItem().ResourceType.ShouldBe("api.example.com/hook");
    }

    [Test]
    public async Task Invocation_reference_facts_carry_receiver_type()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var getMulti = result
            .References!.Where(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("GetMultiAsync", StringComparison.Ordinal))
            .ToArray();

        getMulti.ShouldNotBeEmpty();
        getMulti.ShouldContain(r => r.ReceiverType != null && r.ReceiverType.Contains("DataAdapter", StringComparison.Ordinal));
    }

    [Test]
    public async Task Invocation_reference_facts_carry_first_argument_literal_and_type()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var submitBill = result
            .References!.Where(r => r.RefKind == "invocation" && r.TargetSymbolId.Contains("SubmitBill", StringComparison.Ordinal))
            .ToArray();

        submitBill.ShouldNotBeEmpty();
        submitBill.ShouldContain(r => r.FirstArgumentTemplate == "<bill/>");
        submitBill.ShouldContain(r =>
            r.FirstArgumentType != null && r.FirstArgumentType.Contains("String", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Test]
    public async Task Invocation_reference_facts_carry_structural_context()
    {
        var playground = await playgrounds.EntryPointEffectsAsync();
        var result = playground.Result;

        var invocations = result.References!.Where(r => r.RefKind == "invocation").ToArray();

        invocations.ShouldContain(r =>
            r.TargetSymbolId.Contains("StringGetAsync", StringComparison.Ordinal)
            && r.EnclosingLoopKind == "foreach"
            && r.EnclosingLoopDetail != null
            && r.EnclosingLoopDetail.Contains("relatedTeamId", StringComparison.Ordinal)
        );

        invocations.ShouldContain(r =>
            r.TargetSymbolId.Contains("StringGet", StringComparison.Ordinal)
            && r.EnclosingInvocations != null
            && r.EnclosingInvocations.Contains("Parallel", StringComparison.Ordinal)
            && r.EnclosingInvocations.Contains("ForEach", StringComparison.Ordinal)
        );
    }

    [Test]
    public async Task Mvc_and_minapi_entry_point_route_literals_are_captured()
    {
        var playground = await playgrounds.EntryPointEffectsAsync();
        var result = playground.Result;

        result.References!.ShouldContain(r =>
            r.RefKind == "invocation"
            && r.TargetSymbolId.Contains("MapGet", StringComparison.Ordinal)
            && r.FirstArgumentTemplate == "/minapi/teams/{id}"
        );

        result.References!.ShouldContain(r =>
            r.RefKind == "ctor"
            && r.TargetSymbolId.Contains("RouteAttribute", StringComparison.Ordinal)
            && r.FirstArgumentTemplate == "api/[controller]"
            && r.EnclosingSymbolId != null
            && r.EnclosingSymbolId.Contains("TeamsController", StringComparison.Ordinal)
        );

        result.References!.ShouldContain(r =>
            r.RefKind == "ctor"
            && r.TargetSymbolId.Contains("HttpGetAttribute", StringComparison.Ordinal)
            && r.FirstArgumentTemplate == "{id}"
        );
    }

    [Test]
    public async Task External_provider_effects_are_derived_from_rules()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rules = RuleSetLoader.Load(playground.WorkingDirectory);
        var effects = FactEffectDeriver.Derive(
            FactProjection.Invocations(result),
            rules.Effects,
            providerFilter: null,
            baseEdges: FactProjection.EntryPointData(result).BaseEdges
        );

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
                e =>
                    e.Provider == provider
                    && e.Operation == operation
                    && e.EnclosingSymbolId!.Contains("OutboundGateway.SendEverything", StringComparison.Ordinal),
                $"expected a {provider}/{operation} effect from OutboundGateway.SendEverything"
            );
        }
    }

    // #16: Echo actor effects attribute to the routing target (the ProcessDns member path at arg 0),
    // and Flurl URL building scopes the http effect to the path-segment literal. The implicit-target
    // tellSelf is excluded so its MESSAGE argument is never mislabeled as a process name.
    [Test]
    public async Task Echo_actor_and_flurl_route_effects_resolve_their_first_argument_target()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rules = RuleSetLoader.Load(playground.WorkingDirectory);
        var effects = FactEffectDeriver.Derive(
            FactProjection.Invocations(result),
            rules.Effects,
            providerFilter: null,
            baseEdges: FactProjection.EntryPointData(result).BaseEdges
        );

        var actor = effects
            .Where(e => e.Provider == "actor" && e.EnclosingSymbolId!.Contains("OutboundGateway.SendEverything", StringComparison.Ordinal))
            .ToArray();

        actor.ShouldContain(e => e.Operation == "spawn" && e.ResourceType == "ProcessDns.WorkerName");
        actor.ShouldContain(e => e.Operation == "tell" && e.ResourceType == "ProcessDns.AccountService");
        actor.ShouldContain(e => e.Operation == "ask" && e.ResourceType == "ProcessDns.AccountService");

        // Implicit-target tellSelf is NOT in the tell rule: no actor effect names the message expression.
        actor.ShouldNotContain(e => e.ResourceType.Contains("self-msg", StringComparison.Ordinal));
        actor.ShouldNotContain(e => e.Operation == "tell" && e.ResourceType != "ProcessDns.AccountService");

        effects.ShouldContain(e =>
            e.Provider == "http"
            && e.Operation == "route"
            && e.ResourceType == "submit"
            && e.EnclosingSymbolId!.Contains("OutboundGateway.SendEverything", StringComparison.Ordinal)
        );
    }

    [Test]
    public async Task Derived_effect_resource_is_resolved_from_facts()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var rules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).Effects;
        var effects = FactEffectDeriver.Derive(
            FactProjection.Invocations(result),
            rules,
            providerFilter: null,
            baseEdges: FactProjection.EntryPointData(result).BaseEdges
        );

        effects.ShouldContain(e =>
            e.Provider == "soap" && e.Operation == "submit" && e.ResourceType == "LegacyNet48Web.External.HealthcodeServiceProxy"
        );

        effects.ShouldContain(e => e.Provider == "llblgen" && e.Operation == "tx_begin" && e.ResourceType == "System.Data.IsolationLevel");
    }

    [Test]
    public async Task Derived_effects_carry_structural_observations()
    {
        var playground = await playgrounds.EntryPointEffectsAsync();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var effectRules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).Effects;
        var observationRules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).Observations;
        var epData = FactProjection.EntryPointData(result);
        var effects = FactEffectDeriver.Derive(
            FactProjection.Invocations(result),
            effectRules,
            providerFilter: null,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs,
            observationRules: observationRules
        );

        effects.ShouldContain(e =>
            e.Provider == "redis" && e.Observations != null && e.Observations.Any(o => o.Type == "looped_effect" && o.Context == "foreach")
        );

        effects.ShouldContain(e =>
            e.Provider == "redis"
            && e.Observations != null
            && e.Observations.Any(o => o.Type == "parallel_fanout" && o.Context == "Parallel.ForEach")
        );
    }

    [Test]
    public async Task Llblgen_entity_constructor_fetches_are_derived()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var rules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).Effects;
        var epData = FactProjection.EntryPointData(result);
        var effects = FactEffectDeriver.Derive(
            FactProjection.Invocations(result),
            rules,
            providerFilter: null,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs
        );

        var entityFetches = effects
            .Where(e =>
                e.Provider == "llblgen" && e.Operation == "fetch" && e.ResourceType.Contains("InvoiceEntity", StringComparison.Ordinal)
            )
            .ToArray();

        entityFetches.Length.ShouldBe(2);
        entityFetches.ShouldAllBe(e => e.EnclosingSymbolId!.Contains("EntityFetcher.Load", StringComparison.Ordinal));
    }

    [Test]
    public async Task Clientpage_proxy_effects_exclude_non_proxy_ShowDialog()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var rules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).Effects;
        var baseEdges = FactProjection.EntryPointData(result).BaseEdges;
        var effects = FactEffectDeriver.Derive(FactProjection.Invocations(result), rules, providerFilter: null, baseEdges: baseEdges);

        var proxyEffects = effects.Where(e => e.Provider == "clientpage_proxy").ToArray();

        proxyEffects.ShouldContain(e =>
            e.Operation == "show"
            && e.ResourceType.Contains("InvoiceEditProxy", StringComparison.Ordinal)
            && e.EnclosingSymbolId!.Contains("InvoiceMain.SubmitInvoice", StringComparison.Ordinal)
        );

        proxyEffects.ShouldNotContain(e => e.ResourceType.Contains("TermsAndConditions", StringComparison.Ordinal));
        proxyEffects.ShouldNotContain(e => e.ResourceType.Contains("InvoiceServiceProxy", StringComparison.Ordinal));
    }

    [Test]
    public async Task Source_generated_clientpage_proxies_are_indexed()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var typeNames = (playground.Result.Symbols ?? []).Where(s => s.Kind == "type").Select(s => s.Name).ToArray();

        typeNames.ShouldContain("LoginProxy");
    }

    [Test]
    public async Task Lock_statements_lift_to_synthetic_monitor_acquire_release_effects()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        // --- Extraction: IncrementUnderLock's single `lock (_gate) {}` emits a synthetic
        //     Monitor.Enter (acquire) and Monitor.Exit (release) invocation ref, enclosed by the
        //     method. Neither exists as invocation syntax — they come purely from the lowering.
        var monitorRefs = result
            .References!.Where(r =>
                r.RefKind == "invocation"
                && r.TargetSymbolId.Contains("System.Threading.Monitor", StringComparison.Ordinal)
                && r.EnclosingSymbolId != null
                && r.EnclosingSymbolId.Contains("LockZoo.IncrementUnderLock", StringComparison.Ordinal)
            )
            .ToArray();
        var enter = monitorRefs.FirstOrDefault(r => r.TargetSymbolId.Contains(".Enter", StringComparison.Ordinal));
        var exit = monitorRefs.FirstOrDefault(r => r.TargetSymbolId.Contains(".Exit", StringComparison.Ordinal));
        enter.ShouldNotBeNull();
        exit.ShouldNotBeNull();
        // Release is pinned to a LATER line than acquire — the pair straddles the locked body.
        exit!.Line.ShouldBeGreaterThan(enter!.Line);

        // --- Derivation: those refs become lock:acquire / lock:release effects via the EXISTING
        //     data-driven lock rule (no rule change). Ground truth: the explicit Monitor.Enter/Exit
        //     call in ExplicitMonitor derives the SAME effect, so synthetic and real paths agree.
        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var rules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).Effects;
        var effects = FactEffectDeriver.Derive(FactProjection.Invocations(result), rules);
        var lockEffects = effects.Where(e => e.Provider == "lock").ToArray();

        foreach (var method in new[] { "LockZoo.IncrementUnderLock", "LockZoo.SubmitUnderLock", "LockZoo.ExplicitMonitor" })
        {
            lockEffects.ShouldContain(
                e => e.Operation == "acquire" && e.EnclosingSymbolId!.Contains(method, StringComparison.Ordinal),
                $"expected a lock:acquire in {method}"
            );
            lockEffects.ShouldContain(
                e => e.Operation == "release" && e.EnclosingSymbolId!.Contains(method, StringComparison.Ordinal),
                $"expected a lock:release in {method}"
            );
        }

        // Nested locks: BOTH `lock` statements lift -> two acquires in NestedLocks.
        lockEffects
            .Count(e => e.Operation == "acquire" && e.EnclosingSymbolId!.Contains("LockZoo.NestedLocks", StringComparison.Ordinal))
            .ShouldBe(2);

        // #8 setup: SubmitUnderLock holds the lock ACROSS a SOAP submit — both effects land in the
        // same method (ordering/nesting is NOT asserted here; that is the deriver work in #8).
        effects.ShouldContain(e =>
            e.Provider == "soap"
            && e.Operation == "submit"
            && e.EnclosingSymbolId!.Contains("LockZoo.SubmitUnderLock", StringComparison.Ordinal)
        );
    }

    [Test]
    public async Task Resource_span_observation_proves_effect_nested_in_transaction_or_lock_scope()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var result = playground.Result;

        // --- Extraction: the SOAP call in SubmitInsideTransaction carries its enclosing using-scope
        //     (a FakeTransaction), and the one in SubmitWithoutTransaction carries no scope.
        var submitInTx = result.References!.Single(r =>
            r.RefKind == "invocation"
            && r.TargetSymbolId.Contains("SubmitBill", StringComparison.Ordinal)
            && r.EnclosingSymbolId!.Contains("TransactionZoo.SubmitInsideTransaction", StringComparison.Ordinal)
        );
        var scopes = FactStructuralContext.DecodeScopes(submitInTx.EnclosingScopes);
        scopes.ShouldContain(s => s.Kind == "using" && s.Type.Contains("Transaction", StringComparison.Ordinal));

        var submitNoTx = result.References!.Single(r =>
            r.RefKind == "invocation"
            && r.TargetSymbolId.Contains("SubmitBill", StringComparison.Ordinal)
            && r.EnclosingSymbolId!.Contains("TransactionZoo.SubmitWithoutTransaction", StringComparison.Ordinal)
        );
        FactStructuralContext.DecodeScopes(submitNoTx.EnclosingScopes).ShouldBeEmpty();

        // --- Derivation: the soap effect inside the using(transaction) gets a transaction_spans_effect
        //     observation; the lock-wrapped soap (LockZoo.SubmitUnderLock) gets lock_held_across_effect.
        var rulesPath = Path.Combine(playground.WorkingDirectory, "rig.rules.json");
        var effectRules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).Effects;
        var observationRules = RuleSetLoader.Load(playground.WorkingDirectory, [rulesPath]).Observations;
        var effects = FactEffectDeriver.Derive(
            FactProjection.Invocations(result),
            effectRules,
            providerFilter: null,
            observationRules: observationRules
        );

        DerivedEffect SoapIn(string method) =>
            effects.Single(e =>
                e.Provider == "soap" && e.Operation == "submit" && e.EnclosingSymbolId!.Contains(method, StringComparison.Ordinal)
            );

        SoapIn("TransactionZoo.SubmitInsideTransaction")
            .Observations!.ShouldContain(o => o.Type == "transaction_spans_effect" && o.Context == "transaction");

        SoapIn("LockZoo.SubmitUnderLock").Observations!.ShouldContain(o => o.Type == "lock_held_across_effect" && o.Context == "lock");

        // Negative: a SOAP call OUTSIDE any transaction carries no span observation.
        var soapNoTx = SoapIn("TransactionZoo.SubmitWithoutTransaction");
        (soapNoTx.Observations ?? []).ShouldNotContain(o => o.Type == "transaction_spans_effect");

        // Deny-list discipline: the lock's OWN acquire/release (provider "lock", excluded) must NOT
        // be self-flagged as held across itself — otherwise every lock would observe itself.
        effects
            .Where(e => e.Provider == "lock")
            .ShouldAllBe(e => e.Observations == null || e.Observations.All(o => o.Type != "lock_held_across_effect"));
    }

    // FR-1(a): an Atom.Swap invocation derives a shared_state:mutate effect via the existing invocation
    // rule path (method name + receiver-type gate, resource:receiver_type). This is the !10706 culprit
    // shape (atomic RMW on a shared Atom cell).
    [Test]
    public void Atom_swap_invocation_derives_a_shared_state_mutate_effect()
    {
        var swap = new FactInvocation(
            Target: "M:LanguageExt.Atom`1.Swap(System.Func{`0,`0})",
            Enclosing: "M:App.FieldEntityModel.MarkIntraImportFastPathConflicts",
            FilePath: "FieldEntityModel.cs",
            Line: 122,
            Receiver: "LanguageExt.Atom<A>"
        );

        var rule = new FactEffectRule(
            Provider: "shared_state",
            Operation: "mutate",
            Methods: new[] { "Swap", "SwapAsync" },
            DeclaringTypes: Array.Empty<string>(),
            ReceiverTypes: new[] { "LanguageExt.Atom" },
            Resource: "receiver_type"
        );

        var effect = FactEffectDeriver.Derive([swap], [rule]).ShouldHaveSingleItem();
        effect.Provider.ShouldBe("shared_state");
        effect.Operation.ShouldBe("mutate");
        effect.ResourceType.ShouldBe("LanguageExt.Atom<A>");
        effect.EnclosingSymbolId.ShouldNotBeNull().ShouldContain("MarkIntraImportFastPathConflicts");

        // The name-colliding static helper MMS.Swap.Always (a class literally named "Swap") must NOT match
        // — the receiver-type gate is what distinguishes the Atom contract from an unrelated method.
        var collidingHelper = new FactInvocation(
            Target: "M:MMS.Swap.Always``1(``0@,``0@)",
            Enclosing: "M:App.Helper.Do",
            FilePath: "Helper.cs",
            Line: 1,
            Receiver: null
        );
        FactEffectDeriver.Derive([collidingHelper], [rule]).ShouldBeEmpty();
    }

    // FR-1(b): a WRITE ref whose target is a STATIC field derives a shared_state:mutate effect keyed to
    // the writing method, resolved to the field's declaring type.
    [Test]
    public void Write_to_a_static_field_derives_a_shared_state_mutate_effect()
    {
        var rule = new FactEffectRule(
            Provider: "shared_state",
            Operation: "mutate",
            Methods: Array.Empty<string>(),
            DeclaringTypes: Array.Empty<string>(),
            ReceiverTypes: Array.Empty<string>(),
            MatchFieldWrite: true,
            Resource: "declaring_type"
        );

        // The caller (Reads.LoadStaticFieldWriteRefsAsync) pre-filters to STATIC targets; the deriver is
        // handed only those, so this ref stands for a write to a static field.
        var staticWrite = new FactFieldWrite(
            Target: "F:App.GlobalCache.SharedCounter",
            Enclosing: "M:App.Importer.Run",
            FilePath: "Importer.cs",
            Line: 42
        );

        var effect = FactEffectDeriver.Derive([], [rule], staticFieldWriteRefs: [staticWrite]).ShouldHaveSingleItem();
        effect.Provider.ShouldBe("shared_state");
        effect.Operation.ShouldBe("mutate");
        effect.ResourceType.ShouldBe("App.GlobalCache");
        effect.EnclosingSymbolId.ShouldNotBeNull().ShouldContain("Importer.Run");

        // resource:"declaring_type" applies the type gate to the slot's declaring type; a non-matching
        // namespace gate drops it.
        var gated = rule with
        {
            DeclaringTypes = new[] { "Other.Namespace" },
        };
        FactEffectDeriver.Derive([], [gated], staticFieldWriteRefs: [staticWrite]).ShouldBeEmpty();
    }

    // FR-1(b) negative: a field-write rule fires ONLY on the static write refs it is handed. An
    // invocation/ctor/throw input never produces a shared_state:mutate from a MatchFieldWrite rule, and
    // an empty static-write list yields nothing (the instance/local-field case the loader filters out).
    [Test]
    public void Field_write_rule_does_not_fire_without_static_write_refs()
    {
        var rule = new FactEffectRule(
            Provider: "shared_state",
            Operation: "mutate",
            Methods: Array.Empty<string>(),
            DeclaringTypes: Array.Empty<string>(),
            ReceiverTypes: Array.Empty<string>(),
            MatchFieldWrite: true,
            Resource: "declaring_type"
        );

        // No static write refs supplied (an instance/local field write is filtered out upstream) -> none.
        FactEffectDeriver.Derive([], [rule], staticFieldWriteRefs: []).ShouldBeEmpty();

        // An ordinary invocation must NOT be matched by a field-write rule (the arms are disjoint).
        var inv = new FactInvocation(
            Target: "M:App.Foo.Bar(System.Int32)",
            Enclosing: "M:App.Caller.Do",
            FilePath: "Caller.cs",
            Line: 1,
            Receiver: "App.Foo"
        );
        FactEffectDeriver.Derive([inv], [rule], staticFieldWriteRefs: []).ShouldBeEmpty();
    }
}

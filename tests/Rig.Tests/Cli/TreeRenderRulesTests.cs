using Rig.Cli;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Cli;

public sealed class TreeRenderRulesTests
{
    private static TraceNode Node(string id, params TraceNode[] children) => new(id, "invocation", null, null, children);

    private static TraceNode Dispatch(string id, int fanout, params TraceNode[] children) =>
        new(id, "impl-dispatch", null, null, children, Fanout: fanout);

    private static Dictionary<string, List<string>> Effects(params (string Sym, string Effect)[] pairs)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (sym, effect) in pairs)
        {
            (map.TryGetValue(sym, out var l) ? l : map[sym] = new List<string>()).Add(effect);
        }

        return map;
    }

    private static string Render(
        TraceNode root,
        FactRenderRules rules,
        IReadOnlyDictionary<string, List<string>> effects,
        bool prune = false,
        IReadOnlyDictionary<string, List<string>>? seamEffects = null
    )
    {
        var output = new StringWriter();
        seamEffects ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        TreeRenderer.RenderTreeNode(root, prefix: "", isLast: true, isRoot: true, effects, prune, rules, seamEffects, output);
        return output.ToString();
    }

    // A node carrying generic monomorphization bindings (JSON C:/T:/M: token arrays), as the mine produces.
    private static TraceNode Bind(string id, string? declaring, string? method, params TraceNode[] children) =>
        new(id, "invocation", null, null, children, DeclaringTypeArgBinding: declaring, MethodTypeArgBinding: method);

    [Test]
    public void Concrete_declaring_binding_substitutes_the_declaring_type_placeholders()
    {
        var root = Node("M:App.Caller.Go()", Bind("M:App.QueryPipeline`2.Enumerate()", """["C:App.Account","C:App.Invoice"]""", null));

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryPipeline<Account, Invoice>.Enumerate");
        output.ShouldNotContain("QueryPipeline<T, U>");
    }

    [Test]
    public void Open_forwarding_child_inherits_the_parents_instantiation_via_T_tokens()
    {
        // QueryResult<Account, Invoice>.Create's body calls QueryPipeline<T, U>.Create where T,U forward
        // QueryResult's TYPE params (tokens T:0, T:1) — the child carries no concrete of its own.
        var root = Node(
            "M:App.Caller.Go()",
            Bind(
                "M:App.QueryResult`2.Create()",
                """["C:App.Account","C:App.Invoice"]""",
                null,
                Bind("M:App.QueryPipeline`2.Enumerate()", """["T:0","T:1"]""", null)
            )
        );

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryResult<Account, Invoice>.Create");
        output.ShouldContain("QueryPipeline<Account, Invoice>.Enumerate");
        output.ShouldNotContain("QueryPipeline<T, U>");
    }

    [Test]
    public void Forwarding_tokens_respect_argument_order()
    {
        var root = Node(
            "M:App.Caller.Go()",
            Bind(
                "M:App.QueryResult`2.Create()",
                """["C:App.Account","C:App.Invoice"]""",
                null,
                Bind("M:App.QueryPipeline`2.Enumerate()", """["T:1","T:0"]""", null)
            )
        );

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryPipeline<Invoice, Account>.Enumerate");
    }

    [Test]
    public void Method_arity_substitutes_from_the_method_binding_including_M_token_forwarding()
    {
        // The real static-factory shape: QueryResult.Create<Entity, Account> (declaring C:, method C:) whose
        // body calls QueryPipeline<RRecord(M:1), TColumn(T:1)>.Create<TEntity(M:0), RRecord(M:1)>.
        var root = Node(
            "M:App.Caller.Go()",
            Bind(
                "M:App.QueryResult`2.Create``2()",
                """["C:App.Account","C:App.Row"]""",
                """["C:App.Entity","C:App.Account"]""",
                Bind("M:App.QueryPipeline`2.Create``2()", """["M:1","T:1"]""", """["M:0","M:1"]""")
            )
        );

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryResult<Account, Row>.Create<Entity, Account>");
        // M:1 -> parent method arg[1]=Account, T:1 -> parent declaring arg[1]=Row; method M:0->Entity, M:1->Account.
        output.ShouldContain("QueryPipeline<Account, Row>.Create<Entity, Account>");
    }

    [Test]
    public void Binding_propagates_through_a_chain_of_open_generics()
    {
        var root = Node(
            "M:App.Caller.Go()",
            Bind(
                "M:App.QueryResult`2.Create()",
                """["C:App.Account","C:App.Invoice"]""",
                null,
                Bind(
                    "M:App.QueryPipeline`2.Create()",
                    """["T:0","T:1"]""",
                    null,
                    Bind("M:App.OrderedQueryPipeline`2.Create()", """["T:0","T:1"]""", null)
                )
            )
        );

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryPipeline<Account, Invoice>.Create");
        output.ShouldContain("OrderedQueryPipeline<Account, Invoice>.Create");
    }

    [Test]
    public void Forwarding_without_a_parent_binding_keeps_placeholders()
    {
        // T-tokens present but no parent instantiation to resolve them against -> nothing to substitute.
        var root = Node("M:App.Caller.Go()", Bind("M:App.QueryPipeline`2.Enumerate()", """["T:0","T:1"]""", null));

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryPipeline<T, U>.Enumerate");
    }

    [Test]
    public void Without_a_binding_the_label_keeps_open_placeholders()
    {
        var root = Node("M:App.Caller.Go()", Node("M:App.QueryPipeline`2.Enumerate()"));

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryPipeline<T, U>.Enumerate");
    }

    [Test]
    public void Method_arity_keeps_placeholders_when_only_the_declaring_binding_is_present()
    {
        // Declaring arity 2 substituted; the generic method's own arity 1 has no binding -> stays <T>.
        var root = Node("M:App.Caller.Go()", Bind("M:App.QueryPipeline`2.Map``1()", """["C:App.Account","C:App.Invoice"]""", null));

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryPipeline<Account, Invoice>.Map<T>");
    }

    [Test]
    public void An_arity_mismatch_between_binding_and_declaring_type_keeps_placeholders()
    {
        // Binding has 1 arg but the declaring type arity is 2 — refuse to substitute (safety).
        var root = Node("M:App.Caller.Go()", Bind("M:App.QueryPipeline`2.Enumerate()", """["C:App.Account"]""", null));

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryPipeline<T, U>.Enumerate");
    }

    [Test]
    public void A_lambda_passes_the_enclosing_methods_instantiation_through_to_its_body_calls()
    {
        // A `…~λN` lambda shares its enclosing method's type scope: a forwarding call in its body (e.g.
        // `skip: i => Create(...)`) must resolve against the lambda's PARENT instantiation, not reset to null.
        var lambdaId = "M:App.QueryPipeline`2.Create``2()~λ0";
        var root = Node(
            "M:App.Caller.Go()",
            Bind(
                "M:App.QueryPipeline`2.Create``2()",
                """["C:App.Account","C:App.Invoice"]""",
                """["C:App.Entity","C:App.Account"]""",
                // The lambda node carries no binding of its own.
                new TraceNode(
                    lambdaId,
                    "invocation",
                    null,
                    null,
                    [Bind("M:App.QueryPipeline`2.Create``2()", """["T:0","T:1"]""", """["M:0","M:1"]""")]
                )
            )
        );

        var output = Render(root, FactRenderRules.Empty, Effects());

        // The body call resolves T:/M: through the lambda against QueryPipeline.Create's instantiation.
        output.ShouldContain("QueryPipeline<Account, Invoice>.Create<Entity, Account>");
    }

    [Test]
    public void An_impl_dispatch_node_inherits_the_dispatch_sources_instantiation()
    {
        // IQueryResult<Account, Invoice>.Enumerate dispatches to the impl QueryResult<T,U>.Enumerate on the
        // SAME runtime instantiation; the impl (and its forwarding body) inherit the source's concrete args.
        var root = Node(
            "M:App.Caller.Go()",
            Bind(
                "M:App.IQueryResult`2.Enumerate()",
                """["C:App.Account","C:App.Invoice"]""",
                null,
                Dispatch("M:App.QueryResult`2.Enumerate()", 1, Bind("M:App.QueryPipeline`2.Enumerate()", """["T:0","T:1"]""", null))
            )
        );

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryResult<Account, Invoice>.Enumerate");
        output.ShouldContain("QueryPipeline<Account, Invoice>.Enumerate");
        output.ShouldNotContain("QueryResult<T, U>");
    }

    [Test]
    public void A_single_impl_fold_carries_the_interface_binding_onto_the_promoted_impl()
    {
        // IQueryResult<Account, Invoice>.Enumerate (concrete) folds its lone impl-dispatch child
        // QueryResult<T,U>.Enumerate into its slot. The fold must transfer the interface's binding so the
        // promoted impl — and its forwarding body — still monomorphize.
        var iface = Bind(
            "M:App.IQueryResult`2.Enumerate()",
            """["C:App.Account","C:App.Invoice"]""",
            null,
            Dispatch("M:App.QueryResult`2.Enumerate()", 1, Bind("M:App.QueryPipeline`2.Enumerate()", """["T:0","T:1"]""", null))
        );

        var folded = TreeRenderer.FoldSingleImplHops(iface, new Dictionary<string, List<string>>(StringComparer.Ordinal));
        var output = new StringWriter();
        TreeRenderer.RenderTreeNode(
            folded,
            "",
            isLast: true,
            isRoot: true,
            new Dictionary<string, List<string>>(StringComparer.Ordinal),
            prune: false,
            FactRenderRules.Empty,
            new Dictionary<string, List<string>>(StringComparer.Ordinal),
            output
        );
        var text = output.ToString();

        text.ShouldContain("QueryResult<Account, Invoice>.Enumerate");
        text.ShouldContain("«via"); // the folded-away interface marker is still shown
        text.ShouldContain("QueryPipeline<Account, Invoice>.Enumerate");
        text.ShouldNotContain("<T, U>");
    }

    [Test]
    public void An_unresolvable_token_keeps_only_that_positions_placeholder()
    {
        // "?" (a composite like Seq<T>) and an out-of-range forward both leave their slot as a placeholder,
        // while the resolvable position is substituted — a partial binding still helps.
        var root = Node(
            "M:App.Caller.Go()",
            Bind(
                "M:App.QueryResult`2.Create()",
                """["C:App.Account","C:App.Invoice"]""",
                null,
                Bind("M:App.QueryPipeline`2.Enumerate()", """["T:0","?"]""", null)
            )
        );

        var output = Render(root, FactRenderRules.Empty, Effects());

        output.ShouldContain("QueryPipeline<Account, U>.Enumerate");
    }

    [Test]
    public void Empty_render_rules_match_nothing()
    {
        FactRenderRules.Empty.MatchCollapseSeam("M:Ns.IService.Startup()").ShouldBeNull();
        FactRenderRules.Empty.MatchOpaque("M:Ns.Whatever.Do()").ShouldBeNull();
        FactRenderRules.Empty.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void Type_pattern_matches_declaring_type_not_a_parameter_type_in_the_signature()
    {
        var rules = new FactRenderRules([], [new FactRenderRule("Echo.", "actor framework")]);
        rules.MatchOpaque("M:Echo.ActorContext.System(Echo.ProcessId)")!.Label.ShouldBe("actor framework");
        rules.MatchOpaque("M:App.Data.ImportSubscribeMsg.#ctor(App.ImportId,Echo.ProcessId)").ShouldBeNull();
    }

    [Test]
    public void Collapse_seam_matches_the_hub_by_case_insensitive_substring()
    {
        var rules = new FactRenderRules([new FactRenderRule("IService.Startup", "service-locator")], []);
        rules.MatchCollapseSeam("M:Ns.IService.Startup()")!.Label.ShouldBe("service-locator");
        rules.MatchCollapseSeam("M:NS.iservice.startup()")!.Label.ShouldBe("service-locator");
        rules.MatchCollapseSeam("M:Ns.AppStartupProcesses.Startup()").ShouldBeNull();
    }

    [Test]
    public void Collapse_folds_a_fanout_hubs_children_into_one_summary_line_with_effect_union()
    {
        var hub = Node(
            "M:Ns.IService.Startup()",
            Dispatch("M:Ns.A.Startup()", 3, Node("M:Ns.A.DoDeep()")),
            Dispatch("M:Ns.B.Startup()", 3),
            Dispatch("M:Ns.C.Startup()", 3)
        );
        var rules = new FactRenderRules([new FactRenderRule("IService.Startup", "service-locator")], []);
        var effects = Effects(
            ("M:Ns.A.Startup()", "💾 db:write Foo"),
            ("M:Ns.A.DoDeep()", "🔍 db:read Bar"),
            ("M:Ns.B.Startup()", "💾 db:write Foo")
        );

        var output = Render(hub, rules, effects);

        output.ShouldContain("IService.Startup");
        output.ShouldContain("3 dispatch targets collapsed");
        output.ShouldContain("[seam: service-locator]");
        output.ShouldContain("{💾 db:write Foo}");
        output.ShouldContain("{🔍 db:read Bar}");
        output.IndexOf("💾 db:write Foo").ShouldBe(output.LastIndexOf("💾 db:write Foo"));
        output.ShouldNotContain("A.Startup");
        output.ShouldNotContain("DoDeep");
        output.ShouldContain("4 lines hidden");
    }

    [Test]
    public void Collapse_prefers_the_precomputed_realistic_summary_over_the_truncated_subtree()
    {
        var hub = Node("M:Ns.IService.Startup()", Dispatch("M:Ns.A.Startup()", 3));
        var rules = new FactRenderRules([new FactRenderRule("IService.Startup", "service-locator")], []);
        var effects = Effects(("M:Ns.A.Startup()", "💾 db:write Foo"));
        var seamEffects = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["M:Ns.IService.Startup()"] = ["📥 llblgen:fetch ×42", "🔍 llblgen:read ×31"],
        };

        var output = Render(hub, rules, effects, seamEffects: seamEffects);

        output.ShouldContain("{📥 llblgen:fetch ×42}");
        output.ShouldContain("{🔍 llblgen:read ×31}");
        output.ShouldNotContain("db:write Foo");
    }

    [Test]
    public void Opaque_type_renders_as_a_leaf_keeping_its_own_effects_but_dropping_its_subtree()
    {
        var root = Node("M:Ns.Caller.Go()", Node("M:Ns.LinqMetaData.Build()", Node("M:Ns.Internals.Churn()")));
        var rules = new FactRenderRules([], [new FactRenderRule("LinqMetaData", "ORM")]);
        var effects = Effects(("M:Ns.LinqMetaData.Build()", "🔍 db:read Q"), ("M:Ns.Internals.Churn()", "💾 db:write Hidden"));

        var output = Render(root, rules, effects);

        output.ShouldContain("LinqMetaData.Build");
        output.ShouldContain("«opaque: ORM»");
        output.ShouldContain("{🔍 db:read Q}");
        output.ShouldNotContain("Churn");
        output.ShouldNotContain("Hidden");
    }

    [Test]
    public void Full_mode_renders_effects_as_provenance_leaf_nodes_not_inline_tags()
    {
        var root = Node("M:Ns.Repo.SubmitEvent()", Node("M:Ns.Repo.WithConnection()"));
        var effects = Effects(
            ("M:Ns.Repo.SubmitEvent()", "• dapper:execute Dapper.SqlMapper"),
            ("M:Ns.Repo.WithConnection()", "• db_connection:open SqlConnection")
        );
        var leaves = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["M:Ns.Repo.SubmitEvent()"] = ["• dapper:execute Dapper.SqlMapper  Repo.cs:15"],
            ["M:Ns.Repo.WithConnection()"] = ["• db_connection:open SqlConnection  Repo.cs:44"],
        };
        var output = new StringWriter();
        TreeRenderer.RenderTreeNode(
            root,
            prefix: "",
            isLast: true,
            isRoot: true,
            effects,
            prune: false,
            FactRenderRules.Empty,
            new Dictionary<string, List<string>>(StringComparer.Ordinal),
            output,
            full: true,
            effectLeavesByMethod: leaves
        );
        var text = output.ToString();

        // Effects render as their own leaf nodes carrying the call site, not as the inline {…} tag.
        text.ShouldContain("dapper:execute Dapper.SqlMapper  Repo.cs:15");
        text.ShouldContain("db_connection:open SqlConnection  Repo.cs:44");
        text.ShouldNotContain("{• dapper:execute"); // inline tag suppressed in --full
        // The effect leaf sits ABOVE the call child (effects first, then callees).
        text.IndexOf("dapper:execute").ShouldBeLessThan(text.IndexOf("WithConnection"));
        // The db_connection effect nests under WithConnection, not the root.
        text.IndexOf("WithConnection").ShouldBeLessThan(text.IndexOf("db_connection:open"));
    }

    [Test]
    public void Default_mode_keeps_the_inline_effect_tag()
    {
        var root = Node("M:Ns.Repo.SubmitEvent()");
        var effects = Effects(("M:Ns.Repo.SubmitEvent()", "• dapper:execute Dapper.SqlMapper"));

        var output = Render(root, FactRenderRules.Empty, effects); // full defaults to false

        output.ShouldContain("{• dapper:execute Dapper.SqlMapper}");
    }

    [Test]
    public void Raw_mode_equivalent_empty_rules_expands_everything()
    {
        var hub = Node("M:Ns.IService.Startup()", Dispatch("M:Ns.A.Startup()", 2), Dispatch("M:Ns.B.Startup()", 2));

        var output = Render(hub, FactRenderRules.Empty, Effects());

        output.ShouldContain("A.Startup");
        output.ShouldContain("B.Startup");
        output.ShouldNotContain("collapsed");
    }
}

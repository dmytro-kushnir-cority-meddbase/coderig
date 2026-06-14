using Rig.Cli;
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
            (map.TryGetValue(sym, out var l) ? l : map[sym] = new List<string>()).Add(effect);
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
        CliApplication.RenderTreeNode(root, prefix: "", isLast: true, isRoot: true, effects, prune, rules, seamEffects, output);
        return output.ToString();
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
        CliApplication.RenderTreeNode(
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

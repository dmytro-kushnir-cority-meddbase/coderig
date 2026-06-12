using Rig.Cli;
using Rig.Domain.Data;
using Shouldly;

namespace Rig.Tests.Cli;

// Unit tests for the codebase-specific `rig tree` render rules (collapse-seams / opaque-types).
// These are PRESENTATION rules — they only change what the tree draws, never the reach. Driven over
// hand-built TraceNode trees + a StringWriter, so no Roslyn / SQLite. The matcher itself
// (FactRenderRules.Match*) is pure substring logic; the renderer (CliApplication.RenderTreeNode)
// applies it.
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

    [Fact]
    public void Empty_render_rules_match_nothing()
    {
        FactRenderRules.Empty.MatchCollapseSeam("M:Ns.IService.Startup()").ShouldBeNull();
        FactRenderRules.Empty.MatchOpaque("M:Ns.Whatever.Do()").ShouldBeNull();
        FactRenderRules.Empty.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Type_pattern_matches_declaring_type_not_a_parameter_type_in_the_signature()
    {
        // An app method that merely TAKES an Echo.ProcessId parameter must not be marked opaque by an
        // "Echo." namespace rule — only methods DECLARED in Echo.* should match.
        var rules = new FactRenderRules([], [new FactRenderRule("Echo.", "actor framework")]);
        rules.MatchOpaque("M:Echo.ActorContext.System(Echo.ProcessId)")!.Label.ShouldBe("actor framework"); // declared in Echo.*
        rules.MatchOpaque("M:App.Data.ImportSubscribeMsg.#ctor(App.ImportId,Echo.ProcessId)").ShouldBeNull(); // Echo only in params
    }

    [Fact]
    public void Collapse_seam_matches_the_hub_by_case_insensitive_substring()
    {
        var rules = new FactRenderRules([new FactRenderRule("IService.Startup", "service-locator")], []);
        rules.MatchCollapseSeam("M:Ns.IService.Startup()")!.Label.ShouldBe("service-locator");
        rules.MatchCollapseSeam("M:NS.iservice.startup()")!.Label.ShouldBe("service-locator"); // case-insensitive
        rules.MatchCollapseSeam("M:Ns.AppStartupProcesses.Startup()").ShouldBeNull(); // an impl, not the hub
    }

    [Fact]
    public void Collapse_folds_a_fanout_hubs_children_into_one_summary_line_with_effect_union()
    {
        // IService.Startup is a hub with 3 impl children; two carry effects, one nests a deeper effect.
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
            ("M:Ns.B.Startup()", "💾 db:write Foo") // duplicate glyph — must de-dupe
        );

        var output = Render(hub, rules, effects);

        output.ShouldContain("IService.Startup"); // the hub still prints
        output.ShouldContain("3 dispatch targets collapsed");
        output.ShouldContain("[seam: service-locator]");
        output.ShouldContain("{💾 db:write Foo}");
        output.ShouldContain("{🔍 db:read Bar}"); // rolled up from the nested grandchild
        output.IndexOf("💾 db:write Foo").ShouldBe(output.LastIndexOf("💾 db:write Foo")); // de-duped (appears once)
        output.ShouldNotContain("A.Startup"); // the impl subtrees are folded away
        output.ShouldNotContain("DoDeep");
        output.ShouldContain("4 lines hidden"); // 3 impls + 1 grandchild
    }

    [Fact]
    public void Collapse_prefers_the_precomputed_realistic_summary_over_the_truncated_subtree()
    {
        // The rendered subtree is shallow (one ↺seen-style impl with no children), so a subtree walk
        // would under-report. The precomputed seam-effect map (the reach-closure aggregate) must win.
        var hub = Node("M:Ns.IService.Startup()", Dispatch("M:Ns.A.Startup()", 3));
        var rules = new FactRenderRules([new FactRenderRule("IService.Startup", "service-locator")], []);
        var effects = Effects(("M:Ns.A.Startup()", "💾 db:write Foo"));
        var seamEffects = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["M:Ns.IService.Startup()"] = ["📥 llblgen:fetch ×42", "🔍 llblgen:read ×31"],
        };

        var output = Render(hub, rules, effects, seamEffects: seamEffects);

        output.ShouldContain("{📥 llblgen:fetch ×42}"); // the aggregated reach-closure tally
        output.ShouldContain("{🔍 llblgen:read ×31}");
        output.ShouldNotContain("db:write Foo"); // the shallow subtree's effect did NOT win
    }

    [Fact]
    public void Opaque_type_renders_as_a_leaf_keeping_its_own_effects_but_dropping_its_subtree()
    {
        var root = Node("M:Ns.Caller.Go()", Node("M:Ns.LinqMetaData.Build()", Node("M:Ns.Internals.Churn()")));
        var rules = new FactRenderRules([], [new FactRenderRule("LinqMetaData", "ORM")]);
        var effects = Effects(("M:Ns.LinqMetaData.Build()", "🔍 db:read Q"), ("M:Ns.Internals.Churn()", "💾 db:write Hidden"));

        var output = Render(root, rules, effects);

        output.ShouldContain("LinqMetaData.Build");
        output.ShouldContain("«opaque: ORM»");
        output.ShouldContain("{🔍 db:read Q}"); // the opaque node's OWN effect still shows
        output.ShouldNotContain("Churn"); // subtree suppressed
        output.ShouldNotContain("Hidden");
    }

    [Fact]
    public void Raw_mode_equivalent_empty_rules_expands_everything()
    {
        var hub = Node("M:Ns.IService.Startup()", Dispatch("M:Ns.A.Startup()", 2), Dispatch("M:Ns.B.Startup()", 2));

        var output = Render(hub, FactRenderRules.Empty, Effects());

        output.ShouldContain("A.Startup");
        output.ShouldContain("B.Startup");
        output.ShouldNotContain("collapsed");
    }
}

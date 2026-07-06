using Rig.Cli.Rendering;
using Rig.Cli.Services;
using Rig.Cli.Web;
using Rig.Domain.Data;
using Shouldly;

namespace Rig.Tests.Cli;

// Guards the fix that makes the NON-pretty tree consumers (the llm/llm-ids TSV renderer and the web DTO mapper)
// honour the opaque/collapse render rules — the pretty TreeRenderer always did, but these two walked every
// child regardless, so a model asking for --format llm (or the SPA) got the full unfolded tree. Nothing tested
// this before, which is how it drifted.
public sealed class TreeFoldTests
{
    private static TraceNode Node(string id, params TraceNode[] children) => new(id, "invocation", null, null, children);

    private static Dictionary<string, List<string>> RawEffects(params (string Sym, string[] Effects)[] entries)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (sym, efx) in entries)
        {
            d[sym] = [.. efx];
        }

        return d;
    }

    private static FactRenderRules Rules(FactRenderRule[]? collapse = null, FactRenderRule[]? opaque = null) =>
        new(CollapseSeams: collapse ?? [], OpaqueTypes: opaque ?? []);

    private static List<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

    private static string RenderLlm(
        IReadOnlyList<TraceNode> roots,
        FactRenderRules rules,
        IReadOnlyDictionary<string, List<string>> rawEffects
    )
    {
        var sw = new StringWriter();
        LlmSummaryRenderer.Render(
            roots: roots,
            rawEffectsByMethod: rawEffects,
            projection: LlmSummaryRenderer.LlmProjection.Full,
            output: sw,
            suppress: LlmSummaryRenderer.SuppressSet.None,
            renderRules: rules
        );
        return sw.ToString();
    }

    // ── llm format ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Llm_opaque_rule_folds_the_subtree_and_flags_the_node()
    {
        var root = Node("M:App.Svc.Do()", Node("M:Vendor.Framework.Internal()", Node("M:Vendor.Framework.Deep()")));
        var rules = Rules(opaque: [new FactRenderRule("M:Vendor.Framework.", "vendor framework")]);

        var lines = Lines(RenderLlm([root], rules, RawEffects()));

        // The opaque node emits a row carrying the fold flag...
        lines.ShouldContain(l => l.Contains("Framework.Internal") && l.Contains("opaque:vendor framework"));
        // ...but its subtree is folded away (the deep child never appears).
        lines.ShouldNotContain(l => l.Contains("Framework.Deep"));
    }

    [Test]
    public void Llm_collapse_rule_folds_subtree_and_rolls_up_the_hidden_effect_union()
    {
        var root = Node("M:App.Svc.Do()", Node("M:App.Pricing.GetData()", Node("M:App.Pricing.Step1()"), Node("M:App.Pricing.Step2()")));
        var rules = Rules(collapse: [new FactRenderRule("Pricing.GetData", "pricing engine")]);
        var effects = RawEffects(
            ("M:App.Pricing.Step1()", ["llblgen:fetch", "llblgen:fetch"]),
            ("M:App.Pricing.Step2()", ["llblgen:fetch", "cache:read"])
        );

        var lines = Lines(RenderLlm([root], rules, effects));

        // The seam node is a folded leaf flagged collapsed:<label>, carrying the UNION of the hidden effects
        // as an aggregated count (3 fetches across the two hidden steps) — parity with the pretty seam summary.
        var seamRow = lines.Single(l => l.Contains("Pricing.GetData"));
        seamRow.ShouldContain("collapsed:pricing engine");
        seamRow.ShouldContain("llblgen:fetch*3");
        seamRow.ShouldContain("cache:read");
        // The hidden steps are not emitted as their own rows.
        lines.ShouldNotContain(l => l.Contains("Pricing.Step1"));
        lines.ShouldNotContain(l => l.Contains("Pricing.Step2"));
    }

    [Test]
    public void Llm_empty_rules_the_raw_opt_out_do_not_fold()
    {
        var root = Node("M:App.Svc.Do()", Node("M:Vendor.Framework.Internal()", Node("M:Vendor.Framework.Deep()")));

        var lines = Lines(RenderLlm([root], FactRenderRules.Empty, RawEffects()));

        // Under Empty rules (the --raw / ?raw=true opt-out) the full subtree is present, no fold flags.
        lines.ShouldContain(l => l.Contains("Framework.Deep"));
        lines.ShouldNotContain(l => l.Contains("opaque:") || l.Contains("collapsed:"));
    }

    // ── web DTO mapper ───────────────────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, TreeQueryService.SymbolLocation> NoLocations() =>
        new Dictionary<string, TreeQueryService.SymbolLocation>(StringComparer.Ordinal);

    private static DerivedEffect Eff(string sym, string provider, string op) =>
        new(Provider: provider, Operation: op, ResourceType: "R", EnclosingSymbolId: sym, FilePath: "f.cs", Line: 1);

    [Test]
    public void Web_mapper_folds_collapse_seam_into_a_labelled_leaf_with_the_effect_union()
    {
        var root = Node("M:App.Svc.Do()", Node("M:App.Pricing.GetData()", Node("M:App.Pricing.Step1()"), Node("M:App.Pricing.Step2()")));
        var rules = Rules(collapse: [new FactRenderRule("Pricing.GetData", "pricing engine")]);
        var effects = new List<DerivedEffect>
        {
            Eff("M:App.Pricing.Step1()", "llblgen", "fetch"),
            Eff("M:App.Pricing.Step2()", "llblgen", "fetch"),
        };

        var resp = TreeMapper.ToResponse("App.Svc.Do", [root], effects, NoLocations(), new Dictionary<string, string>(), rules);

        var seam = resp.Roots[0].Children[0];
        seam.FoldKind.ShouldBe("collapse");
        seam.FoldLabel.ShouldBe("pricing engine");
        seam.FoldHidden.ShouldBe(2); // Step1 + Step2 folded away
        seam.Children.ShouldBeEmpty(); // subtree hidden
        // The union of the hidden effects rides on the seam node so the SPA can show it without the subtree.
        seam.Effects.ShouldContain(e => e.Provider == "llblgen" && e.Operation == "fetch" && e.Sites == 2);
    }

    [Test]
    public void Web_mapper_empty_rules_the_raw_opt_out_keep_the_full_subtree()
    {
        var root = Node("M:App.Svc.Do()", Node("M:App.Pricing.GetData()", Node("M:App.Pricing.Step1()")));

        var resp = TreeMapper.ToResponse(
            "App.Svc.Do",
            [root],
            new List<DerivedEffect>(),
            NoLocations(),
            new Dictionary<string, string>(),
            FactRenderRules.Empty
        );

        // No fold: the seam node keeps its child, and no node is annotated as folded.
        var getData = resp.Roots[0].Children[0];
        getData.FoldKind.ShouldBeNull();
        getData.Children.ShouldNotBeEmpty();
    }
}

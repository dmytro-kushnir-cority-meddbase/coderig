using Rig.Cli;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Shouldly;

namespace Rig.Tests.Cli;

// Unit tests for LlmSummaryRenderer: header, name shortening, arity, effect aggregation, seen flag,
// lambda suppression, ctor suppression, determinism. Uses synthetic TraceNode forests — no DB, no graph.
public sealed class LlmSummaryRendererTests
{
    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static TraceNode Node(string id, int callSites = 1, params TraceNode[] children) =>
        new(id, "invocation", null, null, children, CallSites: callSites);

    private static TraceNode Truncated(string id) => new(id, "invocation", null, null, [], Truncated: true);

    private static Dictionary<string, List<string>> RawEffects(params (string Sym, string[] Effects)[] entries)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (sym, efx) in entries)
        {
            d[sym] = [.. efx];
        }

        return d;
    }

    private static string Render(
        IReadOnlyList<TraceNode> roots,
        IReadOnlyDictionary<string, List<string>>? rawEffects = null,
        LlmSummaryRenderer.LlmProjection projection = LlmSummaryRenderer.LlmProjection.Full,
        LlmSummaryRenderer.SuppressSet suppress = LlmSummaryRenderer.SuppressSet.None
    )
    {
        var sw = new StringWriter();
        LlmSummaryRenderer.Render(
            roots: roots,
            rawEffectsByMethod: rawEffects ?? new Dictionary<string, List<string>>(StringComparer.Ordinal),
            projection: projection,
            output: sw,
            suppress: suppress
        );
        return sw.ToString();
    }

    private static List<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

    // ── header ───────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Output_starts_with_the_header_row_reconstructable_projections_have_6_columns()
    {
        // EffectfulPaths and Full: no parent column — depth+order encodes the linkage.
        var outputFull = Render([Node("M:App.Svc.Do()")], projection: LlmSummaryRenderer.LlmProjection.Full);
        var outputEfp = Render([Node("M:App.Svc.Do()")], projection: LlmSummaryRenderer.LlmProjection.EffectfulPaths);

        Lines(outputFull)[0].ShouldBe("depth\tname\tarity\tcalls\teffects\tflags");
        Lines(outputEfp)[0].ShouldBe("depth\tname\tarity\tcalls\teffects\tflags");
    }

    [Test]
    public void Output_starts_with_the_header_row_effects_flat_has_7_columns_with_parent()
    {
        // EffectsFlat: includes parent-name column (parent row may be absent in the gappy flat view).
        var rawEffects = RawEffects(("M:App.Svc.Do()", ["io:read"]));
        var output = Render([Node("M:App.Svc.Do()")], rawEffects, LlmSummaryRenderer.LlmProjection.EffectsFlat);

        Lines(output)[0].ShouldBe("depth\tparent\tname\tarity\tcalls\teffects\tflags");
    }

    // ── name shortening: no namespaces, no parameter types ───────────────────────────────────────

    [Test]
    public void Name_strips_namespace_leaving_TypeName_MethodName()
    {
        // Full projection: 6-column header (depth name arity calls effects flags) — name is at index 1.
        var output = Render([Node("M:My.Namespace.Service.DoSomething(System.String,System.Int32)")]);

        var data = Lines(output)[1];
        var cols = data.Split('\t');
        cols[1].ShouldBe("Service.DoSomething");
    }

    [Test]
    public void Name_has_no_parameter_types_in_the_name_column()
    {
        // Full projection: name at index 1.
        var output = Render([Node("M:App.Repo.Save(App.Entity,System.Int32)")]);

        var data = Lines(output)[1];
        var cols = data.Split('\t');
        cols[1].ShouldNotContain("App.Entity");
        cols[1].ShouldNotContain("System.Int32");
        cols[1].ShouldBe("Repo.Save");
    }

    // ── arity markers stripped from name ─────────────────────────────────────────────────────────

    [Test]
    public void Name_strips_method_arity_marker()
    {
        // Roslyn DocID: method-level arity marker ``N on the method segment.
        // "M:App.Loader.Concat``1(System.String)" -> name should be "Loader.Concat" (not "Loader.Concat``1")
        var output = Render([Node("M:App.Loader.Concat``1(System.String)")]);
        var cols = Lines(output)[1].Split('\t');
        cols[1].ShouldBe("Loader.Concat");
    }

    [Test]
    public void Name_strips_type_arity_marker()
    {
        // "M:App.Foo`2.Bar()" -> name should be "Foo.Bar" (type arity marker `2 stripped)
        var output = Render([Node("M:App.Foo`2.Bar()")]);
        var cols = Lines(output)[1].Split('\t');
        cols[1].ShouldBe("Foo.Bar");
    }

    [Test]
    public void Name_strips_both_type_and_method_arity_markers()
    {
        // "M:App.Construct`2.New``1()" -> name should be "Construct.New"
        var output = Render([Node("M:App.Construct`2.New``1()")]);
        var cols = Lines(output)[1].Split('\t');
        cols[1].ShouldBe("Construct.New");
    }

    // ── arity ────────────────────────────────────────────────────────────────────────────────────
    // Full projection: 6-column header (depth name arity calls effects flags) — arity at index 2.

    [Test]
    public void Arity_is_parameter_count_not_type_names()
    {
        // 3-param method
        var output = Render([Node("M:App.Svc.Create(System.String,System.Int32,App.Context)")]);

        var cols = Lines(output)[1].Split('\t');
        cols[2].ShouldBe("3"); // arity column
    }

    [Test]
    public void Arity_is_zero_for_no_arg_method()
    {
        var output = Render([Node("M:App.Svc.Init()")]);

        var cols = Lines(output)[1].Split('\t');
        cols[2].ShouldBe("0");
    }

    [Test]
    public void Arity_is_one_for_single_param()
    {
        var output = Render([Node("M:App.Svc.Load(System.Int32)")]);

        var cols = Lines(output)[1].Split('\t');
        cols[2].ShouldBe("1");
    }

    // ── calls ────────────────────────────────────────────────────────────────────────────────────
    // Full projection: 6-column header (depth name arity calls effects flags) — calls at index 3.

    [Test]
    public void Calls_column_reflects_CallSites()
    {
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Save()", callSites: 3));

        var lines = Lines(Render([root]));
        // line[1] = root, line[2] = child
        var childCols = lines[2].Split('\t');
        childCols[3].ShouldBe("3"); // calls column
    }

    // ── effects aggregation — ASCII, whitespace-free ─────────────────────────────────────────────
    // Full projection: 6-column header (depth name arity calls effects flags) — effects at index 4.
    // Count suffix is "*N" (ASCII), distinct effects joined by "," (no space).

    [Test]
    public void Single_effect_occurrence_has_no_count_suffix()
    {
        var root = Node("M:App.Svc.Do()");
        var rawEffects = RawEffects(("M:App.Svc.Do()", ["io:read"]));

        var cols = Lines(Render([root], rawEffects))[1].Split('\t');
        cols[4].ShouldBe("io:read");
    }

    [Test]
    public void Multiple_occurrences_of_same_effect_are_aggregated_with_ascii_count()
    {
        var root = Node("M:App.Svc.Do()");
        var rawEffects = RawEffects(("M:App.Svc.Do()", ["io:read", "io:read", "io:read"]));

        var cols = Lines(Render([root], rawEffects))[1].Split('\t');
        // ASCII "*N", no space before count, no × character
        cols[4].ShouldBe("io:read*3");
        cols[4].ShouldNotContain("×");
        cols[4].ShouldNotContain(" ");
    }

    [Test]
    public void Multiple_distinct_effects_are_comma_joined_no_space()
    {
        var root = Node("M:App.Svc.Do()");
        var rawEffects = RawEffects(("M:App.Svc.Do()", ["io:read", "efcore:read", "io:read", "efcore:read", "io:read"]));

        var cols = Lines(Render([root], rawEffects))[1].Split('\t');
        // first-seen order: io:read then efcore:read; comma without space
        cols[4].ShouldBe("io:read*3,efcore:read*2");
        cols[4].ShouldNotContain(" ");
        cols[4].ShouldNotContain("×");
    }

    [Test]
    public void Effects_column_is_empty_when_node_has_no_effects()
    {
        var root = Node("M:App.Svc.Do()");

        var cols = Lines(Render([root]))[1].Split('\t');
        cols[4].ShouldBe("");
    }

    // ── fixed column count ────────────────────────────────────────────────────────────────────────
    // Every row in a given projection has exactly that projection's column count:
    //   paths/full = 6: depth name arity calls effects flags
    //   effects-flat = 7: depth parent name arity calls effects flags
    // Empty trailing fields are still present (tab-separated), so the field count is always fixed.
    // The "no trailing tab" guarantee means: no EXTRA columns beyond the defined projection column
    // count. When the last field (flags) is empty, the row ends with the tab before it (standard
    // TSV: tab is a separator, so N fields have N-1 separators, and the Nth field can be empty).

    [Test]
    public void Full_projection_rows_have_exactly_6_columns()
    {
        // Node with no effects and no flags — every row in full projection must have 6 fields.
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Load()"));
        var lines = Lines(Render([root]));
        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split('\t');
            cols.Length.ShouldBe(6, $"Expected 6 columns for Full; got {cols.Length} on: {line}");
        }
    }

    [Test]
    public void EffectsFlat_rows_have_exactly_7_columns()
    {
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Load()"));
        var rawEffects = RawEffects(("M:App.Repo.Load()", ["io:read"]));
        var lines = Lines(Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.EffectsFlat));
        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split('\t');
            cols.Length.ShouldBe(7, $"Expected 7 columns for EffectsFlat; got {cols.Length} on: {line}");
        }
    }

    [Test]
    public void Row_with_empty_effects_and_empty_flags_has_correct_field_count()
    {
        // A node with NO effects and NO flags still has 6 columns; the last two are empty strings.
        // The row may end with tabs (the two empty trailing fields are still present).
        var root = Node("M:App.Svc.DoEmpty()");
        var lines = Lines(Render([root]));
        var dataLine = lines[1];
        var cols = dataLine.Split('\t');
        cols.Length.ShouldBe(6, $"Expected 6 columns; got {cols.Length} on: {dataLine}");
        // effects (index 4) and flags (index 5) are both empty
        cols[4].ShouldBe("");
        cols[5].ShouldBe("");
    }

    [Test]
    public void Row_with_flags_does_not_have_extra_column_after_flags()
    {
        // A row with flags set must not have a 7th column (for Full projection) — no trailing tab
        // beyond the defined projection column count.
        var root = Node("M:App.Svc.Do()", callSites: 1, Truncated("M:App.Repo.Load()"));
        var lines = Lines(Render([root]));
        // The truncated child gets flags="seen"; its row must have exactly 6 columns.
        var truncatedLine = lines[2]; // depth 1
        var cols = truncatedLine.Split('\t');
        cols.Length.ShouldBe(6, $"Expected 6 columns for truncated row; got {cols.Length} on: {truncatedLine}");
        cols[5].ShouldBe("seen");
    }

    // ── "seen" flag (Truncated = elided/seen; was "x-phase") ────────────────────────────────────
    // NOTE: TraceNode.Truncated conflates "already expanded elsewhere" and "depth/budget cap" —
    // both emit flags="seen". The causes are not distinguishable from the current model.

    [Test]
    public void Seen_node_is_emitted_with_seen_flag()
    {
        // An already-expanded node appears again as Truncated — the elided marker.
        var root = Node("M:App.Svc.Do()", callSites: 1, Truncated("M:App.Repo.Load()"));
        var rawEffects = RawEffects(("M:App.Repo.Load()", ["efcore:read", "efcore:read"]));

        var lines = Lines(Render([root], rawEffects));
        // line[2] = the truncated child
        var seenLine = lines[2];
        seenLine.ShouldContain("seen");
        seenLine.ShouldNotContain("x-phase");
        // Must include its effects even though Truncated; ASCII format
        seenLine.ShouldContain("efcore:read*2");
    }

    [Test]
    public void Seen_node_is_NOT_suppressed()
    {
        var root = Node("M:App.Svc.Do()", callSites: 1, Truncated("M:App.Repo.Load()"));

        var lines = Lines(Render([root]));
        // header + root + seen child = 3 lines
        lines.Count.ShouldBe(3);
        lines[2].ShouldContain("Repo.Load");
    }

    // ── lambda suppression ───────────────────────────────────────────────────────────────────────

    [Test]
    public void Lambda_node_is_suppressed_when_suppress_includes_lambdas()
    {
        var lambdaId = "M:App.Svc.Do()~λ0";
        var root = Node("M:App.Caller.Go()", callSites: 1, Node(lambdaId));

        var lines = Lines(Render([root], suppress: LlmSummaryRenderer.SuppressSet.Lambdas));
        // header + Caller.Go only — lambda is gone
        lines.Count.ShouldBe(2);
        lines.ShouldNotContain(l => l.Contains("~λ") || l.Contains("λ0"));
    }

    [Test]
    public void Lambda_node_is_NOT_suppressed_when_suppress_is_none()
    {
        var lambdaId = "M:App.Svc.Do()~λ0";
        var root = Node("M:App.Caller.Go()", callSites: 1, Node(lambdaId));

        var lines = Lines(Render([root], suppress: LlmSummaryRenderer.SuppressSet.None));
        // header + root + lambda row
        lines.Count.ShouldBe(3);
    }

    [Test]
    public void Lambda_children_are_still_walked_after_suppression()
    {
        // Lambda wrapping a named method — the named method should still surface.
        var lambdaId = "M:App.Svc.Do()~λ0";
        var namedChild = Node("M:App.Repo.Save()");
        var lambdaNode = Node(lambdaId, callSites: 1, namedChild);
        var root = Node("M:App.Caller.Go()", callSites: 1, lambdaNode);

        var lines = Lines(Render([root], suppress: LlmSummaryRenderer.SuppressSet.Lambdas));
        // header + Caller.Go + Repo.Save (lambda skipped but its child emitted at same depth)
        lines.Count.ShouldBe(3);
        lines.ShouldContain(l => l.Contains("Repo.Save"));
    }

    // ── ctor suppression ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void Ctor_node_is_suppressed_by_default()
    {
        // .#ctor rows are omitted when SuppressSet includes Ctors (the default).
        var ctor = Node("M:App.Repo.Repository.#ctor(App.Db.DbContext)");
        var root = Node("M:App.Svc.Do()", callSites: 1, ctor);

        // Default suppress = Default (includes Ctors)
        var lines = Lines(Render([root], suppress: LlmSummaryRenderer.SuppressSet.Default));
        // header + Svc.Do only — ctor is gone
        lines.Count.ShouldBe(2);
        lines.ShouldNotContain(l => l.Contains("#ctor") || l.Contains(".ctor"));
    }

    [Test]
    public void Ctor_node_is_NOT_suppressed_when_suppress_is_none()
    {
        var ctor = Node("M:App.Repo.Repository.#ctor(App.Db.DbContext)");
        var root = Node("M:App.Svc.Do()", callSites: 1, ctor);

        var lines = Lines(Render([root], suppress: LlmSummaryRenderer.SuppressSet.None));
        // header + root + ctor row
        lines.Count.ShouldBe(3);
    }

    [Test]
    public void Ctor_effects_are_rolled_up_to_nearest_non_suppressed_ancestor()
    {
        // Ctor that has direct effects: those effects must surface on the calling method's row.
        var ctorId = "M:App.Repo.Repository.#ctor(App.Db.DbContext)";
        var ctor = Node(ctorId, callSites: 1);
        var root = Node("M:App.Svc.Do()", callSites: 1, ctor);
        var rawEffects = RawEffects(("M:App.Svc.Do()", ["efcore:read"]), (ctorId, ["efcore:commit"]));

        var lines = Lines(Render([root], rawEffects, suppress: LlmSummaryRenderer.SuppressSet.Default));
        // Ctor row suppressed; its efcore:commit should appear on Svc.Do's row.
        lines.Count.ShouldBe(2); // header + Svc.Do only
        var dataCols = lines[1].Split('\t');
        // effects at index 4: should contain both efcore:read (own) and efcore:commit (rolled up from ctor)
        dataCols[4].ShouldContain("efcore:read");
        dataCols[4].ShouldContain("efcore:commit");
    }

    [Test]
    public void Ctor_children_are_still_walked_after_suppression()
    {
        // A ctor wrapping a named method — the named method should still surface.
        var ctorId = "M:App.Repo.Repository.#ctor(App.Db.DbContext)";
        var namedChild = Node("M:App.Cache.Cache.Warm()");
        var ctor = Node(ctorId, callSites: 1, namedChild);
        var root = Node("M:App.Svc.Do()", callSites: 1, ctor);

        var lines = Lines(Render([root], suppress: LlmSummaryRenderer.SuppressSet.Ctors));
        // header + Svc.Do + Cache.Warm (ctor skipped but its child emitted at same depth as ctor)
        lines.Count.ShouldBe(3);
        lines.ShouldContain(l => l.Contains("Cache.Warm"));
    }

    [Test]
    public void Static_ctor_cctor_is_also_suppressed_by_default()
    {
        var cctor = Node("M:App.Config.AppConfig.#cctor()");
        var root = Node("M:App.Svc.Do()", callSites: 1, cctor);

        var lines = Lines(Render([root], suppress: LlmSummaryRenderer.SuppressSet.Default));
        lines.Count.ShouldBe(2); // header + Svc.Do only
    }

    // ── EffectsFlat projection mirrors --effects node selection ─────────────────────────────────

    [Test]
    public void EffectsFlat_emits_only_effect_bearing_nodes()
    {
        var root = Node("M:App.Svc.RunAsync()", callSites: 1, Node("M:App.Repo.Load()"), Node("M:App.Repo.Save()"));
        var rawEffects = RawEffects(("M:App.Repo.Save()", ["efcore:commit"]));

        var lines = Lines(Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.EffectsFlat));
        // header + Save only (root and Load have no effects)
        lines.Count.ShouldBe(2);
        // Check the name column (index 2) for Repo.Save, and that no row has Svc.RunAsync or Repo.Load as the name.
        var dataCols = lines[1].Split('\t');
        dataCols[2].ShouldBe("Repo.Save");
        lines.Skip(1).ShouldNotContain(l => l.Split('\t')[2] == "Svc.RunAsync");
        lines.Skip(1).ShouldNotContain(l => l.Split('\t')[2] == "Repo.Load");
    }

    [Test]
    public void EffectsFlat_consistency_ascii_effects_and_correct_column_count()
    {
        // EffectsFlat must have the same ASCII effects format as Full, and 7 columns per row.
        var root = Node("M:App.Svc.RunAsync()", callSites: 1, Node("M:App.Repo.Load()"));
        var rawEffects = RawEffects(("M:App.Repo.Load()", ["efcore:read", "efcore:read"]));

        var lines = Lines(Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.EffectsFlat));
        lines.Count.ShouldBe(2); // header + Load
        var dataCols = lines[1].Split('\t');
        dataCols.Length.ShouldBe(7);
        // effects column (index 5 in EffectsFlat) must be ASCII, whitespace-free
        dataCols[5].ShouldBe("efcore:read*2");
        dataCols[5].ShouldNotContain("×");
        dataCols[5].ShouldNotContain(" ");
    }

    [Test]
    public void Full_projection_emits_all_reachable_nodes()
    {
        var root = Node("M:App.Svc.RunAsync()", callSites: 1, Node("M:App.Repo.Load()"), Node("M:App.Repo.Save()"));
        var rawEffects = RawEffects(("M:App.Repo.Save()", ["efcore:commit"]));

        var lines = Lines(Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.Full));
        // header + root + Load + Save = 4
        lines.Count.ShouldBe(4);
    }

    // ── EffectfulPaths projection — spine-keeping default ────────────────────────────────────────

    [Test]
    public void EffectfulPaths_keeps_spine_to_effectful_nodes()
    {
        // Tree:  Root -> A (no effect) -> B (has effect)
        //             -> C (no effect, no descendants with effects)
        var b = Node("M:App.Svc.B()");
        var a = Node("M:App.Svc.A()", callSites: 1, b);
        var c = Node("M:App.Svc.C()");
        var root = Node("M:App.Svc.Root()", callSites: 1, a, c);
        var rawEffects = RawEffects(("M:App.Svc.B()", ["io:read"]));

        var lines = Lines(Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.EffectfulPaths));
        // Should include: Root (spine), A (spine), B (effect-bearing); NOT C (effectless subtree).
        // EffectfulPaths: 6-column header (no parent) — name is at index 1.
        var names = lines.Skip(1).Select(l => l.Split('\t')[1]).ToList();
        names.ShouldContain("Svc.Root");
        names.ShouldContain("Svc.A");
        names.ShouldContain("Svc.B");
        names.ShouldNotContain("Svc.C");
    }

    [Test]
    public void EffectfulPaths_prunes_entire_subtree_with_no_effects()
    {
        // A root with two branches: one with an effect, one completely effectless.
        var effectful = Node("M:App.Repo.Save()");
        var branchA = Node("M:App.Svc.Branch1()", callSites: 1, effectful);
        var branchB = Node("M:App.Svc.Branch2()", callSites: 1, Node("M:App.Svc.Inner()"));
        var root = Node("M:App.Svc.Root()", callSites: 1, branchA, branchB);
        var rawEffects = RawEffects(("M:App.Repo.Save()", ["efcore:commit"]));

        var lines = Lines(Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.EffectfulPaths));
        // EffectfulPaths: 6-column header (no parent) — name is at index 1.
        var names = lines.Skip(1).Select(l => l.Split('\t')[1]).ToList();
        names.ShouldContain("Svc.Root");
        names.ShouldContain("Svc.Branch1");
        names.ShouldContain("Repo.Save");
        names.ShouldNotContain("Svc.Branch2");
        names.ShouldNotContain("Svc.Inner");
    }

    [Test]
    public void EffectfulPaths_output_is_reconstructable_via_depth_and_order()
    {
        // Build a tree with a multi-level spine: Root -> Svc -> Repo (effect)
        //                                               -> Util (no effect, pruned)
        // In the no-parent 6-column format, reconstructability is: every non-root row has a
        // preceding row at depth-1 (that is the derivable parent).
        var repo = Node("M:App.Data.Repo.Fetch()");
        var svc = Node("M:App.Services.Svc.Process()", callSites: 1, repo, Node("M:App.Utils.Util.NoOp()"));
        var root = Node("M:App.Api.Controller.Handle()", callSites: 1, svc);
        var rawEffects = RawEffects(("M:App.Data.Repo.Fetch()", ["efcore:read"]));

        var lines = Lines(Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.EffectfulPaths));
        // EffectfulPaths: 6-column header — name at index 1, no parent column.
        var emittedNames = lines.Skip(1).Select(l => l.Split('\t')[1]).ToList();

        // Every non-root row must have a preceding row at depth-1 (the derivable parent).
        // Track the most-recently-seen row at each depth level.
        var lastAtDepth = new Dictionary<int, string>(capacity: 8);
        foreach (var dataLine in lines.Skip(1))
        {
            var cols = dataLine.Split('\t');
            cols.Length.ShouldBe(6, $"Expected 6 columns (no parent) for EffectfulPaths; got {cols.Length} on: {dataLine}");
            var depth = int.Parse(cols[0], System.Globalization.CultureInfo.InvariantCulture);
            var name = cols[1];
            if (depth > 0)
            {
                lastAtDepth.ShouldContainKey(
                    depth - 1,
                    $"Row '{name}' at depth {depth} has no preceding row at depth {depth - 1} — output is not reconstructable by depth+order."
                );
            }

            lastAtDepth[depth] = name;
        }

        // Sanity: Util (effectless) should be pruned.
        emittedNames.ShouldNotContain("Util.NoOp");
    }

    // ── parent column (EffectsFlat only) ────────────────────────────────────────────────────────
    // Reconstructable projections (Full, EffectfulPaths) have no parent column — depth+order suffice.
    // EffectsFlat keeps the parent-name column because the parent row may be absent in the gappy view.

    [Test]
    public void EffectsFlat_root_has_empty_parent_column()
    {
        // Root nodes have no caller — parent column must be empty.
        var rawEffects = RawEffects(("M:App.Svc.Do()", ["io:read"]));
        var cols = Lines(Render([Node("M:App.Svc.Do()")], rawEffects, LlmSummaryRenderer.LlmProjection.EffectsFlat))[1].Split('\t');
        cols[1].ShouldBe(""); // parent empty for roots
    }

    [Test]
    public void EffectsFlat_child_has_parent_name_column()
    {
        // EffectsFlat: parent-name column is at index 1 (7-column header).
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Load()"));
        var rawEffects = RawEffects(("M:App.Repo.Load()", ["efcore:read"]));

        var lines = Lines(Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.EffectsFlat));
        // header + Load row only (root has no effect)
        var childCols = lines[1].Split('\t');
        childCols[1].ShouldBe("Svc.Do"); // parent = short name of caller
        childCols[2].ShouldBe("Repo.Load"); // name column
    }

    [Test]
    public void Full_projection_has_no_parent_column()
    {
        // Full: 6-column header — depth name arity calls effects flags. Index 1 is the name, not the parent.
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Load()"));

        var lines = Lines(Render([root]));
        // Every row has exactly 6 columns.
        foreach (var line in lines.Skip(1))
        {
            line.Split('\t').Length.ShouldBe(6);
        }

        // Index 1 is the name, not a parent field.
        lines[1].Split('\t')[1].ShouldBe("Svc.Do");
        lines[2].Split('\t')[1].ShouldBe("Repo.Load");
    }

    // ── depth column ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Depth_increments_correctly_for_nested_nodes()
    {
        var root = Node("M:App.A.Go()", callSites: 1, Node("M:App.B.Run()", callSites: 1, Node("M:App.C.Exec()")));

        var lines = Lines(Render([root]));
        // depth 0 = A.Go, depth 1 = B.Run, depth 2 = C.Exec
        lines[1].Split('\t')[0].ShouldBe("0");
        lines[2].Split('\t')[0].ShouldBe("1");
        lines[3].Split('\t')[0].ShouldBe("2");
    }

    // ── determinism ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Output_is_byte_for_byte_identical_across_two_runs()
    {
        var root = Node("M:App.Svc.RunAsync()", callSites: 1, Truncated("M:App.Repo.Load()"), Node("M:App.Repo.Save()"));
        var rawEffects = RawEffects(("M:App.Repo.Load()", ["efcore:read", "efcore:read"]), ("M:App.Repo.Save()", ["efcore:commit"]));

        var first = Render([root], rawEffects);
        var second = Render([root], rawEffects);

        first.ShouldBe(second);
    }

    // ── multiple roots ───────────────────────────────────────────────────────────────────────────

    [Test]
    public void Multiple_roots_each_appear_at_depth_zero()
    {
        var roots = new TraceNode[] { Node("M:App.A.Go()"), Node("M:App.B.Run()") };

        var lines = Lines(Render(roots));
        lines.Count.ShouldBe(3); // header + two roots
        lines[1].Split('\t')[0].ShouldBe("0");
        lines[2].Split('\t')[0].ShouldBe("0");
    }

    // ── IsSuppressed helper ───────────────────────────────────────────────────────────────────────

    [Test]
    public void IsSuppressed_returns_true_for_lambda_ids_when_lambdas_in_suppress_set()
    {
        LlmSummaryRenderer.IsSuppressed("M:App.Svc.Do()~λ0", LlmSummaryRenderer.SuppressSet.Lambdas).ShouldBeTrue();
        LlmSummaryRenderer.IsSuppressed("M:App.Svc.Do()~λ3", LlmSummaryRenderer.SuppressSet.Lambdas).ShouldBeTrue();
    }

    [Test]
    public void IsSuppressed_returns_false_for_lambda_ids_when_suppress_is_none()
    {
        LlmSummaryRenderer.IsSuppressed("M:App.Svc.Do()~λ0", LlmSummaryRenderer.SuppressSet.None).ShouldBeFalse();
    }

    [Test]
    public void IsSuppressed_returns_true_for_ctor_when_ctors_in_suppress_set()
    {
        LlmSummaryRenderer.IsSuppressed("M:App.Repo.Repository.#ctor(System.String)", LlmSummaryRenderer.SuppressSet.Ctors).ShouldBeTrue();
        LlmSummaryRenderer.IsSuppressed("M:App.Config.AppConfig.#cctor()", LlmSummaryRenderer.SuppressSet.Ctors).ShouldBeTrue();
    }

    [Test]
    public void IsSuppressed_returns_false_for_ctor_when_suppress_is_none()
    {
        LlmSummaryRenderer.IsSuppressed("M:App.Repo.Repository.#ctor(System.String)", LlmSummaryRenderer.SuppressSet.None).ShouldBeFalse();
    }

    [Test]
    public void IsSuppressed_returns_false_for_normal_ids()
    {
        LlmSummaryRenderer.IsSuppressed("M:App.Svc.DoSomething()", LlmSummaryRenderer.SuppressSet.Default).ShouldBeFalse();
        LlmSummaryRenderer.IsSuppressed("M:App.Repo.Save(App.Entity)", LlmSummaryRenderer.SuppressSet.Default).ShouldBeFalse();
    }

    // ── ParseArity helper ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void ParseArity_returns_correct_counts()
    {
        LlmSummaryRenderer.ParseArity("M:App.Svc.Do()").ShouldBe(0);
        LlmSummaryRenderer.ParseArity("M:App.Svc.Do(System.String)").ShouldBe(1);
        LlmSummaryRenderer.ParseArity("M:App.Svc.Do(System.String,System.Int32)").ShouldBe(2);
        LlmSummaryRenderer.ParseArity("M:App.Svc.Do(System.String,System.Int32,App.Context)").ShouldBe(3);
    }

    // ── FormatEffects helper ──────────────────────────────────────────────────────────────────────

    [Test]
    public void FormatEffects_empty_list_returns_empty_string()
    {
        LlmSummaryRenderer.FormatEffects([]).ShouldBe("");
    }

    [Test]
    public void FormatEffects_single_occurrence_no_count()
    {
        LlmSummaryRenderer.FormatEffects(["io:write"]).ShouldBe("io:write");
    }

    [Test]
    public void FormatEffects_multiple_occurrences_ascii_asterisk_count()
    {
        LlmSummaryRenderer.FormatEffects(["io:write", "io:write"]).ShouldBe("io:write*2");
    }

    [Test]
    public void FormatEffects_multiple_kinds_comma_joined_no_space()
    {
        LlmSummaryRenderer.FormatEffects(["io:read", "efcore:read", "io:read"]).ShouldBe("io:read*2,efcore:read");
    }

    [Test]
    public void FormatEffects_result_is_ascii_and_whitespace_free()
    {
        var result = LlmSummaryRenderer.FormatEffects(["io:read", "efcore:read", "io:read", "efcore:read"]);
        result.ShouldNotContain("×");
        result.ShouldNotContain(" ");
        result.ShouldBe("io:read*2,efcore:read*2");
    }

    // ── StripArityMarkers helper ──────────────────────────────────────────────────────────────────

    [Test]
    public void StripArityMarkers_strips_method_arity()
    {
        LlmSummaryRenderer.StripArityMarkers("Concat``1").ShouldBe("Concat");
        LlmSummaryRenderer.StripArityMarkers("Load``2").ShouldBe("Load");
    }

    [Test]
    public void StripArityMarkers_strips_type_arity()
    {
        LlmSummaryRenderer.StripArityMarkers("Foo`2.Bar").ShouldBe("Foo.Bar");
        LlmSummaryRenderer.StripArityMarkers("Cache`1.Get``1").ShouldBe("Cache.Get");
    }

    [Test]
    public void StripArityMarkers_strips_both_type_and_method_arity()
    {
        LlmSummaryRenderer.StripArityMarkers("Construct`2.New``1").ShouldBe("Construct.New");
    }

    [Test]
    public void StripArityMarkers_no_markers_is_identity()
    {
        LlmSummaryRenderer.StripArityMarkers("Repo.Save").ShouldBe("Repo.Save");
        LlmSummaryRenderer.StripArityMarkers("Svc.DoSomething").ShouldBe("Svc.DoSomething");
    }

    // ── IsCtorSymbol helper ───────────────────────────────────────────────────────────────────────

    [Test]
    public void IsCtorSymbol_returns_true_for_ctor_and_cctor()
    {
        LlmSummaryRenderer.IsCtorSymbol("M:App.Repo.Repository.#ctor(System.String)").ShouldBeTrue();
        LlmSummaryRenderer.IsCtorSymbol("M:App.Config.AppConfig.#cctor()").ShouldBeTrue();
    }

    [Test]
    public void IsCtorSymbol_returns_false_for_regular_methods()
    {
        LlmSummaryRenderer.IsCtorSymbol("M:App.Svc.DoSomething()").ShouldBeFalse();
        LlmSummaryRenderer.IsCtorSymbol("M:App.Repo.Save(App.Entity)").ShouldBeFalse();
    }

    // ── llm-ids format ────────────────────────────────────────────────────────────────────────────

    private static string RenderWithIds(
        IReadOnlyList<TraceNode> roots,
        IReadOnlyDictionary<string, List<string>>? rawEffects = null,
        LlmSummaryRenderer.LlmProjection projection = LlmSummaryRenderer.LlmProjection.Full,
        LlmSummaryRenderer.SuppressSet suppress = LlmSummaryRenderer.SuppressSet.None
    )
    {
        var sw = new StringWriter();
        LlmSummaryRenderer.RenderWithIds(
            roots: roots,
            rawEffectsByMethod: rawEffects ?? new Dictionary<string, List<string>>(StringComparer.Ordinal),
            projection: projection,
            output: sw,
            suppress: suppress
        );
        return sw.ToString();
    }

    [Test]
    public void LlmIds_header_is_8_column_schema()
    {
        var output = RenderWithIds([Node("M:App.Svc.Do()")]);
        Lines(output)[0].ShouldBe("id\tparent_id\tdepth\tname\tarity\tcalls\teffects\tflags");
    }

    [Test]
    public void LlmIds_every_row_has_8_columns()
    {
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Load()"));
        var lines = Lines(RenderWithIds([root]));
        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split('\t');
            cols.Length.ShouldBe(8, $"Expected 8 columns for llm-ids; got {cols.Length} on: {line}");
        }
    }

    [Test]
    public void LlmIds_ids_are_monotonic_1_based()
    {
        var root = Node("M:App.A.Go()", callSites: 1, Node("M:App.B.Run()", callSites: 1, Node("M:App.C.Exec()")));
        var lines = Lines(RenderWithIds([root]));
        var ids = lines.Skip(1).Select(l => int.Parse(l.Split('\t')[0], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        // Must be 1, 2, 3 (monotonic 1-based)
        for (var i = 0; i < ids.Count; i++)
        {
            ids[i].ShouldBe(i + 1, $"Expected id {i + 1} at position {i}, got {ids[i]}");
        }
    }

    [Test]
    public void LlmIds_parent_id_of_root_is_empty()
    {
        var root = Node("M:App.Svc.Do()");
        var lines = Lines(RenderWithIds([root]));
        var cols = lines[1].Split('\t');
        cols[1].ShouldBe(""); // parent_id empty for root
    }

    [Test]
    public void LlmIds_parent_id_resolves_to_earlier_row_id()
    {
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Load()", callSites: 2));
        var lines = Lines(RenderWithIds([root]));
        // line[1] = root (id=1, parent_id=""), line[2] = child (id=2, parent_id=1)
        var rootCols = lines[1].Split('\t');
        var childCols = lines[2].Split('\t');
        rootCols[0].ShouldBe("1"); // root id
        rootCols[1].ShouldBe(""); // root has no parent
        childCols[0].ShouldBe("2"); // child id
        childCols[1].ShouldBe("1"); // parent_id = root's id
    }

    [Test]
    public void LlmIds_parent_id_is_correct_for_nested_tree()
    {
        // A -> B -> C: A gets id=1, B gets id=2 (parent=1), C gets id=3 (parent=2)
        var root = Node("M:App.A.Go()", callSites: 1, Node("M:App.B.Run()", callSites: 1, Node("M:App.C.Exec()")));
        var lines = Lines(RenderWithIds([root]));
        var aCols = lines[1].Split('\t');
        var bCols = lines[2].Split('\t');
        var cCols = lines[3].Split('\t');
        aCols[0].ShouldBe("1");
        aCols[1].ShouldBe("");
        bCols[0].ShouldBe("2");
        bCols[1].ShouldBe("1"); // parent = A
        cCols[0].ShouldBe("3");
        cCols[1].ShouldBe("2"); // parent = B
    }

    [Test]
    public void LlmIds_multiple_roots_both_have_empty_parent_id()
    {
        var roots = new TraceNode[] { Node("M:App.A.Go()"), Node("M:App.B.Run()") };
        var lines = Lines(RenderWithIds(roots));
        // id=1 parent="" and id=2 parent=""
        lines[1].Split('\t')[1].ShouldBe("");
        lines[2].Split('\t')[1].ShouldBe("");
    }

    [Test]
    public void LlmIds_seen_row_flags_carries_canonical_id()
    {
        // First emit M:App.Repo.Load() fully (id=2), then it appears as Truncated (id=3).
        // The truncated row's flags must be "seen:2".
        var repo = Node("M:App.Repo.Load()");
        var truncatedRepo = Truncated("M:App.Repo.Load()");
        var root = Node("M:App.Svc.Do()", callSites: 1, repo, truncatedRepo);

        var lines = Lines(RenderWithIds([root]));
        // line[1]=Svc.Do (id=1), line[2]=Repo.Load first emission (id=2), line[3]=Repo.Load seen (id=3)
        lines.Count.ShouldBe(4); // header + 3 data rows
        var seenCols = lines[3].Split('\t');
        seenCols[7].ShouldBe("seen:2"); // flags = seen:<canonicalId>
    }

    [Test]
    public void LlmIds_seen_row_without_prior_expansion_uses_seen_only()
    {
        // A node that only appears as Truncated (never expanded) → flags = "seen" (no id).
        var root = Node("M:App.Svc.Do()", callSites: 1, Truncated("M:App.Repo.Load()"));
        var lines = Lines(RenderWithIds([root]));
        var seenCols = lines[2].Split('\t');
        seenCols[7].ShouldBe("seen"); // no canonical id available
    }

    [Test]
    public void LlmIds_composes_with_full_projection()
    {
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Load()"), Node("M:App.Repo.Save()"));
        var lines = Lines(RenderWithIds([root], projection: LlmSummaryRenderer.LlmProjection.Full));
        // header + root + Load + Save = 4
        lines.Count.ShouldBe(4);
        // All rows have 8 columns
        foreach (var line in lines.Skip(1))
        {
            line.Split('\t').Length.ShouldBe(8);
        }
    }

    [Test]
    public void LlmIds_composes_with_effects_flat_projection()
    {
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Load()"));
        var rawEffects = RawEffects(("M:App.Repo.Load()", ["efcore:read"]));
        var lines = Lines(RenderWithIds([root], rawEffects, LlmSummaryRenderer.LlmProjection.EffectsFlat));
        // header + Load only (root has no effect)
        lines.Count.ShouldBe(2);
        var cols = lines[1].Split('\t');
        cols.Length.ShouldBe(8);
        // name is at index 3 in llm-ids (id parent_id depth name ...)
        cols[3].ShouldBe("Repo.Load");
    }

    [Test]
    public void LlmIds_composes_with_suppress()
    {
        var ctor = Node("M:App.Repo.Repository.#ctor(App.Db.DbContext)");
        var root = Node("M:App.Svc.Do()", callSites: 1, ctor);
        var lines = Lines(RenderWithIds([root], suppress: LlmSummaryRenderer.SuppressSet.Ctors));
        // header + Svc.Do only — ctor is suppressed
        lines.Count.ShouldBe(2);
        lines.ShouldNotContain(l => l.Contains("#ctor"));
    }

    [Test]
    public void LlmIds_determinism_byte_for_byte_identical_across_two_runs()
    {
        var root = Node("M:App.Svc.RunAsync()", callSites: 1, Truncated("M:App.Repo.Load()"), Node("M:App.Repo.Save()"));
        var rawEffects = RawEffects(("M:App.Repo.Load()", ["efcore:read", "efcore:read"]), ("M:App.Repo.Save()", ["efcore:commit"]));

        var first = RenderWithIds([root], rawEffects);
        var second = RenderWithIds([root], rawEffects);

        first.ShouldBe(second);
    }

    [Test]
    public void LlmIds_llm_format_output_is_unchanged_regression_guard()
    {
        // Verify that adding llm-ids did NOT change the llm (positional) format output.
        var root = Node("M:App.Svc.Do()", callSites: 1, Truncated("M:App.Repo.Load()"), Node("M:App.Cache.Warm()"));
        var rawEffects = RawEffects(("M:App.Repo.Load()", ["efcore:read"]));

        // Render the original llm format twice; it must be identical regardless of llm-ids being present.
        var llmFirst = Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.Full);
        var llmSecond = Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.Full);
        llmFirst.ShouldBe(llmSecond);

        // The llm format header must still be the 6-column schema, not the 8-column llm-ids schema.
        Lines(llmFirst)[0].ShouldBe("depth\tname\tarity\tcalls\teffects\tflags");

        // No id or parent_id columns appear.
        Lines(llmFirst)[0].ShouldNotContain("id\t");
        Lines(llmFirst)[0].ShouldNotContain("parent_id");
    }
}

// CLI-level tests for --format llm: verify the option parses, mutual exclusion works, and output has the right shape.
public sealed class LlmSummaryCliTests
{
    [Test]
    public async Task Format_llm_with_full_is_accepted()
    {
        // --view full --format llm is a valid combination (Full projection).
        var output = new StringWriter();
        var error = new StringWriter();

        // No index exists, but it should fail with "No symbol matches" (exit 1) not a validation error.
        // We only check that the CLI does NOT emit a "can't be combined" validation error.
        var exitCode = await CliApplication.RunAsync(["tree", "X", "--view", "full", "--format", "llm"], output, error);

        // May fail for "no index" reasons, but not for a validation/parse error.
        error.ToString().ShouldNotContain("can't be combined");
        error.ToString().ShouldNotContain("--format llm");
    }

    [Test]
    public async Task Format_llm_with_effects_is_accepted()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--view", "effects", "--format", "llm"], output, error);

        error.ToString().ShouldNotContain("can't be combined");
    }

    [Test]
    public async Task Format_llm_alone_is_accepted()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--format", "llm"], output, error);

        error.ToString().ShouldNotContain("can't be combined");
    }

    [Test]
    public async Task Format_llm_combined_with_summary_is_rejected()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--format", "llm", "--view", "summary"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("can't be combined");
        error.ToString().ShouldContain("--view summary");
    }

    [Test]
    public async Task Format_llm_combined_with_hazards_is_rejected()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--format", "llm", "--view", "hazards"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("can't be combined");
        error.ToString().ShouldContain("--view hazards");
    }

    [Test]
    public async Task Unknown_view_value_is_rejected()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--view", "invalid"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("invalid");
        error.ToString().ShouldContain("not recognized");
    }

    [Test]
    public async Task Format_llm_effects_emits_header_and_correct_shape_on_real_index()
    {
        using var playground = await Rig.Tests.Fixtures.TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = System.IO.Path.Combine(playground.RootDirectory, "workspace");
        var rulesPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(playground.SolutionPath)!, "rig.rules.json");
        var sw = new StringWriter();
        var err = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], sw, err, workingDirectory)).ShouldBe(0);

        sw.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "PaymentGatewayCaller.Dispatch", "--view", "effects", "--format", "llm", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        // --view effects → EffectsFlat projection: 7-column header (with parent-name column).
        lines[0].ShouldBe("depth\tparent\tname\tarity\tcalls\teffects\tflags");
        // At least one data row (effects exist: gateway_ask, gateway_tell)
        lines.Count.ShouldBeGreaterThan(1);
        // Every data row must have exactly 7 tab-separated columns
        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split('\t');
            cols.Length.ShouldBe(7);
            // name column (index 2) must not contain full CLR namespace prefixes
            cols[2].ShouldNotContain("System.");
            // effects column (index 5) must be ASCII, no × character, no internal spaces
            if (cols[5].Length > 0)
            {
                cols[5].ShouldNotContain("×");
                cols[5].ShouldNotContain(" ");
            }
        }
    }

    [Test]
    public async Task Full_format_llm_emits_more_rows_than_effects_format_llm()
    {
        using var playground = await Rig.Tests.Fixtures.TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = System.IO.Path.Combine(playground.RootDirectory, "workspace");
        var rulesPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(playground.SolutionPath)!, "rig.rules.json");
        var sw = new StringWriter();
        var err = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], sw, err, workingDirectory)).ShouldBe(0);

        sw.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "PaymentGatewayCaller.Dispatch", "--view", "full", "--format", "llm", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);
        var fullLines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        sw.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "PaymentGatewayCaller.Dispatch", "--view", "effects", "--format", "llm", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);
        var effectsLines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        // --view full --format llm has at least as many rows as --view effects --format llm (effects is the subset)
        fullLines.ShouldBeGreaterThanOrEqualTo(effectsLines);
    }

    [Test]
    public async Task Default_format_llm_row_count_is_between_full_and_effects_flat()
    {
        using var playground = await Rig.Tests.Fixtures.TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = System.IO.Path.Combine(playground.RootDirectory, "workspace");
        var rulesPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(playground.SolutionPath)!, "rig.rules.json");
        var sw = new StringWriter();
        var err = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], sw, err, workingDirectory)).ShouldBe(0);

        sw.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "PaymentGatewayCaller.Dispatch", "--view", "full", "--format", "llm", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);
        var fullLines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        sw.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "PaymentGatewayCaller.Dispatch", "--format", "llm", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);
        var defaultLines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        sw.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "PaymentGatewayCaller.Dispatch", "--view", "effects", "--format", "llm", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);
        var effectsLines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        // full >= default (effectful-paths with spine) >= effects-flat
        fullLines.ShouldBeGreaterThanOrEqualTo(defaultLines);
        defaultLines.ShouldBeGreaterThanOrEqualTo(effectsLines);
    }

    [Test]
    public async Task Format_llm_ids_alone_is_accepted()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--format", "llm-ids"], output, error);

        error.ToString().ShouldNotContain("can't be combined");
    }

    [Test]
    public async Task Format_llm_ids_with_full_is_accepted()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--view", "full", "--format", "llm-ids"], output, error);

        error.ToString().ShouldNotContain("can't be combined");
    }

    [Test]
    public async Task Format_llm_ids_with_effects_is_accepted()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--view", "effects", "--format", "llm-ids"], output, error);

        error.ToString().ShouldNotContain("can't be combined");
    }

    [Test]
    public async Task Format_llm_ids_combined_with_summary_is_rejected()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--format", "llm-ids", "--view", "summary"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("can't be combined");
        error.ToString().ShouldContain("--view summary");
    }

    [Test]
    public async Task Format_llm_ids_combined_with_hazards_is_rejected()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--format", "llm-ids", "--view", "hazards"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("can't be combined");
        error.ToString().ShouldContain("--view hazards");
    }

    [Test]
    public async Task Format_llm_ids_emits_8_column_header_and_rows_on_real_index()
    {
        using var playground = await Rig.Tests.Fixtures.TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = System.IO.Path.Combine(playground.RootDirectory, "workspace");
        var rulesPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(playground.SolutionPath)!, "rig.rules.json");
        var sw = new StringWriter();
        var err = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], sw, err, workingDirectory)).ShouldBe(0);

        sw.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "PaymentGatewayCaller.Dispatch", "--view", "full", "--format", "llm-ids", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        // 8-column header
        lines[0].ShouldBe("id\tparent_id\tdepth\tname\tarity\tcalls\teffects\tflags");
        lines.Count.ShouldBeGreaterThan(1);

        // Collect id → row for parent_id validation.
        var idToRowIndex = new Dictionary<int, int>(capacity: lines.Count);
        for (var i = 1; i < lines.Count; i++)
        {
            var cols = lines[i].Split('\t');
            cols.Length.ShouldBe(8, $"Expected 8 columns for llm-ids; got {cols.Length} on: {lines[i]}");
            var rowId = int.Parse(cols[0], System.Globalization.CultureInfo.InvariantCulture);
            idToRowIndex[rowId] = i;
        }

        // ids must be monotonic 1-based
        var sortedIds = idToRowIndex.Keys.OrderBy(x => x).ToList();
        for (var i = 0; i < sortedIds.Count; i++)
        {
            sortedIds[i].ShouldBe(i + 1, $"id at position {i} should be {i + 1}");
        }

        // Every non-root row's parent_id must refer to a row with a smaller id.
        for (var i = 1; i < lines.Count; i++)
        {
            var cols = lines[i].Split('\t');
            var parentIdStr = cols[1];
            if (string.IsNullOrEmpty(parentIdStr))
            {
                continue; // root row — no parent
            }

            var parentId = int.Parse(parentIdStr, System.Globalization.CultureInfo.InvariantCulture);
            var thisId = int.Parse(cols[0], System.Globalization.CultureInfo.InvariantCulture);
            parentId.ShouldBeLessThan(thisId, $"parent_id {parentId} should be less than row id {thisId}");
            idToRowIndex.ContainsKey(parentId).ShouldBeTrue($"parent_id {parentId} does not match any emitted row id");
        }
    }

    [Test]
    public async Task Default_format_llm_output_is_reconstructable()
    {
        using var playground = await Rig.Tests.Fixtures.TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = System.IO.Path.Combine(playground.RootDirectory, "workspace");
        var rulesPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(playground.SolutionPath)!, "rig.rules.json");
        var sw = new StringWriter();
        var err = new StringWriter();

        (await CliApplication.RunAsync(["index", playground.SolutionPath], sw, err, workingDirectory)).ShouldBe(0);

        sw.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "PaymentGatewayCaller.Dispatch", "--format", "llm", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        // Default projection → EffectfulPaths: 6-column header (no parent column — depth+order encode linkage).
        lines[0].ShouldBe("depth\tname\tarity\tcalls\teffects\tflags");

        // Reconstructability via depth+order: every non-root row must have a preceding row at depth-1.
        var lastAtDepth = new Dictionary<int, string>(capacity: 8);
        foreach (var dataLine in lines.Skip(1))
        {
            var cols = dataLine.Split('\t');
            cols.Length.ShouldBe(6, $"Expected 6 columns (no parent) for default/EffectfulPaths; got {cols.Length} on: {dataLine}");
            var depth = int.Parse(cols[0], System.Globalization.CultureInfo.InvariantCulture);
            var name = cols[1];
            if (depth > 0)
            {
                lastAtDepth.ShouldContainKey(
                    depth - 1,
                    $"Row '{name}' at depth {depth} has no preceding row at depth {depth - 1} — not reconstructable by depth+order."
                );
            }

            lastAtDepth[depth] = name;
        }
    }
}

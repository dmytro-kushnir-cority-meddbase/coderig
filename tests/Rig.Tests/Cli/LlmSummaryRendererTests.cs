using Rig.Cli;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Shouldly;

namespace Rig.Tests.Cli;

// Unit tests for LlmSummaryRenderer: header, name shortening, arity, effect aggregation, x-phase,
// lambda suppression, determinism. Uses synthetic TraceNode forests — no DB, no graph.
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
        LlmSummaryRenderer.LlmProjection projection = LlmSummaryRenderer.LlmProjection.Full
    )
    {
        var sw = new StringWriter();
        LlmSummaryRenderer.Render(
            roots: roots,
            rawEffectsByMethod: rawEffects ?? new Dictionary<string, List<string>>(StringComparer.Ordinal),
            projection: projection,
            output: sw
        );
        return sw.ToString();
    }

    private static List<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

    // ── header ───────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Output_starts_with_the_header_row()
    {
        var output = Render([Node("M:App.Svc.Do()")]);

        Lines(output)[0].ShouldBe("depth\tparent\tname\tarity\tcalls\teffects\tflags");
    }

    // ── name shortening: no namespaces, no parameter types ───────────────────────────────────────

    [Test]
    public void Name_strips_namespace_leaving_TypeName_MethodName()
    {
        var output = Render([Node("M:My.Namespace.Service.DoSomething(System.String,System.Int32)")]);

        var data = Lines(output)[1];
        // name column is at index 2 (0-based: depth, parent, name, …)
        var cols = data.Split('\t');
        cols[2].ShouldBe("Service.DoSomething");
    }

    [Test]
    public void Name_has_no_parameter_types_in_the_name_column()
    {
        var output = Render([Node("M:App.Repo.Save(App.Entity,System.Int32)")]);

        var data = Lines(output)[1];
        var cols = data.Split('\t');
        cols[2].ShouldNotContain("App.Entity");
        cols[2].ShouldNotContain("System.Int32");
        cols[2].ShouldBe("Repo.Save");
    }

    // ── arity ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Arity_is_parameter_count_not_type_names()
    {
        // 3-param method
        var output = Render([Node("M:App.Svc.Create(System.String,System.Int32,App.Context)")]);

        var cols = Lines(output)[1].Split('\t');
        cols[3].ShouldBe("3"); // arity column
    }

    [Test]
    public void Arity_is_zero_for_no_arg_method()
    {
        var output = Render([Node("M:App.Svc.Init()")]);

        var cols = Lines(output)[1].Split('\t');
        cols[3].ShouldBe("0");
    }

    [Test]
    public void Arity_is_one_for_single_param()
    {
        var output = Render([Node("M:App.Svc.Load(System.Int32)")]);

        var cols = Lines(output)[1].Split('\t');
        cols[3].ShouldBe("1");
    }

    // ── calls ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Calls_column_reflects_CallSites()
    {
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Save()", callSites: 3));

        var lines = Lines(Render([root]));
        // line[1] = root, line[2] = child
        var childCols = lines[2].Split('\t');
        childCols[4].ShouldBe("3"); // calls column
    }

    // ── effects aggregation ──────────────────────────────────────────────────────────────────────

    [Test]
    public void Single_effect_occurrence_has_no_count_suffix()
    {
        var root = Node("M:App.Svc.Do()");
        var rawEffects = RawEffects(("M:App.Svc.Do()", ["io:read"]));

        var cols = Lines(Render([root], rawEffects))[1].Split('\t');
        cols[5].ShouldBe("io:read");
    }

    [Test]
    public void Multiple_occurrences_of_same_effect_are_aggregated_with_count()
    {
        var root = Node("M:App.Svc.Do()");
        var rawEffects = RawEffects(("M:App.Svc.Do()", ["io:read", "io:read", "io:read"]));

        var cols = Lines(Render([root], rawEffects))[1].Split('\t');
        cols[5].ShouldBe("io:read ×3");
    }

    [Test]
    public void Multiple_distinct_effects_are_comma_separated_with_per_kind_counts()
    {
        var root = Node("M:App.Svc.Do()");
        var rawEffects = RawEffects(("M:App.Svc.Do()", ["io:read", "efcore:read", "io:read", "efcore:read", "io:read"]));

        var cols = Lines(Render([root], rawEffects))[1].Split('\t');
        // first-seen order: io:read then efcore:read
        cols[5].ShouldBe("io:read ×3, efcore:read ×2");
    }

    [Test]
    public void Effects_column_is_empty_when_node_has_no_effects()
    {
        var root = Node("M:App.Svc.Do()");

        var cols = Lines(Render([root]))[1].Split('\t');
        cols[5].ShouldBe("");
    }

    // ── x-phase (Truncated = elided/seen) ───────────────────────────────────────────────────────

    [Test]
    public void X_phase_node_is_emitted_with_x_phase_flag()
    {
        // An already-expanded node appears again as Truncated — the elided marker.
        var root = Node("M:App.Svc.Do()", callSites: 1, Truncated("M:App.Repo.Load()"));
        var rawEffects = RawEffects(("M:App.Repo.Load()", ["efcore:read", "efcore:read"]));

        var lines = Lines(Render([root], rawEffects));
        // line[2] = the truncated child
        var xphaseLine = lines[2];
        xphaseLine.ShouldContain("x-phase");
        // Must include its effects even though Truncated
        xphaseLine.ShouldContain("efcore:read ×2");
    }

    [Test]
    public void X_phase_node_is_NOT_suppressed()
    {
        var root = Node("M:App.Svc.Do()", callSites: 1, Truncated("M:App.Repo.Load()"));

        var lines = Lines(Render([root]));
        // header + root + x-phase child = 3 lines
        lines.Count.ShouldBe(3);
        lines[2].ShouldContain("Repo.Load");
    }

    // ── lambda suppression ───────────────────────────────────────────────────────────────────────

    [Test]
    public void Lambda_node_is_suppressed()
    {
        var lambdaId = "M:App.Svc.Do()~λ0";
        var root = Node("M:App.Caller.Go()", callSites: 1, Node(lambdaId));

        var lines = Lines(Render([root]));
        // header + Caller.Go only — lambda is gone
        lines.Count.ShouldBe(2);
        lines.ShouldNotContain(l => l.Contains("~λ") || l.Contains("λ0"));
    }

    [Test]
    public void Lambda_children_are_still_walked_after_suppression()
    {
        // Lambda wrapping a named method — the named method should still surface.
        var lambdaId = "M:App.Svc.Do()~λ0";
        var namedChild = Node("M:App.Repo.Save()");
        var lambdaNode = Node(lambdaId, callSites: 1, namedChild);
        var root = Node("M:App.Caller.Go()", callSites: 1, lambdaNode);

        var lines = Lines(Render([root]));
        // header + Caller.Go + Repo.Save (lambda skipped but its child emitted at same depth)
        lines.Count.ShouldBe(3);
        lines.ShouldContain(l => l.Contains("Repo.Save"));
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
        var names = lines.Skip(1).Select(l => l.Split('\t')[2]).ToList();
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
        var names = lines.Skip(1).Select(l => l.Split('\t')[2]).ToList();
        names.ShouldContain("Svc.Root");
        names.ShouldContain("Svc.Branch1");
        names.ShouldContain("Repo.Save");
        names.ShouldNotContain("Svc.Branch2");
        names.ShouldNotContain("Svc.Inner");
    }

    [Test]
    public void EffectfulPaths_output_is_reconstructable_every_parent_resolves_to_earlier_row()
    {
        // Build a tree with a multi-level spine: Root -> Svc -> Repo (effect)
        //                                               -> Util (no effect)
        var repo = Node("M:App.Data.Repo.Fetch()");
        var svc = Node("M:App.Services.Svc.Process()", callSites: 1, repo, Node("M:App.Utils.Util.NoOp()"));
        var root = Node("M:App.Api.Controller.Handle()", callSites: 1, svc);
        var rawEffects = RawEffects(("M:App.Data.Repo.Fetch()", ["efcore:read"]));

        var lines = Lines(Render([root], rawEffects, LlmSummaryRenderer.LlmProjection.EffectfulPaths));
        // Collect emitted names in order (excluding header).
        var emittedNames = lines.Skip(1).Select(l => l.Split('\t')[2]).ToList();

        // Every non-root row's parent column must name a row already emitted at a shallower depth.
        var emittedSoFar = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dataLine in lines.Skip(1))
        {
            var cols = dataLine.Split('\t');
            var parent = cols[1];
            var name = cols[2];
            if (!string.IsNullOrEmpty(parent))
            {
                emittedSoFar.ShouldContain(
                    parent,
                    $"Row '{name}' has parent '{parent}' but that name was not yet emitted — output is not reconstructable."
                );
            }

            emittedSoFar.Add(name);
        }

        // Sanity: Util (effectless) should be pruned.
        emittedNames.ShouldNotContain("Util.NoOp");
    }

    // ── parent column ────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Root_node_has_empty_parent_column()
    {
        var root = Node("M:App.Svc.Do()");

        var cols = Lines(Render([root]))[1].Split('\t');
        cols[1].ShouldBe(""); // parent
    }

    [Test]
    public void Child_node_parent_column_is_the_caller_short_name()
    {
        var root = Node("M:App.Svc.Do()", callSites: 1, Node("M:App.Repo.Load()"));

        var lines = Lines(Render([root]));
        var childCols = lines[2].Split('\t');
        childCols[1].ShouldBe("Svc.Do"); // parent = short name of caller
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
    public void IsSuppressed_returns_true_for_lambda_ids()
    {
        LlmSummaryRenderer.IsSuppressed("M:App.Svc.Do()~λ0").ShouldBeTrue();
        LlmSummaryRenderer.IsSuppressed("M:App.Svc.Do()~λ3").ShouldBeTrue();
    }

    [Test]
    public void IsSuppressed_returns_false_for_normal_ids()
    {
        LlmSummaryRenderer.IsSuppressed("M:App.Svc.DoSomething()").ShouldBeFalse();
        LlmSummaryRenderer.IsSuppressed("M:App.Repo.Save(App.Entity)").ShouldBeFalse();
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
    public void FormatEffects_multiple_occurrences_get_count()
    {
        LlmSummaryRenderer.FormatEffects(["io:write", "io:write"]).ShouldBe("io:write ×2");
    }

    [Test]
    public void FormatEffects_multiple_kinds_comma_separated()
    {
        LlmSummaryRenderer.FormatEffects(["io:read", "efcore:read", "io:read"]).ShouldBe("io:read ×2, efcore:read");
    }
}

// CLI-level tests for --format llm: verify the option parses, mutual exclusion works, and output has the right shape.
public sealed class LlmSummaryCliTests
{
    [Test]
    public async Task Format_llm_with_full_is_accepted()
    {
        // --full --format llm is a valid combination (Full projection).
        var output = new StringWriter();
        var error = new StringWriter();

        // No index exists, but it should fail with "No symbol matches" (exit 1) not a validation error.
        // We only check that the CLI does NOT emit a "can't be combined" validation error.
        var exitCode = await CliApplication.RunAsync(["tree", "X", "--full", "--format", "llm"], output, error);

        // May fail for "no index" reasons, but not for a validation/parse error.
        error.ToString().ShouldNotContain("can't be combined");
        error.ToString().ShouldNotContain("--format llm");
    }

    [Test]
    public async Task Format_llm_with_effects_is_accepted()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--effects", "--format", "llm"], output, error);

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

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--format", "llm", "--summary"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("can't be combined");
        error.ToString().ShouldContain("--summary");
    }

    [Test]
    public async Task Format_llm_combined_with_hazards_is_rejected()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--format", "llm", "--hazards"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("can't be combined");
        error.ToString().ShouldContain("--hazards");
    }

    [Test]
    public async Task Full_and_effects_together_are_still_rejected()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["tree", "X", "--full", "--effects"], output, error);

        exitCode.ShouldBe(1);
        error.ToString().ShouldContain("can't be combined");
        error.ToString().ShouldContain("--full");
        error.ToString().ShouldContain("--effects");
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
                ["tree", "PaymentGatewayCaller.Dispatch", "--effects", "--format", "llm", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        // Header must be the LLM header
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
                ["tree", "PaymentGatewayCaller.Dispatch", "--full", "--format", "llm", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);
        var fullLines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        sw.GetStringBuilder().Clear();
        (
            await CliApplication.RunAsync(
                ["tree", "PaymentGatewayCaller.Dispatch", "--effects", "--format", "llm", "--rules", rulesPath],
                sw,
                err,
                workingDirectory
            )
        ).ShouldBe(0);
        var effectsLines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        // --full --format llm has at least as many rows as --effects --format llm (effects is the subset)
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
                ["tree", "PaymentGatewayCaller.Dispatch", "--full", "--format", "llm", "--rules", rulesPath],
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
                ["tree", "PaymentGatewayCaller.Dispatch", "--effects", "--format", "llm", "--rules", rulesPath],
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
        lines[0].ShouldBe("depth\tparent\tname\tarity\tcalls\teffects\tflags");

        // Reconstructability: for every non-root data row, the parent column must name a row already emitted.
        var emittedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dataLine in lines.Skip(1))
        {
            var cols = dataLine.Split('\t');
            cols.Length.ShouldBe(7);
            var parent = cols[1];
            var name = cols[2];
            if (!string.IsNullOrEmpty(parent))
            {
                emittedNames.ShouldContain(
                    parent,
                    $"Row '{name}' has parent '{parent}' but that name was not yet emitted (output not reconstructable)."
                );
            }

            emittedNames.Add(name);
        }
    }
}

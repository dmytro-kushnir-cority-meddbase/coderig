using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Shouldly;

namespace Rig.Tests.Cli;

// `tree --guards` (M3 increment 3): the renderer marks a control-dependence-GUARDED call edge with
// ⎇ [predicate] — the analog of 🔁[loop] — decoded from the reaching edge's frozen guard set
// (TraceNode.EnclosingGuards). A must-run edge (empty/null guard set) carries no glyph, and the whole
// annotation is gated behind --guards so the default tree (and its golden tests) is unchanged.
public sealed class TreeGuardsRenderTests
{
    // A guarded child of `Root`: its EnclosingGuards encodes the given (predicate, when-true) pairs.
    private static TraceNode Guarded(string id, params (string Predicate, bool WhenTrue)[] guards) =>
        new(id, "invocation", null, null, [], EnclosingGuards: FactStructuralContext.EncodeGuards(guards));

    // A must-run child of `Root`: no guards (the unconditional spine).
    private static TraceNode MustRun(string id) => new(id, "invocation", null, null, []);

    private static string Render(TraceNode root, bool guards)
    {
        var output = new StringWriter();
        TreeRenderer.RenderTreeNode(
            root,
            prefix: "",
            isLast: true,
            isRoot: true,
            new Dictionary<string, List<string>>(StringComparer.Ordinal),
            prune: false,
            FactRenderRules.Empty,
            new Dictionary<string, List<string>>(StringComparer.Ordinal),
            output,
            guards: guards
        );
        return output.ToString();
    }

    private static List<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

    [Test]
    public void A_guarded_edge_gets_a_glyph_and_a_must_run_edge_does_not()
    {
        var root = new TraceNode(
            "M:App.Svc.RemoveConfirm()",
            "entry",
            null,
            null,
            [MustRun("M:App.Audit.Write()"), Guarded("M:App.Repo.BulkDelete()", ("invoice.IsHealthcode", true))]
        );

        var lines = Lines(Render(root, guards: true));

        // The guarded child carries ⎇ [predicate]; the must-run sibling carries no ⎇ (it's the spine).
        lines.ShouldContain(l => l.Contains("Repo.BulkDelete") && l.Contains("⎇ [invoice.IsHealthcode]"));
        lines.ShouldContain(l => l.Contains("Audit.Write") && !l.Contains("⎇"));
        // The root (entry, no reaching edge) is never guarded.
        lines[0].ShouldStartWith("Svc.RemoveConfirm");
        lines[0].ShouldNotContain("⎇");
    }

    [Test]
    public void Without_the_flag_no_guard_glyph_is_rendered()
    {
        // Default tree must be byte-stable: a guarded edge renders identically to today (no ⎇) unless --guards.
        var root = new TraceNode(
            "M:App.Svc.Go()",
            "entry",
            null,
            null,
            [Guarded("M:App.Repo.BulkDelete()", ("invoice.IsHealthcode", true))]
        );

        Render(root, guards: false).ShouldNotContain("⎇");
    }

    [Test]
    public void The_else_arm_polarity_negates_the_predicate()
    {
        var root = new TraceNode("M:App.Svc.Go()", "entry", null, null, [Guarded("M:App.Repo.Skip()", ("invoice.IsHealthcode", false))]);

        Render(root, guards: true).ShouldContain("⎇ [!invoice.IsHealthcode]");
    }

    [Test]
    public void A_compound_predicate_is_parenthesised_when_negated()
    {
        // `!a == null` would mis-bind; the renderer wraps a compound (whitespace-bearing) predicate.
        var root = new TraceNode("M:App.Svc.Go()", "entry", null, null, [Guarded("M:App.Repo.Skip()", ("a == null", false))]);

        Render(root, guards: true).ShouldContain("⎇ [!(a == null)]");
    }

    [Test]
    public void Multiple_guards_join_as_an_and_chain()
    {
        // All guards must hold for the call to run → AND-joined; mixed polarity preserved.
        var root = new TraceNode(
            "M:App.Svc.Go()",
            "entry",
            null,
            null,
            [Guarded("M:App.Notify.Send()", ("a != null", true), ("order.IsDirty", true))]
        );

        Render(root, guards: true).ShouldContain("⎇ [a != null && order.IsDirty]");
    }

    // A child reached inside a `foreach (ident in COLL)` — carries the loop marker AND the given guards.
    private static TraceNode Looped(string id, string loopDetail, params (string Predicate, bool WhenTrue)[] guards) =>
        new(id, "invocation", "foreach", loopDetail, [], EnclosingGuards: FactStructuralContext.EncodeGuards(guards));

    [Test]
    public void A_foreach_move_next_guard_is_dropped_as_redundant_with_the_loop_marker()
    {
        // `foreach (root in roots) EmitTsvNode()` — the call's guard IS the collection `roots`, which the
        // 🔁[root in roots] marker already conveys. The ⎇ glyph must not duplicate it (no signal).
        var root = new TraceNode("M:App.X.Go()", "entry", null, null, [Looped("M:App.X.EmitTsvNode()", "root in roots", ("roots", true))]);

        var line = Lines(Render(root, guards: true)).Single(l => l.Contains("EmitTsvNode", StringComparison.Ordinal));
        line.ShouldContain("🔁"); // the loop marker stays
        line.ShouldNotContain("⎇"); // ...but the redundant collection-guard glyph is gone
    }

    [Test]
    public void A_genuine_inner_guard_inside_a_foreach_is_kept()
    {
        // `foreach (projPath in projectPaths) if (File.Exists(projPath)) Parse()` — the inner condition is
        // high signal and distinct from the collection, so it survives the redundant-with-loop filter.
        var root = new TraceNode(
            "M:App.X.Go()",
            "entry",
            null,
            null,
            [Looped("M:App.X.Parse()", "projPath in projectPaths", ("File.Exists(projPath)", true))]
        );

        Render(root, guards: true).ShouldContain("⎇ [File.Exists(projPath)]");
    }

    [Test]
    public void Only_the_collection_guard_is_filtered_when_a_real_guard_also_applies()
    {
        // Guarded by BOTH the foreach (collection `items`) and a real inner `if (x.IsDirty)` — keep only the
        // inner one. A leading `⎇ [x.IsDirty` (not `⎇ [items && …`) proves the collection was dropped.
        var root = new TraceNode(
            "M:App.X.Go()",
            "entry",
            null,
            null,
            [Looped("M:App.X.Save()", "x in items", ("items", true), ("x.IsDirty", true))]
        );

        Render(root, guards: true).ShouldContain("⎇ [x.IsDirty]");
    }
}

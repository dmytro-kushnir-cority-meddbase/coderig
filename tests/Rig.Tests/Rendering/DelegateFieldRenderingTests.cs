using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Shouldly;

namespace Rig.Tests.Rendering;

// Verified against real `rig tree --view full` output.
public sealed class DelegateFieldRenderingTests
{
    private static TraceNode Child(string id, string edgeKind, int fanout = 0) => new(id, edgeKind, null, null, [], Fanout: fanout);

    private static string Render(TraceNode root)
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
            output
        );
        return output.ToString();
    }

    private static List<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

    [Test]
    public void A_delegate_field_edge_gets_the_quiet_guillemet_marker()
    {
        var root = new TraceNode(
            "M:DFS.DFS.Save()",
            "invocation",
            null,
            null,
            [Child("M:DFS.DFS.InitialiseFromRuntime()~λ0", EdgeKinds.DelegateField), Child("M:DFS.Metrics.Log1()", "invocation")]
        );

        var lines = Lines(Render(root));

        lines.ShouldContain(l => l.Contains("InitialiseFromRuntime") && l.Contains("«delegate-field»"));
        lines.ShouldContain(l => l.Contains("Metrics.Log1") && !l.Contains("«delegate-field»"));
    }

    [Test]
    public void The_marker_is_not_the_handoff_glyph()
    {
        var root = new TraceNode(
            "M:DFS.DFS.Save()",
            "invocation",
            null,
            null,
            [Child("M:DFS.DFS.InitialiseFromRuntime()~λ0", EdgeKinds.DelegateField)]
        );

        var rendered = Render(root);
        rendered.ShouldContain("«delegate-field»");
        rendered.ShouldNotContain("⤳");
    }

    [Test]
    public void A_multi_assignment_fan_out_mirrors_the_dispatch_count_form()
    {
        var root = new TraceNode(
            "M:DFS.DFS.Save()",
            "invocation",
            null,
            null,
            [Child("M:DFS.DFS.InitialiseFromRuntime()~λ0", EdgeKinds.DelegateField, fanout: 3)]
        );

        Render(root).ShouldContain("«delegate-field ×3 fan-out»");
    }

    [Test]
    public void An_ordinary_invocation_edge_gets_no_marker()
    {
        var root = new TraceNode("M:DFS.DFS.Save()", "invocation", null, null, [Child("M:DFS.DFS.Plain()", "invocation")]);

        Render(root).ShouldNotContain("«");
    }
}

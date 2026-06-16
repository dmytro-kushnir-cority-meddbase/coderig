using Rig.Cli.Deployments;

namespace Rig.Cli.Rendering;

// The ONE place entry points are rendered, so every command (derive, callers, tree, reaches, path)
// shows them identically: a ▶ marker, the EP kind, the route, and a deployment chip naming the
// service(s) whose process loads the EP. An EP that lands in several services (a shared library
// referenced by many hosts — the common case) renders as "⟦N svcs: a, b, c +k⟧" so the fan-out is
// explicit rather than hidden behind a single name.
internal static class EntryPointRenderer
{
    public const string Marker = "▶";

    // The deployment chip for an EP at filePath, showing the services it is ACTIVE-IN (loaded AND
    // capability-gated in): "⟦A (iis)⟧", "⟦3 svcs: A (iis), B, C⟧", or "⟦no service⟧" when the owning
    // project is in no service closure (tests/tools). When the EP's rule `requires` tokens that some
    // loaded services don't `provide`, those drop out of the active set and surface as a dim delta —
    // "⟦A (iis) · 1 linked-inactive⟧" — so the "loaded here, doesn't run here" signal stays visible.
    // `requires` null/empty (the default / ungated EP) makes active == loaded, so the chip is unchanged.
    public static string DeployTag(DeploymentMap deployments, string? filePath, IReadOnlyList<string>? requires = null, int cap = 3)
    {
        var loaded = deployments.ServicesForFile(filePath);
        if (loaded.Count == 0)
            return "⟦no service⟧";

        var active = deployments.ActiveServices(loaded, requires);
        var inactive = loaded.Count - active.Count;
        if (active.Count == 0)
            // Linked into hosts but gated out of all of them — the "runs here? no, anywhere" signal.
            return $"⟦0 active · {inactive} linked-inactive⟧";

        var shown = string.Join(", ", active.Take(cap).Select(s => FormatService(deployments, s)));
        var overflow = active.Count > cap ? $" +{active.Count - cap}" : "";
        // Lead with the count when an EP fans out to multiple services so it reads as "this runs in N hosts".
        var lead = active.Count > 1 ? $"{active.Count} svcs: " : "";
        var inactiveDelta = inactive > 0 ? $" · {inactive} linked-inactive" : "";
        return $"⟦{lead}{shown}{overflow}{inactiveDelta}⟧";
    }

    private static string FormatService(DeploymentMap deployments, string name)
    {
        var kind = deployments.Service(name)?.Kind;
        return kind is null ? name : $"{name} ({kind})";
    }

    // The inline EP marker for a tree node / header: "▶ echoactor " (prefix) + " ⟦…⟧" (suffix),
    // so a node renders as "▶ echoactor SomeHandler …  ⟦MedDBase⟧". Empty strings when not an EP.
    public static (string Prefix, string Suffix) NodeChip(
        DeploymentMap deployments,
        string? kind,
        string? filePath,
        IReadOnlyList<string>? requires = null
    )
    {
        if (kind is null)
            return ("", "");
        return ($"{Marker} {kind} ", $"  {DeployTag(deployments, filePath, requires)}");
    }

    // The two-line EP listing block (the "custom rendering"): the ▶/kind/route/deploy-chip line, then
    // the indented source location. `shorten` keeps path-display logic with the caller.
    public static void WriteEntryPoint(
        TextWriter output,
        DeploymentMap deployments,
        string kind,
        string route,
        string? filePath,
        int line,
        Func<string, string> shorten,
        IReadOnlyList<string>? requires = null,
        int kindPad = 10
    )
    {
        output.WriteLine($"{Marker} {kind.PadRight(kindPad)} {route}  {DeployTag(deployments, filePath, requires)}");
        if (filePath is not null)
            output.WriteLine($"     {shorten(filePath)}:{line}");
    }
}

// Per-render lookup so a call-tree node can be marked when it IS itself an entry point: maps a node's
// SymbolId to its declaration site, and a site (FilePath, Line) to its EP kind. Built once per tree
// render and only when deployments are configured, then threaded through RenderTreeNode.
internal sealed record EpRenderContext(
    DeploymentMap Deployments,
    IReadOnlyDictionary<string, (string? File, int Line)> SiteById,
    // (File, Line) -> the EP's kind AND its capability requirements, so a node/header can compute
    // active-in (not just loaded-in) right where it renders.
    IReadOnlyDictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)> EpSiteKind
)
{
    // (prefix, suffix) wrapping a node's name when it is an entry point — "▶ kind " / "  ⟦svc⟧";
    // ("", "") when the node is not a rule-detected entry point.
    public (string Prefix, string Suffix) ChipFor(string symbolId)
    {
        if (!SiteById.TryGetValue(symbolId, out var loc) || loc.File is null)
            return ("", "");
        if (!EpSiteKind.TryGetValue((loc.File, loc.Line), out var ep))
            return ("", "");
        return EntryPointRenderer.NodeChip(Deployments, ep.Kind, loc.File, ep.Requires);
    }

    // A header suffix for a from-symbol (reaches/path/callers roots): "▶ kind  ⟦svc⟧" when it is an
    // entry point, else just the "⟦svc⟧" deployment chip, else "" when the symbol has no known site.
    public string HeaderTag(string symbolId)
    {
        if (!SiteById.TryGetValue(symbolId, out var loc) || loc.File is null)
            return "";
        if (EpSiteKind.TryGetValue((loc.File, loc.Line), out var ep))
            return $"{EntryPointRenderer.Marker} {ep.Kind}  {EntryPointRenderer.DeployTag(Deployments, loc.File, ep.Requires)}";
        return EntryPointRenderer.DeployTag(Deployments, loc.File);
    }
}

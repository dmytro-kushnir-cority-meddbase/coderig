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

    // The deployment chip for an EP at filePath: "⟦MedDBase (iis)⟧", "⟦3 svcs: A (iis), B, C⟧",
    // or "⟦no service⟧" when the owning project is in no service closure (tests/tools).
    public static string DeployTag(DeploymentMap deployments, string? filePath, int cap = 3)
    {
        var svcs = deployments.ServicesForFile(filePath);
        if (svcs.Count == 0)
            return "⟦no service⟧";

        var shown = string.Join(", ", svcs.Take(cap).Select(s => FormatService(deployments, s)));
        var overflow = svcs.Count > cap ? $" +{svcs.Count - cap}" : "";
        // Lead with the count when an EP fans out to multiple services so it reads as "this runs in N hosts".
        var lead = svcs.Count > 1 ? $"{svcs.Count} svcs: " : "";
        return $"⟦{lead}{shown}{overflow}⟧";
    }

    private static string FormatService(DeploymentMap deployments, string name)
    {
        var kind = deployments.Service(name)?.Kind;
        return kind is null ? name : $"{name} ({kind})";
    }

    // The inline EP marker for a tree node / header: "▶ echoactor " (prefix) + " ⟦…⟧" (suffix),
    // so a node renders as "▶ echoactor SomeHandler …  ⟦MedDBase⟧". Empty strings when not an EP.
    public static (string Prefix, string Suffix) NodeChip(DeploymentMap deployments, string? kind, string? filePath)
    {
        if (kind is null)
            return ("", "");
        return ($"{Marker} {kind} ", $"  {DeployTag(deployments, filePath)}");
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
        int kindPad = 10
    )
    {
        output.WriteLine($"{Marker} {kind.PadRight(kindPad)} {route}  {DeployTag(deployments, filePath)}");
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
    IReadOnlyDictionary<(string File, int Line), string> EpSiteKind
)
{
    // (prefix, suffix) wrapping a node's name when it is an entry point — "▶ kind " / "  ⟦svc⟧";
    // ("", "") when the node is not a rule-detected entry point.
    public (string Prefix, string Suffix) ChipFor(string symbolId)
    {
        if (!SiteById.TryGetValue(symbolId, out var loc) || loc.File is null)
            return ("", "");
        if (!EpSiteKind.TryGetValue((loc.File, loc.Line), out var kind))
            return ("", "");
        return EntryPointRenderer.NodeChip(Deployments, kind, loc.File);
    }

    // A header suffix for a from-symbol (reaches/path/callers roots): "▶ kind  ⟦svc⟧" when it is an
    // entry point, else just the "⟦svc⟧" deployment chip, else "" when the symbol has no known site.
    public string HeaderTag(string symbolId)
    {
        if (!SiteById.TryGetValue(symbolId, out var loc) || loc.File is null)
            return "";
        var deployTag = EntryPointRenderer.DeployTag(Deployments, loc.File);
        return EpSiteKind.TryGetValue((loc.File, loc.Line), out var kind) ? $"{EntryPointRenderer.Marker} {kind}  {deployTag}" : deployTag;
    }
}

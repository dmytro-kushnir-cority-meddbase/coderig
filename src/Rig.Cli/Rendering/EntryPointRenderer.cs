using Rig.Cli.Deployments;

namespace Rig.Cli.Rendering;

// The ONE place entry points are rendered, so every command (derive, callers, tree, reaches, path) shows
// them identically: a ▶ marker, the EP kind, the route, and a deployment chip naming the service(s) whose
// process loads the EP. An EP in several services renders as "⟦N svcs: a, b, c +k⟧" so the fan-out is explicit.
internal static class EntryPointRenderer
{
    public const string Marker = "▶";

    // The deployment chip for an EP at filePath, showing the services it is ACTIVE-IN (loaded AND
    // capability-gated in). When the EP's rule `requires` tokens some loaded services don't `provide`,
    // those drop from the active set and surface as a "· N linked-inactive" delta, keeping the
    // "loaded here, doesn't run here" signal visible. `requires` null/empty makes active == loaded.
    public static string DeployTag(DeploymentMap deployments, string? filePath, IReadOnlyList<string>? requires = null, int cap = 3)
    {
        var loaded = deployments.ServicesForFile(filePath);
        if (loaded.Count == 0)
        {
            return "⟦no service⟧";
        }

        var active = deployments.ActiveServices(loadedServices: loaded, requires: requires);
        var inactive = loaded.Count - active.Count;
        if (active.Count == 0)
        // Linked into hosts but gated out of all of them — the "runs here? no, anywhere" signal.
        {
            return $"⟦0 active · {inactive} linked-inactive⟧";
        }

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

    public static (string Prefix, string Suffix) NodeChip(
        DeploymentMap deployments,
        string? kind,
        string? filePath,
        IReadOnlyList<string>? requires = null
    )
    {
        if (kind is null)
        {
            return ("", "");
        }

        return ($"{Marker} {kind} ", $"  {DeployTag(deployments, filePath, requires)}");
    }
}

// Per-render lookup so a call-tree node can be marked when it IS itself an entry point: maps a node's
// SymbolId to its declaration site, and a site (File, Line) to its EP kind AND capability requirements
// (so a node/header computes active-in, not just loaded-in, right where it renders). Built once per tree
// render, only when deployments are configured, then threaded through RenderTreeNode.
internal sealed record EpRenderContext(
    DeploymentMap Deployments,
    IReadOnlyDictionary<string, (string? File, int Line)> SiteById,
    IReadOnlyDictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)> EpSiteKind
)
{
    public (string Prefix, string Suffix) ChipFor(string symbolId)
    {
        if (!SiteById.TryGetValue(symbolId, out var loc) || loc.File is null)
        {
            return ("", "");
        }

        if (!EpSiteKind.TryGetValue((loc.File, loc.Line), out var ep))
        {
            return ("", "");
        }

        return EntryPointRenderer.NodeChip(Deployments, kind: ep.Kind, filePath: loc.File, requires: ep.Requires);
    }

    public string HeaderTag(string symbolId)
    {
        if (!SiteById.TryGetValue(symbolId, out var loc) || loc.File is null)
        {
            return "";
        }

        if (EpSiteKind.TryGetValue((loc.File, loc.Line), out var ep))
        {
            return $"{EntryPointRenderer.Marker} {ep.Kind}  {EntryPointRenderer.DeployTag(Deployments, loc.File, ep.Requires)}";
        }

        return EntryPointRenderer.DeployTag(Deployments, loc.File);
    }
}

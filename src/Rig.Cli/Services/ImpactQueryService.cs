using Rig.Cli.Caching;
using Rig.Cli.CommandLine;
using Rig.Cli.Commands;
using Rig.Cli.Impact;
using static Rig.Cli.Graph.TraversalGraphLoader;

namespace Rig.Cli.Services;

// Store-vs-store impact diff for the web /api/impact endpoint — the SAME artifact `rig impact` renders,
// via ImpactEngine.DiffAsync (the single compute path shared by CLI + web). Internal (not public like the
// other services) because the artifact/diff types are internal to Rig.Cli.Impact; the Web mapper — same
// assembly — projects them to a JSON DTO.
internal static class ImpactQueryService
{
    public static async Task<ImpactCacheArtifact> DiffAsync(
        string workingDirectory,
        string baseRef,
        string headRef,
        bool async = false,
        bool includeDelivery = false,
        bool gate = true,
        IReadOnlyList<string>? extraRules = null,
        Func<string, long, Task>? onPhase = null
    )
    {
        var ws = new WorkspaceLocation(workingDirectory, headRef);
        await using var context = await OpenReadContextGatedAsync(ws with { StoreRef = headRef });
        var mode = CommonOptions.Mode(async: async, includeDelivery: includeDelivery);
        return await ImpactEngine.DiffAsync(
            headContext: context,
            ws: ws,
            baseRef: baseRef,
            headRef: headRef,
            mode: mode,
            gate: gate,
            noCache: false,
            extraRules: extraRules ?? [],
            onPhase: onPhase
        );
    }
}

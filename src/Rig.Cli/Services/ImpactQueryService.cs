using Rig.Analysis;
using Rig.Cli.Caching;
using Rig.Cli.CommandLine;
using Rig.Cli.Commands;
using Rig.Cli.Impact;
using Rig.Cli.Telemetry;
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

    // The per-run resource telemetry (CPU/mem/disk over time + per-phase labels) for a store-vs-store diff, as
    // a rig-*-telemetry.csv string the telemetry dashboard renders. Runs the diff with noCache: true so there
    // is REAL cold work to sample — a warm cache hit does no work and would yield an empty profile. Heavier
    // than the plain diff (a full recompute), so it backs an explicit "load graphs" action, not the diff view.
    public static async Task<string> TelemetryCsvAsync(string workingDirectory, string baseRef, string headRef, bool async = false)
    {
        var ws = new WorkspaceLocation(workingDirectory, headRef);
        await using var context = await OpenReadContextGatedAsync(ws with { StoreRef = headRef });
        var mode = CommonOptions.Mode(async: async, includeDelivery: false);
        var timings = new PhaseTimings();
        timings.StartSampling();
        await ImpactEngine.DiffAsync(
            headContext: context,
            ws: ws,
            baseRef: baseRef,
            headRef: headRef,
            mode: mode,
            gate: true,
            noCache: true,
            extraRules: [],
            onPhase: (name, ms) =>
            {
                timings.Record(name, TimeSpan.FromMilliseconds(ms));
                return Task.CompletedTask;
            }
        );
        var samples = timings.StopSampling();
        return TimingReport.BuildCsv(timings, samples);
    }
}

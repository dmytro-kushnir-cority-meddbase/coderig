using Rig.Cli.Deployments;
using Rig.Cli.EntryPoints;
using Rig.Domain.Data;
using Rig.Storage.Storage;

namespace Rig.Cli.Services;

// A deployed service that owns (loads) a piece of code: its name and optional deployment kind (e.g. "iis",
// "kube"). The wire/render-friendly shape of a DeploymentMap ServiceDef, minus the capability tokens.
// Public: surfaced on the public CallersQueryService result + the web DTOs (owning-service chip).
public sealed record ServiceRef(string Name, string? Kind);

// Reusable, load-once wrapper over the deployment-attribution engine (Deployments/DeploymentMap +
// EntryPoints/EntryPointContext) so consumers OTHER than the CLI renderers — the in-process web host, JSON
// DTO mappers — can answer "which deployed service(s) load this code?" without touching the rendering layer
// or duplicating the deployments.json → ProjectReference-closure algorithm.
//
// The underlying attribution is FILE-BASED: DeploymentMap inverts each service's entry-csproj transitive
// <ProjectReference> closure into a project→service index, then attributes a source file to its most specific
// project by longest owning-directory prefix. There is NO symbol→project table in the store, so a bare
// SymbolId (DocID) alone cannot be attributed — resolve it to its declaration File first (the CLI does this
// via EpRenderContext.SiteById; the web already has TreeQueryService.SymbolLocation.File per node) and pass
// the file path here. A DocID passed to ServicesFor returns empty (it fails Path.GetFullPath / prefix match,
// handled safely inside DeploymentMap.ServicesForFile).
//
// Membership is "this code is LOADED in service X" — a compile-time UPPER BOUND. For capability-gated EPs
// (actor/background handlers whose rule `requires` a token), the ACTIVE-IN subset is narrower; use
// ActiveServicesFor with the EP's requirements to compute it. Ungated code (no requirements) is active
// wherever it is loaded.
internal sealed class DeploymentAttributionLookup
{
    public static readonly DeploymentAttributionLookup Empty = new(DeploymentMap.Empty);

    private readonly DeploymentMap _map;

    internal DeploymentAttributionLookup(DeploymentMap map) => _map = map;

    // True when no deployments.json was configured / resolvable — every lookup returns empty, so a consumer
    // can cheaply omit the "services" field entirely rather than emit empty arrays everywhere.
    public bool IsEmpty => _map.IsEmpty;

    // The raw underlying map, for advanced callers that need ServiceDef capability tokens or the CLI chip
    // renderers (EntryPointRenderer.DeployTag / EpRenderContext) which already take a DeploymentMap.
    public DeploymentMap Map => _map;

    // Load the attribution once for a store's working directory. Mirrors EntryPointContext.LoadDeploymentsAsync
    // exactly (short-circuits before any EF query when deployments.json is absent; resolves the primary /
    // max-symbol solution the host csproj paths are relative to), so behaviour is identical to what the CLI
    // commands load. `log` (optional) surfaces config problems — missing host project, JSON parse failure.
    // Call ONCE per request/invocation and reuse the instance for every node.
    public static async Task<DeploymentAttributionLookup> LoadAsync(
        RigDbContext context,
        string workingDirectory,
        TextWriter? log = null
    ) => new(await EntryPointContext.LoadDeploymentsAsync(context, workingDirectory, log));

    // Owning service name(s) for a source FILE PATH. `symbolIdOrFilePath` is treated as a file path; a bare
    // SymbolId (DocID) yields an empty list — resolve it to its declaration file first (see class remarks).
    // Empty when the file is outside every service's ProjectReference closure ("no service"), or when
    // attribution is unconfigured (IsEmpty).
    public IReadOnlyList<string> ServicesFor(string? symbolIdOrFilePath) => _map.ServicesForFile(symbolIdOrFilePath);

    // Owning service(s) WITH their deployment kind for a source file path — the shape a UI/DTO wants
    // ("MedDBase" / kind "iis"). Same file-based, loaded-in semantics as ServicesFor.
    public IReadOnlyList<ServiceRef> ServicesWithKindFor(string? filePath)
    {
        var names = _map.ServicesForFile(filePath);
        if (names.Count == 0)
        {
            return [];
        }

        var result = new List<ServiceRef>(names.Count);
        foreach (var name in names)
        {
            result.Add(new ServiceRef(name, _map.Service(name)?.Kind));
        }

        return result;
    }

    // The ACTIVE-IN subset for a capability-gated entry point: the file's loaded services intersected with the
    // EP rule's `requires` tokens (ANY semantics). When `requires` is null/empty the EP is ungated and this
    // collapses to the full loaded set. Use for entry-point lists where the EP's requirements are known (the
    // web's EP-site map carries them as (Kind, Requires)); use ServicesFor/ServicesWithKindFor for plain
    // call-tree nodes where only loaded-in is meaningful.
    public IReadOnlyList<ServiceRef> ActiveServicesFor(string? filePath, IReadOnlyList<string>? requires)
    {
        var loaded = _map.ServicesForFile(filePath);
        if (loaded.Count == 0)
        {
            return [];
        }

        var active = _map.ActiveServices(loadedServices: loaded, requires: requires);
        if (active.Count == 0)
        {
            return [];
        }

        var result = new List<ServiceRef>(active.Count);
        foreach (var name in active)
        {
            result.Add(new ServiceRef(name, _map.Service(name)?.Kind));
        }

        return result;
    }
}

using Rig.Cli.Services; // ServiceRef (owning-service chip)

namespace Rig.Cli.Web;

// JSON contracts for /api/callers — the web equivalent of `rig callers <to>`. Deliberately flat, camelCased
// by the ASP.NET default serializer, and decoupled from the internal Rig.Domain / CallersQueryService types
// so the wire shape can evolve independently of the engine — same convention as /api/tree's TreeNodeDto/
// TreeResponseDto and /api/path's PathNodeDto/PathResponseDto (see WebContracts.cs, PathContracts.cs).
//
// mode=roots deliberately does NOT reuse TreeMapper/TreeNodeDto (see CallersEndpoint.cs and the note on
// CallersQueryService.BuildRoots): the reverse-reachability domain layer (FactPathFinder.EntryRootsReaching/
// ReachedBy) has no parent-linked TREE builder — only BuildTree (which walks FORWARD Successors) does. The
// reverse walk (Predecessors/BuildReverseMaps) only ever produces flat depth-maps/root-lists, never
// parent-linked nodes, so CallerRootDto is FLAT — mirroring how `rig callers --roots` itself renders, and
// ReachesResponseDto's precedent (ReachesContracts.cs) of a flat shape backed by a flat domain computation.
//
// mode=entrypoints reuses the SAME Kind/Route/Fqn/File/Line shape /api/entrypoints already exposes
// (EntryPointService.EntryPointView) — EntryPointDto mirrors it 1:1 rather than inventing new fields.

internal sealed record CallerRootDto(
    string Id, // SymbolId (DocID) — same identity TreeNodeDto.Id / PathNodeDto.Id use.
    string Name, // ShortName — display label.
    // false = reverse-only: in the reverse closure but with NO confirmed forward path back to the target (a
    // reverse-dispatch over-approximation — see CallersCommand's --include-reverse-only). Always true under
    // ?raw=true.
    bool ForwardConfirmed
);

internal sealed record CallersRootsResponseDto(
    string To,
    // false = no root callers (no-predecessor origins) reach `To`, or no symbol matches it.
    bool Matched,
    IReadOnlyList<CallerRootDto> Roots
);

internal sealed record EntryPointDto(
    string Kind,
    string Route,
    // The queryable, fully-qualified dotted name — round-trips straight into `?from=` on /api/tree,
    // /api/reaches, /api/path, /api/callers. Falls back to Route when the site maps to no indexed method.
    string Fqn,
    string? File,
    int Line,
    // Owning deployed service(s) for this EP's site (loaded-in; from deployments.json). Empty when
    // deployments.json is absent. Serializes as services:[{name,kind}] — the "which services can trigger this".
    IReadOnlyList<ServiceRef> Services
);

internal sealed record CallersEntryPointsResponseDto(
    string To,
    // false = no rule-detected entry point reaches `To` (synchronously; see the CLI's --async hint), or no
    // symbol matches it.
    bool Matched,
    IReadOnlyList<EntryPointDto> EntryPoints
);

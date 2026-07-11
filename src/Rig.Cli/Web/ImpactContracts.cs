namespace Rig.Cli.Web;

// JSON contracts for /api/impact — a flat projection of ImpactCommand's internal diff. The MVP headline is
// the per-EP behavioral delta (PerEp): which entry points gained/lost which effects (and hazards) between two
// commits. Entry-point add/remove and the structural affected-EP COUNT ride along; the (large) structural
// affected-EP list is summarized as a count for now.

internal sealed record ImpactProvenanceDto(string? Branch, string? Commit, string Label);

internal sealed record ImpactEffectDto(string Provider, string Operation, string Resource, string Enclosing);

internal sealed record ImpactHazardDto(string Type, string Cell, string Enclosing, string Confidence);

internal sealed record ImpactEpDeltaDto(
    string Kind,
    string Route,
    string Fqn, // queryable dotted name — round-trips into the tree view
    string? File,
    int Line,
    int BaseEffects,
    int BranchEffects,
    IReadOnlyList<ImpactEffectDto> Added,
    IReadOnlyList<ImpactEffectDto> Removed,
    IReadOnlyList<ImpactHazardDto> HazardsAdded,
    IReadOnlyList<ImpactHazardDto> HazardsRemoved,
    bool SharedMutationOnPath
);

internal sealed record ImpactKindRouteDto(string Kind, string Route);

// Per-EP STRUCTURAL reach delta (from Impact.EpReachDelta): the methods newly reachable in head
// (Added — DocIDs, matched to head-tree node ids to tint newly-reached nodes) and no longer reachable
// (Removed — base-only, shown as a list since they're absent from the head tree).
internal sealed record ImpactReachNodeDto(string Id, string Name);

internal sealed record ImpactReachDto(IReadOnlyList<ImpactReachNodeDto> Added, IReadOnlyList<ImpactReachNodeDto> Removed);

internal sealed record ImpactResponseDto(
    ImpactProvenanceDto Base,
    ImpactProvenanceDto Head,
    IReadOnlyList<ImpactKindRouteDto> AddedEps,
    IReadOnlyList<ImpactKindRouteDto> RemovedEps,
    int AffectedEpCount, // structural: EPs whose reachable tree changed (behavioral subset is PerEp)
    IReadOnlyList<ImpactEpDeltaDto> PerEp
);

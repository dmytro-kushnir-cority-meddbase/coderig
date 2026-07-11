using Rig.Cli.Caching;
using Rig.Cli.Commands;
using Rig.Cli.Impact;

namespace Rig.Cli.Web;

// Projects the internal ImpactCacheArtifact (Rig.Cli.Impact's diff types) to the flat /api/impact JSON DTO.
// Pure projection — reuses ImpactEngine.FqnForCard so each EP carries the same queryable dotted name the
// CLI's affected-EP cards show.
internal static class ImpactMapper
{
    public static ImpactResponseDto ToResponse(ImpactCacheArtifact art)
    {
        var d = art.Diff;
        var sites = art.FqnSites;
        return new ImpactResponseDto(
            Base: Prov(art.BaseProvenance),
            Head: Prov(art.HeadProvenance),
            AddedEps: d.Ep?.Added.Select(a => new ImpactKindRouteDto(Kind: a.Kind, Route: a.Route)).ToList() ?? [],
            RemovedEps: d.Ep?.Removed.Select(a => new ImpactKindRouteDto(Kind: a.Kind, Route: a.Route)).ToList() ?? [],
            AffectedEpCount: d.AffectedEps.Count,
            PerEp: d.PerEp.Select(p => new ImpactEpDeltaDto(
                    Kind: p.Kind,
                    Route: p.Route,
                    Fqn: ImpactEngine.FqnForCard(route: p.Route, filePath: p.FilePath, line: p.Line, idBySite: sites),
                    File: string.IsNullOrEmpty(p.FilePath) ? null : p.FilePath,
                    Line: p.Line,
                    BaseEffects: p.BaseEffects,
                    BranchEffects: p.BranchEffects,
                    Added: p.Added.Select(e => new ImpactEffectDto(
                            Provider: e.Provider,
                            Operation: e.Operation,
                            Resource: e.Resource,
                            Enclosing: e.Enclosing
                        ))
                        .ToList(),
                    Removed: p.Removed.Select(e => new ImpactEffectDto(
                            Provider: e.Provider,
                            Operation: e.Operation,
                            Resource: e.Resource,
                            Enclosing: e.Enclosing
                        ))
                        .ToList(),
                    HazardsAdded: p.HazardsAddedOrEmpty.Select(hz => new ImpactHazardDto(
                            Type: hz.Type,
                            Cell: hz.Cell,
                            Enclosing: hz.Enclosing,
                            Confidence: hz.Confidence
                        ))
                        .ToList(),
                    HazardsRemoved: p.HazardsRemovedOrEmpty.Select(hz => new ImpactHazardDto(
                            Type: hz.Type,
                            Cell: hz.Cell,
                            Enclosing: hz.Enclosing,
                            Confidence: hz.Confidence
                        ))
                        .ToList(),
                    SharedMutationOnPath: p.SharedMutationOnPath
                ))
                .ToList()
        );
    }

    private static ImpactProvenanceDto Prov(StoreProvenance p) =>
        new(Branch: p.Branch, Commit: p.ShortCommit, Label: p.ShortCommit is null ? p.Fallback : $"{p.Branch ?? "?"} ({p.ShortCommit})");
}

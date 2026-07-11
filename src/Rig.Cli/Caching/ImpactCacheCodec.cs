using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Cli.Commands;
using Rig.Cli.Impact;

namespace Rig.Cli.Caching;

// What `rig impact` computes and caches: the PROVEN two-store diff (entry-point set diff + per-EP
// behavioral/structural deltas) plus everything its RENDER needs that would otherwise force a store load —
// the two sides' provenance and the FQN of each affected EP's site. Today every `rig impact` invocation
// recomputes the whole thing: it loads BOTH per-commit stores, shapes both graphs, and forward-reaches
// every entry point at unbounded depth on each side — even when the user re-runs the SAME (base, head)
// pair differing only in render flags (--structural / --format / --limit). Both stores are IMMUTABLE
// (per-commit), so the diff is a pure function of (baseStoreKey, headStoreKey, rulesHash, mode) — caching
// it lets a repeat run skip the base-store load and BOTH reach computations entirely and render from the
// blob (a cheap deployment-map read aside).
//
// The artifact is FULLY MATERIALIZED so a hit never touches the call graph or either store's symbol table:
// the per-EP FQN labels (which the human cards + tsv round-trip into `rig tree`) are resolved at compute
// time for the diff's sites and stored here, so FqnForCard needs no idBySite on the warm path. Stored
// UNTRUNCATED — --limit is applied at render, so it doesn't fragment the cache.
internal sealed record ImpactCacheArtifact(
    ImpactDiff Diff,
    StoreProvenance BaseProvenance,
    StoreProvenance HeadProvenance,
    // (FilePath, Line) -> method DocID for every site referenced by the diff (FqnForCard input). Restricted
    // to the diff's sites, not the whole store, so the blob stays small. Concrete Dictionary so it drops
    // straight into FqnForCard's parameter on the warm render path.
    Dictionary<(string File, int Line), string> FqnSites
);

// Serializes via System.Text.Json (source-generated — AOT-safe, no reflection) over FLAT DTO records (no
// ValueTuples, which STJ serializes as fieldless `{}` unless IncludeFields is set) and GZips the UTF-8
// bytes — the same shape as TreeCacheCodec. Decode returns null on any corruption / schema drift, so a bad
// or stale blob is a cache MISS (recompute), never a command failure.
internal static class ImpactCacheCodec
{
    private static readonly ImpactCacheJsonContext Context = new(
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
    );

    public static byte[] Encode(
        ImpactDiff diff,
        StoreProvenance baseProvenance,
        StoreProvenance headProvenance,
        IReadOnlyDictionary<(string File, int Line), string> idBySite
    )
    {
        // Resolve the FQN site subset: every (FilePath, Line) the affected/behavioral EP cards render, kept
        // only when the store has a method symbol there (synthesized/promoted EPs map to no site → omitted,
        // and FqnForCard falls back to the route exactly as on the cold path).
        var fqnSites = new List<SiteFqnDto>();
        var seen = new HashSet<(string, int)>();
        void Collect(string filePath, int line)
        {
            if (string.IsNullOrEmpty(filePath) || !seen.Add((filePath, line)))
            {
                return;
            }

            if (idBySite.TryGetValue((filePath, line), out var docId))
            {
                fqnSites.Add(new SiteFqnDto(File: filePath, Line: line, DocId: docId));
            }
        }

        foreach (var d in diff.AffectedEps)
        {
            Collect(d.FilePath, d.Line);
        }

        foreach (var d in diff.PerEp)
        {
            Collect(d.FilePath, d.Line);
        }

        var payload = new ImpactCachePayload(
            Ep: diff.Ep is null ? null : new EpDiffDto(Added: MapKindRoutes(diff.Ep.Added), Removed: MapKindRoutes(diff.Ep.Removed)),
            AffectedEps: diff.AffectedEps.Select(MapReach).ToArray(),
            PerEp: diff.PerEp.Select(MapFootprint).ToArray(),
            BaseProvenance: MapProv(baseProvenance),
            HeadProvenance: MapProv(headProvenance),
            FqnSites: fqnSites
        );

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Context.ImpactCachePayload);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(json, offset: 0, count: json.Length);
        }

        return output.ToArray();
    }

    public static ImpactCacheArtifact? Decode(byte[] blob)
    {
        try
        {
            using var input = new MemoryStream(blob);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var json = new MemoryStream();
            gzip.CopyTo(json);
            json.Position = 0;
            var payload = JsonSerializer.Deserialize(json, Context.ImpactCachePayload);
            if (payload is null)
            {
                return null;
            }

            var diff = new ImpactDiff(
                Ep: payload.Ep is null
                    ? null
                    : new EpDiff(Added: UnmapKindRoutes(payload.Ep.Added), Removed: UnmapKindRoutes(payload.Ep.Removed)),
                AffectedEps: payload.AffectedEps.Select(UnmapReach).ToArray(),
                PerEp: payload.PerEp.Select(UnmapFootprint).ToArray()
            );

            var fqnSites = new Dictionary<(string File, int Line), string>();
            foreach (var s in payload.FqnSites)
            {
                fqnSites[(s.File, s.Line)] = s.DocId;
            }

            return new ImpactCacheArtifact(
                Diff: diff,
                BaseProvenance: UnmapProv(payload.BaseProvenance),
                HeadProvenance: UnmapProv(payload.HeadProvenance),
                FqnSites: fqnSites
            );
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static ProvenanceDto MapProv(StoreProvenance p) => new(Branch: p.Branch, ShortCommit: p.ShortCommit, Fallback: p.Fallback);

    private static StoreProvenance UnmapProv(ProvenanceDto p) => new(Branch: p.Branch, ShortCommit: p.ShortCommit, Fallback: p.Fallback);

    private static List<KindRouteDto> MapKindRoutes(IReadOnlyList<(string Kind, string Route)> items) =>
        items.Select(kr => new KindRouteDto(Kind: kr.Kind, Route: kr.Route)).ToList();

    private static List<(string Kind, string Route)> UnmapKindRoutes(IReadOnlyList<KindRouteDto> items) =>
        items.Select(kr => (kr.Kind, kr.Route)).ToList();

    private static ReachDeltaDto MapReach(EpReachDelta d) =>
        new(
            Kind: d.Kind,
            Route: d.Route,
            FilePath: d.FilePath,
            Line: d.Line,
            Requires: d.Requires,
            Added: d.Added,
            Removed: d.Removed,
            AddedStems: d.AddedStems,
            RemovedStems: d.RemovedStems,
            ChangedStems: d.ChangedStems,
            DistinctStemDelta: d.DistinctStemDelta,
            InPlaceCount: d.InPlaceCount,
            InPlace: d.InPlace
        );

    private static EpReachDelta UnmapReach(ReachDeltaDto d) =>
        new(
            Kind: d.Kind,
            Route: d.Route,
            FilePath: d.FilePath,
            Line: d.Line,
            Requires: d.Requires,
            Added: d.Added,
            Removed: d.Removed,
            AddedStems: d.AddedStems,
            RemovedStems: d.RemovedStems,
            ChangedStems: d.ChangedStems,
            DistinctStemDelta: d.DistinctStemDelta,
            InPlaceCount: d.InPlaceCount,
            InPlace: d.InPlace
        );

    private static FootprintDeltaDto MapFootprint(EpFootprintDelta d) =>
        new(
            Kind: d.Kind,
            Route: d.Route,
            FilePath: d.FilePath,
            Line: d.Line,
            BranchEffects: d.BranchEffects,
            BaseEffects: d.BaseEffects,
            Added: d.Added.Select(MapEffectKey).ToArray(),
            Removed: d.Removed.Select(MapEffectKey).ToArray(),
            Amplified: d.Amplified.Select(MapAmplified).ToArray(),
            SharedMutationOnPath: d.SharedMutationOnPath,
            HazardsAdded: d.HazardsAddedOrEmpty.Select(MapHazard).ToArray(),
            HazardsRemoved: d.HazardsRemovedOrEmpty.Select(MapHazard).ToArray()
        );

    private static EpFootprintDelta UnmapFootprint(FootprintDeltaDto d) =>
        new(
            Kind: d.Kind,
            Route: d.Route,
            FilePath: d.FilePath,
            Line: d.Line,
            BranchEffects: d.BranchEffects,
            BaseEffects: d.BaseEffects,
            Added: d.Added.Select(UnmapEffectKey).ToArray(),
            Removed: d.Removed.Select(UnmapEffectKey).ToArray(),
            Amplified: d.Amplified.Select(UnmapAmplified).ToArray(),
            SharedMutationOnPath: d.SharedMutationOnPath,
            // Defaulted null on an OLD blob (pre-hazard schema) — UnmapHazards maps null -> empty so the warm
            // path's delta is byte-identical to a cold recompute that found no hazard change.
            HazardsAdded: (d.HazardsAdded ?? []).Select(UnmapHazard).ToArray(),
            HazardsRemoved: (d.HazardsRemoved ?? []).Select(UnmapHazard).ToArray()
        );

    private static HazardFindingDto MapHazard(HazardFinding h) =>
        new(Type: h.Type, Cell: h.Cell, Enclosing: h.Enclosing, Confidence: h.Confidence);

    private static HazardFinding UnmapHazard(HazardFindingDto h) =>
        new(Type: h.Type, Cell: h.Cell, Enclosing: h.Enclosing, Confidence: h.Confidence);

    private static EffectKeyDto MapEffectKey((string Provider, string Operation, string Resource, string Enclosing) k) =>
        new(Provider: k.Provider, Operation: k.Operation, Resource: k.Resource, Enclosing: k.Enclosing);

    private static (string Provider, string Operation, string Resource, string Enclosing) UnmapEffectKey(EffectKeyDto k) =>
        (k.Provider, k.Operation, k.Resource, k.Enclosing);

    private static AmplifiedDto MapAmplified(EpEffectAmplified a) =>
        new(
            Provider: a.Provider,
            Operation: a.Operation,
            Resource: a.Resource,
            Enclosing: a.Enclosing,
            BaseCount: a.BaseCount,
            BranchCount: a.BranchCount,
            BaseInLoop: a.BaseInLoop,
            BranchInLoop: a.BranchInLoop
        );

    private static EpEffectAmplified UnmapAmplified(AmplifiedDto a) =>
        new(
            Provider: a.Provider,
            Operation: a.Operation,
            Resource: a.Resource,
            Enclosing: a.Enclosing,
            BaseCount: a.BaseCount,
            BranchCount: a.BranchCount,
            BaseInLoop: a.BaseInLoop,
            BranchInLoop: a.BranchInLoop
        );
}

// The serializable wire shape — flat records mirroring ImpactCommand's internal diff types but with the
// ValueTuples lifted into named DTOs so System.Text.Json round-trips them by property.
internal sealed record ImpactCachePayload(
    EpDiffDto? Ep,
    IReadOnlyList<ReachDeltaDto> AffectedEps,
    IReadOnlyList<FootprintDeltaDto> PerEp,
    ProvenanceDto BaseProvenance,
    ProvenanceDto HeadProvenance,
    IReadOnlyList<SiteFqnDto> FqnSites
);

internal sealed record ProvenanceDto(string? Branch, string? ShortCommit, string Fallback);

internal sealed record KindRouteDto(string Kind, string Route);

internal sealed record EpDiffDto(IReadOnlyList<KindRouteDto> Added, IReadOnlyList<KindRouteDto> Removed);

internal sealed record EffectKeyDto(string Provider, string Operation, string Resource, string Enclosing);

internal sealed record AmplifiedDto(
    string Provider,
    string Operation,
    string Resource,
    string Enclosing,
    int BaseCount,
    int BranchCount,
    bool BaseInLoop,
    bool BranchInLoop
);

internal sealed record FootprintDeltaDto(
    string Kind,
    string Route,
    string FilePath,
    int Line,
    int BranchEffects,
    int BaseEffects,
    IReadOnlyList<EffectKeyDto> Added,
    IReadOnlyList<EffectKeyDto> Removed,
    IReadOnlyList<AmplifiedDto> Amplified,
    // FR-1e: round-tripped so the warm (cache-replayed) path renders the guard-delta callout byte-identically
    // to a cold recompute. Defaulted so an OLD blob (pre-FR-1e schema) still decodes — missing => false.
    bool SharedMutationOnPath = false,
    // HAZARD DELTA: the per-EP hazard findings gained/lost, round-tripped so the warm path renders the hazard
    // lines + headline byte-identically to a cold recompute. Defaulted null so an OLD blob (pre-hazard schema)
    // still decodes — missing => empty (UnmapFootprint maps null -> []).
    IReadOnlyList<HazardFindingDto>? HazardsAdded = null,
    IReadOnlyList<HazardFindingDto>? HazardsRemoved = null
);

internal sealed record HazardFindingDto(string Type, string Cell, string Enclosing, string Confidence);

internal sealed record ReachDeltaDto(
    string Kind,
    string Route,
    string FilePath,
    int Line,
    IReadOnlyList<string>? Requires,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> AddedStems,
    IReadOnlyList<string> RemovedStems,
    IReadOnlyList<string> ChangedStems,
    int DistinctStemDelta,
    int InPlaceCount,
    IReadOnlyList<string>? InPlace
);

internal sealed record SiteFqnDto(string File, int Line, string DocId);

[JsonSerializable(typeof(ImpactCachePayload))]
internal partial class ImpactCacheJsonContext : JsonSerializerContext { }

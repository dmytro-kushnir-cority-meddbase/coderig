using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Cli.Commands;

namespace Rig.Cli.Caching;

// What `tree --hazards` (and `derive`) needs from the GRAPH-TIER hazard sources: the cache_coherence +
// event_cycle + static_init_capture findings, materialized once per (store + rules) so a warm
// `tree --hazards` is a pure cache hit with NO shaped-graph load (the forward-closure correlation + cycle
// detection are the cost we must not pay per EP). These are NOT effect-attached — they have no owning
// DerivedEffect — so they live in their own cache namespace (GraphHazardFindingsCacheKey), DISTINCT from the
// effect-attached hazard-augmented effect set (HazardEffectsCacheKey).
//
// Serializes via System.Text.Json (source-generated — AOT-safe, no reflection) over a flat DTO mirroring
// DeriveCommand.HazardFinding, and GZips the UTF-8 bytes — the same shape as ImpactCacheCodec /
// TreeCacheCodec. Decode returns null on any corruption / schema drift, so a bad or stale blob is a cache
// MISS (recompute), never a command failure.
internal static class GraphHazardFindingsCodec
{
    private static readonly GraphHazardFindingsJsonContext Context = new(
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
    );

    public static byte[] Encode(IReadOnlyList<DeriveCommand.HazardFinding> findings)
    {
        var payload = new GraphHazardFindingsPayload(findings.Select(Map).ToArray());
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Context.GraphHazardFindingsPayload);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(json, offset: 0, count: json.Length);
        }

        return output.ToArray();
    }

    // Null on corruption/schema drift → treated as a cache miss (recompute).
    public static IReadOnlyList<DeriveCommand.HazardFinding>? Decode(byte[] blob)
    {
        try
        {
            using var input = new MemoryStream(blob);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var json = new MemoryStream();
            gzip.CopyTo(json);
            json.Position = 0;
            var payload = JsonSerializer.Deserialize(json, Context.GraphHazardFindingsPayload);
            return payload?.Findings.Select(Unmap).ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static GraphHazardFindingDto Map(DeriveCommand.HazardFinding h) =>
        new(
            Type: h.Type,
            Confidence: h.Confidence,
            Reason: h.Reason,
            Context: h.Context,
            Detail: h.Detail,
            Enclosing: h.Enclosing,
            FilePath: h.FilePath,
            Line: h.Line
        );

    private static DeriveCommand.HazardFinding Unmap(GraphHazardFindingDto h) =>
        new(
            Type: h.Type,
            Confidence: h.Confidence,
            Reason: h.Reason,
            Context: h.Context,
            Detail: h.Detail,
            Enclosing: h.Enclosing,
            FilePath: h.FilePath,
            Line: h.Line
        );
}

// The serializable wire shape — one DTO per finding, mirroring DeriveCommand.HazardFinding by property so
// System.Text.Json round-trips it (the source record is internal to DeriveCommand; this is its flat twin).
internal sealed record GraphHazardFindingDto(
    string Type,
    string Confidence,
    string Reason,
    string Context,
    string Detail,
    string Enclosing,
    string FilePath,
    int Line
);

internal sealed record GraphHazardFindingsPayload(IReadOnlyList<GraphHazardFindingDto> Findings);

[JsonSerializable(typeof(GraphHazardFindingsPayload))]
internal partial class GraphHazardFindingsJsonContext : JsonSerializerContext { }

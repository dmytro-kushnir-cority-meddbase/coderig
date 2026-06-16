using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Domain.Data;

namespace Rig.Storage.Queries;

// What a `rig tree` query computes and caches: the call-tree forest AND the effects derived for it.
// Both are recomputed per query today — the forest by FactPathFinder.BuildTree, the effects by
// FactEffectDeriver.Derive (the ~3.8s dominant cost). Caching the pair lets a repeat query skip both
// and only re-load the (cheaper) graph needed to render. Effects are stored UNFILTERED; --only/--exclude
// are applied after the cache so they don't fragment the key.
public sealed record TreeCachePayload(IReadOnlyList<TraceNode> Forest, IReadOnlyList<DerivedEffect> Effects);

// Serializes the payload with System.Text.Json (source-generated — AOT-safe, no reflection) and GZips
// the UTF-8 bytes. No bespoke binary codec and no DocID interning: DocIDs repeat heavily within a forest,
// and deflate collapses that redundancy, so compression is "outsourced to zip" rather than hand-rolled.
public static class TreeCacheCodec
{
    // Call trees nest deep, and each tree level is TWO JSON levels (the TraceNode object + its Children
    // array), so the System.Text.Json default MaxDepth of 64 overflows at ~32 levels of call depth. Use a
    // context built with a generous MaxDepth (and the null-skipping default). Bounded by the traversal's
    // own --maxdepth, so this only needs to exceed any realistic call depth; deeper still throws and the
    // caller treats the cache write as best-effort (skips it).
    private static readonly TreeCacheJsonContext Context = new(
        new JsonSerializerOptions { MaxDepth = 4096, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
    );

    public static byte[] Encode(TreeCachePayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Context.TreeCachePayload);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(json, 0, json.Length);
        }

        return output.ToArray();
    }

    // Returns null on any corruption / schema drift, so a bad or stale blob is treated as a cache miss
    // (recompute) rather than failing the command.
    public static TreeCachePayload? Decode(byte[] blob)
    {
        try
        {
            using var input = new MemoryStream(blob);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var json = new MemoryStream();
            gzip.CopyTo(json);
            json.Position = 0;
            return JsonSerializer.Deserialize(json, Context.TreeCachePayload);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

// The derived entry-point set as a flat, serializable list: one (file, line) site -> its EP kind and
// capability requirements. This is the expensive, PATTERN-INDEPENDENT half of a tree/reaches/path/
// callers query's EP rendering (it derives the whole-store EP set + classifies every handoff), so it is
// cached once per (store + rules) and reused by every query. The pattern-dependent half (a symbol->site
// map from the bounded graph) is cheap and rebuilt fresh each query.
public sealed record EpSiteEntry(string File, int Line, string Kind, IReadOnlyList<string>? Requires);

public sealed record EpSiteCachePayload(IReadOnlyList<EpSiteEntry> Sites);

public static class EpSiteCacheCodec
{
    private static readonly TreeCacheJsonContext Context = new(
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
    );

    public static byte[] Encode(IReadOnlyDictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)> sites)
    {
        var payload = new EpSiteCachePayload(
            sites.Select(kv => new EpSiteEntry(kv.Key.File, kv.Key.Line, kv.Value.Kind, kv.Value.Requires)).ToArray()
        );
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Context.EpSiteCachePayload);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(json, 0, json.Length);
        }

        return output.ToArray();
    }

    // Null on corruption/schema drift → treated as a cache miss (recompute).
    public static IReadOnlyDictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>? Decode(byte[] blob)
    {
        try
        {
            using var input = new MemoryStream(blob);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var json = new MemoryStream();
            gzip.CopyTo(json);
            json.Position = 0;
            var payload = JsonSerializer.Deserialize(json, Context.EpSiteCachePayload);
            if (payload is null)
            {
                return null;
            }

            var map = new Dictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>();
            foreach (var s in payload.Sites)
            {
                map[(s.File, s.Line)] = (s.Kind, s.Requires);
            }

            return map;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

// The RENDER sidecar: everything a `rig tree` render needs from the bounded graph that ISN'T already in
// the forest/effects payload — so a forest cache hit can render WITHOUT reloading + shaping the graph (the
// ~5.4s warm-query floor). Two maps, both a pure function of the same (store + rules + pattern + depth +
// mode) the forest is keyed by, so they share the forest's invalidation:
//   - SeamEffects: collapse-hub DocID -> the formatted effect-union lines (ComputeSeamEffects output).
//   - Locations:   method DocID -> (file, line); serves BOTH the EP-chip site map and `--files` links.
// Stored next to the forest under a sibling key; written on the cold/miss render path (graph in hand),
// read on the warm path to skip the graph load entirely.
public sealed record LocationEntry(string Symbol, string? File, int Line);

public sealed record RenderSidecarPayload(Dictionary<string, string[]> SeamEffects, LocationEntry[] Locations);

public static class RenderSidecarCodec
{
    private static readonly TreeCacheJsonContext Context = new(
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
    );

    public static byte[] Encode(
        IReadOnlyDictionary<string, List<string>> seamEffects,
        IReadOnlyDictionary<string, (string? File, int Line)> locations
    )
    {
        var payload = new RenderSidecarPayload(
            seamEffects.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal),
            locations.Select(kv => new LocationEntry(kv.Key, kv.Value.File, kv.Value.Line)).ToArray()
        );
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Context.RenderSidecarPayload);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(json, 0, json.Length);
        }

        return output.ToArray();
    }

    // (seamEffects, locations) on success; null on corruption/schema drift → treated as a cache miss.
    public static (Dictionary<string, List<string>> SeamEffects, Dictionary<string, (string? File, int Line)> Locations)? Decode(
        byte[] blob
    )
    {
        try
        {
            using var input = new MemoryStream(blob);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var json = new MemoryStream();
            gzip.CopyTo(json);
            json.Position = 0;
            var payload = JsonSerializer.Deserialize(json, Context.RenderSidecarPayload);
            if (payload is null)
            {
                return null;
            }

            var seam = payload.SeamEffects.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
            var loc = new Dictionary<string, (string? File, int Line)>(StringComparer.Ordinal);
            foreach (var e in payload.Locations)
            {
                loc[e.Symbol] = (e.File, e.Line);
            }

            return (seam, loc);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

[JsonSerializable(typeof(TreeCachePayload))]
[JsonSerializable(typeof(EpSiteCachePayload))]
[JsonSerializable(typeof(RenderSidecarPayload))]
internal partial class TreeCacheJsonContext : JsonSerializerContext { }

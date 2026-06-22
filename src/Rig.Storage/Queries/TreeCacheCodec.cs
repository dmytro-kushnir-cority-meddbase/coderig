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
            gzip.Write(json, offset: 0, count: json.Length);
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
            sites
                .Select(kv => new EpSiteEntry(File: kv.Key.File, Line: kv.Key.Line, Kind: kv.Value.Kind, Requires: kv.Value.Requires))
                .ToArray()
        );
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Context.EpSiteCachePayload);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(json, offset: 0, count: json.Length);
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

// The RENDER sidecar data a `rig tree` render needs from the bounded graph that ISN'T in the forest/effects
// payload — so a forest cache hit renders WITHOUT reloading + shaping the graph. Split into TWO sibling
// cache entries by filter-dependence, so the filter-independent half isn't duplicated across --only/--exclude
// combos:
//   - Locations (method DocID -> (file, line); serves the EP-chip site map AND `--files` links) are
//     filter-INDEPENDENT — keyed by the forest key alone (`:loc`). LocationsCodec.
//   - SeamEffects (collapse-hub DocID -> formatted effect-union lines) are derived from the FILTERED effects
//     — keyed by the forest key + the filter signature (`:seam:<sig>`). SeamCodec.
// Both share the forest's (store+rules+pattern+depth+mode) invalidation. Written on the cold/miss render
// path (graph in hand), read on the warm path. Locations are also hazard-independent, so they cache even
// under --hazards (only the seam rollup is gated off there).
public sealed record LocationEntry(string Symbol, string? File, int Line);

public sealed record LocationsPayload(LocationEntry[] Locations);

public static class LocationsCodec
{
    private static readonly TreeCacheJsonContext Context = new(
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
    );

    public static byte[] Encode(IReadOnlyDictionary<string, (string? File, int Line)> locations)
    {
        var payload = new LocationsPayload(
            locations.Select(kv => new LocationEntry(Symbol: kv.Key, File: kv.Value.File, Line: kv.Value.Line)).ToArray()
        );
        return RenderCodecGzip.Zip(JsonSerializer.SerializeToUtf8Bytes(payload, Context.LocationsPayload));
    }

    public static Dictionary<string, (string? File, int Line)>? Decode(byte[] blob)
    {
        try
        {
            var payload = JsonSerializer.Deserialize(RenderCodecGzip.Unzip(blob), Context.LocationsPayload);
            if (payload is null)
            {
                return null;
            }

            var loc = new Dictionary<string, (string? File, int Line)>(StringComparer.Ordinal);
            foreach (var e in payload.Locations)
            {
                loc[e.Symbol] = (e.File, e.Line);
            }

            return loc;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

public sealed record SeamPayload(Dictionary<string, string[]> SeamEffects);

public static class SeamCodec
{
    private static readonly TreeCacheJsonContext Context = new(
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
    );

    public static byte[] Encode(IReadOnlyDictionary<string, List<string>> seamEffects) =>
        RenderCodecGzip.Zip(
            JsonSerializer.SerializeToUtf8Bytes(
                new SeamPayload(seamEffects.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal)),
                Context.SeamPayload
            )
        );

    public static Dictionary<string, List<string>>? Decode(byte[] blob)
    {
        try
        {
            var payload = JsonSerializer.Deserialize(RenderCodecGzip.Unzip(blob), Context.SeamPayload);
            return payload?.SeamEffects.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

// `tree --full`'s unresolved library-call sites — invocations to a referenced-assembly target that matched
// no effect rule, bounded to the rendered tree's methods. A pure function of the forest (the tree's method
// set), so cached under the forest key (`:libcalls`) and recomputed only when the forest itself changes —
// instead of re-querying reference_facts on every --full run.
public sealed record LibCallsPayload(IReadOnlyList<SymbolRef> Calls);

public static class LibCallsCodec
{
    private static readonly TreeCacheJsonContext Context = new(
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
    );

    public static byte[] Encode(IReadOnlyList<SymbolRef> calls) =>
        RenderCodecGzip.Zip(JsonSerializer.SerializeToUtf8Bytes(new LibCallsPayload(calls), Context.LibCallsPayload));

    public static IReadOnlyList<SymbolRef>? Decode(byte[] blob)
    {
        try
        {
            return JsonSerializer.Deserialize(RenderCodecGzip.Unzip(blob), Context.LibCallsPayload)?.Calls;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

// Shared GZip helpers for the render-data codecs (Locations/Seam/LibCalls) — same GZip-over-source-gen-JSON
// approach the forest/effect payloads inline; factored here since three small codecs share it.
file static class RenderCodecGzip
{
    public static byte[] Zip(byte[] json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(json, offset: 0, count: json.Length);
        }

        return output.ToArray();
    }

    public static MemoryStream Unzip(byte[] blob)
    {
        using var input = new MemoryStream(blob);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var json = new MemoryStream();
        gzip.CopyTo(json);
        json.Position = 0;
        return json;
    }
}

// The WHOLE-STORE hazard-augmented effect set (derive's effect computation), cached once per (store +
// rules) and shared by `derive` + `tree --hazards`. EP-independent and mode-independent — an effect is a
// per-method fact (see HazardEffectsCacheKey). Same GZip-over-source-gen-JSON approach as the forest payload.
public sealed record HazardEffectsPayload(IReadOnlyList<DerivedEffect> Effects);

public static class HazardEffectsCodec
{
    private static readonly TreeCacheJsonContext Context = new(
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
    );

    public static byte[] Encode(IReadOnlyList<DerivedEffect> effects)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new HazardEffectsPayload(effects), Context.HazardEffectsPayload);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(json, offset: 0, count: json.Length);
        }

        return output.ToArray();
    }

    // Null on corruption/schema drift → treated as a cache miss (recompute).
    public static IReadOnlyList<DerivedEffect>? Decode(byte[] blob)
    {
        try
        {
            using var input = new MemoryStream(blob);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var json = new MemoryStream();
            gzip.CopyTo(json);
            json.Position = 0;
            return JsonSerializer.Deserialize(json, Context.HazardEffectsPayload)?.Effects;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

[JsonSerializable(typeof(TreeCachePayload))]
[JsonSerializable(typeof(EpSiteCachePayload))]
[JsonSerializable(typeof(LocationsPayload))]
[JsonSerializable(typeof(SeamPayload))]
[JsonSerializable(typeof(LibCallsPayload))]
[JsonSerializable(typeof(HazardEffectsPayload))]
internal partial class TreeCacheJsonContext : JsonSerializerContext { }

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
    public static byte[] Encode(TreeCachePayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, TreeCacheJsonContext.Default.TreeCachePayload);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            gzip.Write(json, 0, json.Length);
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
            return JsonSerializer.Deserialize(json, TreeCacheJsonContext.Default.TreeCachePayload);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TreeCachePayload))]
internal partial class TreeCacheJsonContext : JsonSerializerContext { }

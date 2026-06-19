using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rig.Domain.Functions;

namespace Rig.Cli.Caching;

// The query cache's key derivation + the best-effort write wrapper. The keys embed a store identity
// (rig.db size+mtime) so any reindex auto-invalidates them, plus the effective rule fingerprint and the
// traversal parameters the cached artifact is a function of.
internal static class QueryCacheKeys
{
    // Identity of the current store for cache keying + invalidation: rig.db size + last-write time.
    // `rig index` publishes a fresh db (atomic rename → new mtime/size) and `rig graph` rewrites the
    // derived edge tables in place (mtime changes), so any reindex shifts this — old cache entries no
    // longer match and are purged. Missing db → a constant sentinel (cache simply never hits).
    internal static string StoreKey(string dbPath)
    {
        try
        {
            var info = new FileInfo(dbPath);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "absent";
        }
        catch (IOException)
        {
            return "absent";
        }
    }

    // Cache key for the pattern-INDEPENDENT EP-site map: store identity + rule fingerprint only (no
    // pattern, no traversal params), so a single derivation serves every query against the store.
    internal static string EpCacheKey(string storeKey, string rulesHash)
    {
        var material = $"ep|v1|{storeKey}|{rulesHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    // The cache key for a `rig tree` forest+effects artifact: everything the artifact is a function of —
    // the store identity, the effective rule fingerprint, and the traversal parameters. `v1` is the
    // payload-schema version (bump to ignore older blobs). Render-only flags (--files/--summary/--effects
    // and --only/--exclude) are deliberately absent: they don't change the forest or the unfiltered
    // effects, only how they're presented, so they must not fragment the cache.
    internal static string TreeCacheKey(
        string storeKey,
        string rulesHash,
        string fromPattern,
        int maxDepth,
        FactPathFinder.TraversalMode mode,
        bool raw
    )
    {
        var material = $"tree|v1|{storeKey}|{rulesHash}|{fromPattern}|{maxDepth}|{mode}|{raw}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    // A stable signature of the effect filters (--only/--exclude) for the render-sidecar key: sorted +
    // lowercased so token order/casing don't fragment it, empty in the common no-filter case. The seam
    // summaries in the sidecar are a function of the FILTERED effects, so two queries that differ only by
    // these flags must get distinct sidecars (the forest itself is filter-independent and is not affected).
    internal static string EffectFilterSignature(IReadOnlyCollection<string> only, IReadOnlyCollection<string> exclude)
    {
        var o = string.Join(',', only.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal));
        var e = string.Join(',', exclude.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal));
        return $"only={o};exclude={e}";
    }

    // Best-effort cache write: encoding a pathologically deep forest (or any IO hiccup) must never fail
    // the query — on error we simply don't cache and the next run recomputes. The single home for the
    // try/catch the tree forest, render sidecar, and EP-site writes all shared.
    internal static void TryCache(Action put)
    {
        try
        {
            put();
        }
        catch (Exception ex)
            when (ex is JsonException or NotSupportedException or InvalidOperationException or IOException)
        {
            // skip caching this result
        }
    }
}

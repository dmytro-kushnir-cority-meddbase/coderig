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

    // The cache key for the WHOLE-STORE hazard-augmented effect set (derive's effect computation: every
    // indexed symbol's effects + the field-fed shared_state arms + the race_window/dual_write/
    // thread_local_context post-pass). It is a pure function of the store + the effective rule fingerprint
    // and is EP-INDEPENDENT and TRAVERSAL-MODE-INDEPENDENT — an effect is a per-method fact, not a function
    // of which entry point reaches it or whether the walk is sync/async. So `derive`, `tree --hazards` (any
    // EP, any mode), and any future hazard surface all share ONE entry: compute once, reuse everywhere.
    // Reindex shifts storeKey (miss); a changed rule shifts rulesHash (miss) — so hazards stay query-side
    // data (a rule edit needs no re-index, just recomputes the cache). `v1` is the payload-schema version.
    internal static string HazardEffectsCacheKey(string storeKey, string rulesHash)
    {
        var material = $"hazardfx|v1|{storeKey}|{rulesHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    // The cache key for a `rig impact` two-store diff artifact: the artifact is a pure function of the TWO
    // immutable per-commit stores (each addressed by its own StoreKey = rig.db size+mtime), the effective
    // rule fingerprint, and the traversal mode (sync-cut vs async-handoff — it changes the reach footprint).
    // Both store keys are folded in, so reindexing EITHER side shifts the key (miss); `mode` distinguishes a
    // --async run from a sync one. Render-only flags (--structural / --format / --limit) are deliberately
    // ABSENT: they only change how the SAME diff is presented (which section, truncation, tsv vs human), not
    // the diff itself, so they must not fragment the cache. `v1` is the payload-schema version (bump to
    // ignore older blobs). The artifact is stored in the HEAD store's cache.db (its store_key purge column),
    // so the base side's identity lives only in this key — a stale base store can never serve a hit.
    internal static string ImpactCacheKey(string baseStoreKey, string headStoreKey, string rulesHash, FactPathFinder.TraversalMode mode)
    {
        var material = $"impact|v1|{baseStoreKey}|{headStoreKey}|{rulesHash}|{mode}";
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
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or IOException)
        {
            // skip caching this result
        }
    }
}

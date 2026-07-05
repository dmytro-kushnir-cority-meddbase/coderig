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

    // A forest cache key — a newtype over the hashed key string, produced ONLY by TreeCacheKey. The render
    // sidecar (RenderSidecarKey) takes this type, not a bare string, so it can NEVER be derived from a
    // non-forest key; and because the sidecar suffixes `.Value`, it inherits TreeCacheKey's full dependency +
    // version set automatically — a forest-key bump (new param / v-bump) flows through for free, so the
    // sidecar can never drift out of lockstep with the forest it hangs off. Free at runtime: a one-field
    // readonly struct over a string reference (pass-by-value = copy one pointer, no allocation, no boxing).
    internal readonly record struct ForestCacheKey(string Value);

    // The cache key for a `rig tree` forest+effects artifact: everything the artifact is a function of —
    // the store identity, the effective rule fingerprint, and the traversal parameters. `v2` is the
    // payload-schema version (bump to ignore older blobs) — bumped from v1 when TraceNode gained
    // TruncationCause, so a warm cache from before the split doesn't render stale conflated `seen` flags.
    // Render-only flags (--files/--summary/--effects and --only/--exclude) are deliberately absent: they
    // don't change the forest or the unfiltered effects, only how they're presented, so they must not
    // fragment the cache.
    internal static ForestCacheKey TreeCacheKey(
        string storeKey,
        string rulesHash,
        string fromPattern,
        int maxDepth,
        int maxNodes,
        FactPathFinder.TraversalMode mode,
        bool raw
    )
    {
        // maxNodes is in the key because a forest built under one --limit must not serve another (a
        // budget-capped forest is a DIFFERENT tree, not a different rendering of the same tree). Adding
        // the field shifts every existing key once (one cache re-warm) — accepted in lieu of a bump.
        var material = $"tree|v2|{storeKey}|{rulesHash}|{fromPattern}|{maxDepth}|{maxNodes}|{mode}|{raw}";
        return new ForestCacheKey(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))));
    }

    // The cache key for the WHOLE-STORE hazard-augmented effect set (derive's effect computation: every
    // indexed symbol's effects + the field-fed shared_state arms + the race_window/dual_write/
    // thread_local_context post-pass). It is a pure function of the store + the effective rule fingerprint
    // and is EP-INDEPENDENT and TRAVERSAL-MODE-INDEPENDENT — an effect is a per-method fact, not a function
    // of which entry point reaches it or whether the walk is sync/async. So `derive`, `tree --hazards` (any
    // EP, any mode), and any future hazard surface all share ONE entry: compute once, reuse everywhere.
    // Reindex shifts storeKey (miss); a changed rule shifts rulesHash (miss) — so hazards stay query-side
    // data (a rule edit needs no re-index, just recomputes the cache). The payload-schema version bumped
    // v1->v2 when DerivedEffect gained EnclosingGuards (branch-aware-effects); a pre-guard cached set must
    // miss, else a stale hit would decode null guards and drop the ⎇ markers on effect leaves. Bumped
    // v2->v3 for the lazy_init_race lock-enclosed tier (2026-07-02): the CLASSIFIER changed with no key
    // input changing, so a warm v2 entry would keep serving pre-tier reasons indefinitely.
    internal static string HazardEffectsCacheKey(string storeKey, string rulesHash)
    {
        var material = $"hazardfx|v3|{storeKey}|{rulesHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    // The cache key for the WHOLE-STORE GRAPH-TIER hazard findings (cache_coherence + event_cycle +
    // static_init_capture). Like the effect set above these are EP-INDEPENDENT, whole-store facts — a property
    // of the SHAPED call graph (forward-closure correlation + cycle detection) + the static-field universe, not
    // a function of which entry point reaches them. So `derive` and `tree --hazards` share ONE entry: derive
    // once over the shaped graph (the cost we must NOT pay per-EP), reuse everywhere. A reindex shifts storeKey
    // (miss); a changed rule shifts rulesHash (miss). DISTINCT namespace (`graphhaz`) from HazardEffectsCacheKey
    // so the effect-attached set and the graph-tier set never collide. `v1` is the payload-schema version.
    internal static string GraphHazardFindingsCacheKey(string storeKey, string rulesHash)
    {
        var material = $"graphhaz|v1|{storeKey}|{rulesHash}";
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
        // Fold the TOOL BUILD (assembly MVID, regenerated every compile) into the key alongside the two store
        // keys, rules, and mode. The diff is a function of the derivation LOGIC too, not just the stores +
        // rules — so a `rig` upgrade that changes how effects/reachability are computed must miss (else the
        // disk cache would serve a stale diff, and the client's derivation-version purge would refetch right
        // into it). Recompute-on-upgrade is the correct default for a fact tool; the diff is idempotent.
        var toolBuild = typeof(QueryCacheKeys).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        var material = $"impact|v2|{toolBuild}|{baseStoreKey}|{headStoreKey}|{rulesHash}|{mode}";
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

    // The render-sidecar cache keys (locations + seam) derived off a forest TreeCacheKey. Encapsulated as a
    // typed record so the seam key's full dependency set is explicit and impossible to omit (a missing
    // component here previously pinned `tree --view hazards` to a permanent render-miss). The seam summary is
    // a function of the FILTERED effects AND, under --view hazards, the whole-store hazard-augmented effect set
    // (which depends on the write-pairing gate) — so Hazards+Gate MUST namespace the key, else a hazards run
    // would either never cache (old behaviour) or taint a plain tree's seam (same forest+filter key).
    internal readonly record struct RenderSidecarKey(ForestCacheKey Forest, string FilterSignature, bool Hazards, bool Gate)
    {
        // Locations (DocID -> file:line) are filter- AND hazard-independent -> keyed off the forest key alone.
        public string Locations() => Forest.Value + ":loc";

        // Seam: namespaced by hazards (+gate, which only affects the hazard-augmented effects) so the hazards
        // seam and the plain-tree seam never share a slot. NON-hazards key is byte-identical to the legacy
        // `Forest.Value + ":seam:" + FilterSignature` (back-compat: existing plain-tree warm caches still hit,
        // and gate must NOT fragment the non-hazards key — a plain tree has no gate-dependent effects).
        public string Seam() => Forest.Value + ":seam:" + (Hazards ? $"haz:{(Gate ? "g" : "ng")}:" : "") + FilterSignature;
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

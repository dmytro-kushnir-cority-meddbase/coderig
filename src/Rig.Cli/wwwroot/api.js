// The IO layer: all HTTP access + the immutable client cache, and NOTHING else (no DOM, no URL). In a React
// port each function becomes a TanStack `useQuery` — the shapes already fit.
//
// Caching: a commit-scoped store is IMMUTABLE and its id is the version, so a response for
// (resolvedStoreId, endpoint, params) is frozen forever — safe to keep in-memory and reuse instantly. Keyed
// by the RESOLVED store id so LATEST and its explicit-id URL share one entry. `runs` is NOT cached (the
// LATEST pointer moves on reindex).

const cache = new Map();

async function getJson(url) {
  const res = await fetch(url);
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error(body.detail || body.title || res.statusText);
  }
  return res.json();
}

async function cached(key, url) {
  if (cache.has(key)) return cache.get(key);
  const data = await getJson(url);
  cache.set(key, data);
  return data;
}

// Build a query string; omits null/blank values. `store` is included only when explicit (an id), so implicit
// LATEST stays out of the URL (and off the server's immutable-cache path).
function qs(params) {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v == null || v === "" || v === false) continue;
    p.set(k, v === true ? "true" : String(v));
  }
  const s = p.toString();
  return s ? "?" + s : "";
}

export const api = {
  runs: () => getJson("/api/runs"),
  providers: () => cached("providers", "/api/providers"),

  // storeId = the RESOLVED id (for the cache key); explicitStore = the id to put on the URL, or null for LATEST.
  tree: (storeId, explicitStore, from, asyncWalk) =>
    cached(`tree|${storeId}|${from}|${!!asyncWalk}`, "/api/tree" + qs({ from, store: explicitStore, async: !!asyncWalk })),

  entrypoints: (storeId, explicitStore) =>
    cached(`eps|${storeId}`, "/api/entrypoints" + qs({ store: explicitStore })),

  hazards: (storeId, explicitStore, from) =>
    cached(`haz|${storeId}|${from}`, "/api/hazards" + qs({ from, store: explicitStore })),

  // search is high-churn and cheap; not cached.
  search: (explicitStore, q) => getJson("/api/search" + qs({ q, store: explicitStore, limit: 15 })),
};

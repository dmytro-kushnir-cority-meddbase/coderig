// The IO layer: all HTTP access + a two-tier client cache (in-memory + IndexedDB), and NOTHING else (no DOM,
// no URL). In a React port each function becomes a TanStack `useQuery`.
//
// Caching correctness: a commit-scoped store's FACTS are immutable, but derived output (tree/effects/hazards)
// also depends on the rule set + the tool build. So the cache is keyed by a DERIVATION VERSION (from
// /api/meta = hash(tool MVID ⊕ rules fingerprint)), not the store id alone. When that version changes (rules
// edit / rig upgrade) the keys change AND the persisted store is purged — stale derived data is never served.
// IndexedDB (not localStorage) because trees are >1 MB; it degrades to memory-only if IDB is unavailable.

const mem = new Map();
let version = "v0"; // derivation version; set at boot via setCacheVersion()

// ---- IndexedDB (best-effort; any failure degrades to in-memory only) ------------------------------------
const DB = "rig-cache",
  STORE = "kv";
function idb() {
  return new Promise((resolve, reject) => {
    const r = indexedDB.open(DB, 1);
    r.onupgradeneeded = () => r.result.createObjectStore(STORE);
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error);
  });
}
async function idbGet(k) {
  try {
    const db = await idb();
    return await new Promise((res) => {
      const q = db.transaction(STORE).objectStore(STORE).get(k);
      q.onsuccess = () => res(q.result);
      q.onerror = () => res(undefined);
    });
  } catch {
    return undefined;
  }
}
async function idbPut(k, v) {
  try {
    const db = await idb();
    db.transaction(STORE, "readwrite").objectStore(STORE).put(v, k);
  } catch {
    /* quota / unavailable — skip */
  }
}
async function idbClear() {
  try {
    const db = await idb();
    db.transaction(STORE, "readwrite").objectStore(STORE).clear();
  } catch {
    /* ignore */
  }
}

// Set the derivation version and purge the persisted store if it moved (keys are version-prefixed, so old
// entries would be unreachable anyway — this reclaims their space). Call once at boot after /api/meta.
export async function setCacheVersion(v) {
  version = v;
  if (localStorage.getItem("rig-cache-ver") !== v) {
    await idbClear();
    mem.clear();
    localStorage.setItem("rig-cache-ver", v);
  }
}
// Force-purge everything (the UI's "purge cache" button).
export async function purgeCache() {
  mem.clear();
  await idbClear();
}

async function getJson(url) {
  const res = await fetch(url);
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error(body.detail || body.title || res.statusText);
  }
  return res.json();
}
async function cached(key, url) {
  const k = version + "|" + key;
  if (mem.has(k)) return mem.get(k);
  const hit = await idbGet(k);
  if (hit !== undefined) {
    mem.set(k, hit);
    return hit;
  }
  const data = await getJson(url);
  mem.set(k, data);
  idbPut(k, data); // fire-and-forget persist
  return data;
}

// Query string; omits null/blank. `store` is included only when explicit (an id) — implicit LATEST stays off
// the URL (its response can't be frozen). The RESOLVED id goes in the cache key, so LATEST and its explicit
// URL share one entry.
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
  meta: () => getJson("/api/meta"),
  runs: () => getJson("/api/runs"), // LATEST pointer moves → never cached
  providers: () => cached("providers", "/api/providers"),
  tree: (storeId, explicitStore, from, asyncWalk) =>
    cached(
      `tree|${storeId}|${from}|${!!asyncWalk}`,
      "/api/tree" + qs({ from, store: explicitStore, async: !!asyncWalk }),
    ),
  entrypoints: (storeId, explicitStore) => cached(`eps|${storeId}`, "/api/entrypoints" + qs({ store: explicitStore })),
  hazards: (storeId, explicitStore, from) =>
    cached(`haz|${storeId}|${from}`, "/api/hazards" + qs({ from, store: explicitStore })),
  search: (explicitStore, q) => getJson("/api/search" + qs({ q, store: explicitStore, limit: 15 })), // high-churn, uncached
};

// App state (a single Zustand-shaped store) + the ONLY code that reads/writes the URL. No DOM, no fetch.
// The URL is the source of truth for the QUERY (shareable, refresh- and back/forward-safe); preferences
// (theme, rail width) live in localStorage (see main.js).

import { createStore } from "./lib.js";

export const store = createStore({
  // query — mirrored to the URL
  from: "",
  storeId: null, // explicit store selection; null => LATEST (the read default)
  view: "paths", // paths | full | effects
  mode: "none", // none | only | exclude   (effect filter)
  tokens: [], // provider / provider:op filter tokens
  asyncWalk: false, // --async (changes the fetched tree → refetch)
  rawTree: false, // show the raw unfolded tree (bypass opaque/collapse seam folds → ?raw=true, refetch)
  collapse: "", // client-side collapse depth ("" = none)
  signatures: false, // render mode: show param signatures
  predicates: false, // render mode: show control-dependence guards
  hazards: false, // render mode: overlay hazard marks
  // data
  runs: [],
  providers: { providers: [], providerOps: [] },
  tree: null, // last /api/tree response (the canonical tree)
  treeFrom: "", // the pattern `tree` was loaded for
  eps: [], // entry points for the active store
  hazardMarks: null, // /api/hazards response (array of {methodId,type,confidence,sites}) for the current tree
  // impact mode (store-vs-store diff)
  appMode: "tree", // tree | impact  (top-level view)
  impactBase: "", // base store id
  impactHead: "", // head store id
  impactAsync: false, // --async for the diff: walk async/scheduled handoffs (changes the diff → refetch)
  impactData: null, // /api/impact response
  impactFilter: "", // filter over per-EP deltas (route / effect substring)
  // diff overlay on a tree: when you open a tree FROM an impact EP card, this carries that EP's changed
  // methods so the head tree can highlight what the diff touched. Session-only (not URL-synced). null = off.
  diffOverlay: null, // { from, base, head, added:[enclosingFqn], removed:[enclosingFqn], changedOnly:bool }
  // ui
  tab: "runs", // runs | eps
  epFilter: "",
  // (status text + busy spinner are transient DOM, managed directly via refs in main.js — not app state)
});

export const get = () => store.getState();
export const set = (patch) => store.setState(patch);

// The resolved store id: explicit selection, else the LATEST run's id. Used for cache keys + display.
export function activeStoreId(s = get()) {
  return (
    s.storeId || (s.runs.find((r) => r.isLatest) || s.runs[0] || {}).storeId
  );
}

// The query slice, for a watch() that re-serializes the URL only when the query changes.
export const querySlice = (s) => [
  s.from,
  s.storeId,
  s.view,
  s.mode,
  s.tokens.join(","),
  s.asyncWalk,
  s.collapse,
  s.signatures,
  s.predicates,
  s.hazards,
  s.appMode,
  s.impactBase,
  s.impactHead,
  s.impactAsync,
];

// state -> URL (query params only; defaults omitted to keep links terse).
export function serializeUrl(s = get()) {
  const p = new URLSearchParams();
  if (s.from) p.set("from", s.from);
  if (s.storeId) p.set("store", s.storeId);
  if (s.view !== "paths") p.set("view", s.view);
  if (s.mode !== "none") p.set("mode", s.mode);
  if (s.tokens.length) p.set("tokens", s.tokens.join(","));
  if (s.asyncWalk) p.set("async", "1");
  if (s.collapse) p.set("collapse", s.collapse);
  if (s.signatures) p.set("sig", "1");
  if (s.predicates) p.set("pred", "1");
  if (s.hazards) p.set("haz", "1");
  if (s.appMode === "impact") {
    p.set("app", "impact");
    if (s.impactBase) p.set("ibase", s.impactBase);
    if (s.impactHead) p.set("ihead", s.impactHead);
    if (s.impactAsync) p.set("iasync", "1");
  }
  history.replaceState(
    null,
    "",
    location.pathname + (p.toString() ? "?" + p : ""),
  );
}

// URL -> a query-state patch. A persisted ?store= that no longer exists falls back to LATEST (null) silently.
// `search` is captured at boot BEFORE the serialize-watch runs (which would otherwise wipe the query first).
export function readUrl(runs, search = location.search) {
  const p = new URLSearchParams(search);
  const s = p.get("store");
  return {
    from: p.get("from") || "",
    storeId: s && runs.some((r) => r.storeId === s) ? s : null,
    view: p.get("view") || "paths",
    mode: p.get("mode") || "none",
    tokens: (p.get("tokens") || "").split(",").filter(Boolean),
    asyncWalk: p.get("async") === "1",
    collapse: p.get("collapse") || "",
    signatures: p.get("sig") === "1",
    predicates: p.get("pred") === "1",
    hazards: p.get("haz") === "1",
    appMode: p.get("app") === "impact" ? "impact" : "tree",
    impactBase: runs.some((r) => r.storeId === p.get("ibase"))
      ? p.get("ibase")
      : "",
    impactHead: runs.some((r) => r.storeId === p.get("ihead"))
      ? p.get("ihead")
      : "",
    impactAsync: p.get("iasync") === "1",
  };
}

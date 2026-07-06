// Controller / wiring: builds the Shell, mounts it, defines the actions (which call api + set state), and
// subscribes region re-renders to state slices. Preferences (theme, rail) and the transient search dropdown /
// status / busy live here as direct DOM (not app state). This is the only file that glues view↔state↔io.

import { h, mount, watch } from "./lib.js";
import { api, setCacheVersion, purgeCache } from "./api.js";
import {
  store,
  get,
  set,
  activeStoreId,
  querySlice,
  serializeUrl,
  readUrl,
  pushCrumb,
} from "./store.js";
import {
  Shell,
  RunsList,
  EpList,
  TreeView,
  CallersPanel,
  ImpactView,
  ImpactProgress,
  Chips,
  treeStatus,
  baseName,
  BreadcrumbTrail,
  shortLabel,
} from "./components.js";

const explicit = () => get().storeId; // the id to put on URLs (null => LATEST)
const resolved = () => activeStoreId(); // the resolved id (for cache keys)

// ---- status + busy (transient DOM) ----------------------------------------------------------------------
let refs;
function status(msg, err = false) {
  refs.status.textContent = msg;
  refs.status.className = err ? "err" : "";
}
function setBusy(on) {
  refs.statusbar.classList.toggle("busy", on);
  refs.tree.classList.toggle("busy", on);
  refs.impact.classList.toggle("busy", on);
  refs.go.disabled = on;
  refs.impactGo.disabled = on;
}

// ---- data actions ---------------------------------------------------------------------------------------
// `recordHistory=false` is the escape hatch used by restoreCrumb (popstate / a breadcrumb click) and by
// openDiffTree (which records its OWN crumb before delegating here) — see the "pivot history" section below.
async function openTree(pattern, { recordHistory = true } = {}) {
  if (!pattern) {
    status("enter a pattern", true);
    return;
  }
  hideResults();
  // Keep the (uncontrolled) search box in sync with programmatic navigations — re-root, a drawer EP click,
  // an impact cross-link — not just typed queries.
  if (refs.from) refs.from.value = pattern;
  // drop a stale diff overlay when navigating to a DIFFERENT EP (openDiffTree sets it for the same pattern
  // right before calling here, so that case is preserved).
  if (get().diffOverlay && get().diffOverlay.from !== pattern)
    set({ diffOverlay: null });
  // A crumb marks a genuine RE-root: going from an already-shown tree to a DIFFERENT pattern. The very first
  // tree of a session (boot deep-link or the first search) has nothing to go "back" to, so it's not a pivot;
  // re-fetches of the SAME pattern (toggling async/raw) aren't a pivot either.
  const prevTreeFrom = get().treeFrom;
  const isNewRoot = recordHistory && prevTreeFrom !== "" && pattern !== prevTreeFrom;
  set({ from: pattern });
  setBusy(true);
  status("querying…");
  try {
    const data = await api.tree(
      resolved(),
      explicit(),
      pattern,
      get().asyncWalk,
      get().rawTree,
    );
    if (!data.matched) {
      set({ tree: null, treeFrom: "" });
      status(`no symbol matches '${pattern}'`, true);
      return;
    }
    set({ tree: data, treeFrom: pattern, hazardMarks: null });
    if (get().hazards) loadHazards();
    if (isNewRoot)
      recordCrumb("tree", shortLabel(pattern), {
        from: pattern,
        diffOverlay: get().diffOverlay,
        callers: null,
      });
  } catch (e) {
    status(e.message, true);
  } finally {
    setBusy(false);
  }
}
async function loadEntrypoints() {
  try {
    set({ eps: await api.entrypoints(resolved(), explicit()) });
  } catch (e) {
    refs.eps.textContent = "error: " + e.message;
  }
}
async function loadHazards() {
  const s = get();
  if (!s.tree || !s.hazards) return;
  try {
    set({ hazardMarks: await api.hazards(resolved(), explicit(), s.treeFrom) });
  } catch (e) {
    status("hazards: " + e.message, true);
  }
}
function selectStore(id) {
  const latest = get().runs.find((r) => r.isLatest) || get().runs[0];
  set({
    storeId: latest && id === latest.storeId ? null : id,
    diffOverlay: null,
  }); // a manual store switch invalidates any diff overlay
  if (get().tab === "eps") loadEntrypoints();
  if (get().treeFrom) openTree(get().treeFrom);
}
function loadImpact() {
  const { impactBase, impactHead, impactAsync } = get();
  if (!impactBase || !impactHead) {
    status("pick a base and a head store", true);
    return;
  }
  if (impactBase === impactHead) {
    status("base and head are the same store", true);
    return;
  }
  setBusy(true);
  status("diffing…");
  // Stream live phase progress over SSE (the stream ALSO warms the disk cache); on `done`, GET the now-warm
  // /api/impact for the data. Warm cache → `cache hit` → `done` almost immediately. (Hacky but "not sad".)
  const log = [];
  mount(refs.impact, ImpactProgress(log));
  const es = new EventSource(
    `/api/impact/stream?base=${encodeURIComponent(impactBase)}&head=${encodeURIComponent(impactHead)}` +
      (impactAsync ? "&async=true" : ""),
  );
  let settled = false;
  const finish = (fn) => {
    if (settled) return;
    settled = true;
    es.close();
    setBusy(false);
    fn();
  };
  es.addEventListener("phase", (e) => {
    log.push(e.data);
    mount(refs.impact, ImpactProgress(log));
  });
  es.addEventListener("done", () =>
    finish(async () => {
      try {
        set({
          impactData: await api.impact(impactBase, impactHead, impactAsync),
        });
        const d = get().impactData;
        status(
          `impact: ${d.perEp.length.toLocaleString()} behavioral change(s), +${d.addedEps.length}/−${d.removedEps.length} EPs`,
        );
      } catch (e) {
        status(e.message, true);
      }
    }),
  );
  es.addEventListener("failed", (e) =>
    finish(() => status("diff failed: " + e.data, true)),
  );
  es.onerror = () => finish(() => status("diff stream connection lost", true));
}

// ---- pivot history (breadcrumbs) -------------------------------------------------------------------------
// Every pivot (re-root, drawer open, diff cross-link) pushes a crumb onto `history` AND mirrors it onto a
// real `history.pushState` entry, so the breadcrumb trail (BreadcrumbTrail, mounted into refs.crumbs) and the
// browser's own back/forward button are driven by the SAME mechanism — clicking a crumb just replays that
// many real back/forward steps (see actions.jumpToCrumb), and the popstate handler below does the one true
// restore. `diffOverlay`/`callers` default to what's already on the crumb; callers pass what they need.
function recordCrumb(kind, label, extra = {}) {
  const s = get();
  const crumb = {
    kind,
    label,
    from: s.treeFrom,
    appMode: s.appMode,
    storeId: s.storeId,
    diffOverlay: null,
    callers: null,
    ...extra,
  };
  const patch = pushCrumb(s, crumb);
  set(patch);
  history.pushState(
    { crumb, cursor: patch.historyCursor },
    "",
    location.pathname + location.search,
  );
}

// Restore app state from a crumb (breadcrumb click or a real browser back/forward). Every pivot action below
// takes a `{ recordHistory: false }` escape hatch specifically so THIS path never re-pushes a crumb.
async function restoreCrumb(crumb) {
  set({ appMode: crumb.appMode, storeId: crumb.storeId ?? null });
  if (crumb.kind === "tree") {
    set({ diffOverlay: crumb.diffOverlay || null, callers: null });
    if (crumb.from) await openTree(crumb.from, { recordHistory: false });
    return;
  }
  // drawer pivots (callers/reaches/path) don't change the tree — restore whatever tree was behind the drawer
  // when the crumb was recorded, then reopen the drawer itself.
  if (crumb.from && crumb.from !== get().treeFrom)
    await openTree(crumb.from, { recordHistory: false });
  if (crumb.kind === "callers")
    await actions.openCallers(
      { id: crumb.callers.target },
      crumb.callers.mode,
      crumb.callers.asyncWalk,
      { recordHistory: false },
    );
  else if (crumb.kind === "reaches")
    await actions.openReaches({ id: crumb.callers.target }, { recordHistory: false });
  else if (crumb.kind === "path")
    await actions.openPath(crumb.callers.from, crumb.callers.target, {
      recordHistory: false,
    });
}

// Real browser back/forward: pushState's attached state carries the crumb + its cursor position directly, so
// there's no need to re-derive anything from the URL.
window.addEventListener("popstate", (e) => {
  if (!e.state || !e.state.crumb) return;
  set({ historyCursor: e.state.cursor });
  restoreCrumb(e.state.crumb);
});

// ---- actions passed to components -----------------------------------------------------------------------
// Positioned context menu for a tree node — the reverse-nav entry point. Built as a transient body-level div
// (dismissed on click-away / Escape), so it escapes the tree's overflow clipping.
function showNodeMenu(node, e) {
  document.querySelectorAll(".node-menu").forEach((m) => m.remove());
  const item = (label, fn) =>
    h("button", { class: "node-menu-item", onClick: () => { menu.remove(); teardown(); fn(); } }, label);
  const menu = h(
    "div",
    { class: "node-menu" },
    item("Re-root here", () => openTree(node.id)),
    item("Entry points reaching this →", () => actions.openCallers(node, "entrypoints")),
    item("Who reaches this (roots)", () => actions.openCallers(node, "roots")),
    item("Effects reachable from here →", () => actions.openReaches(node)),
  );
  menu.style.left = Math.min(e.clientX, window.innerWidth - 240) + "px";
  menu.style.top = e.clientY + "px";
  document.body.appendChild(menu);
  const dismiss = (ev) => { if (!menu.contains(ev.target)) { menu.remove(); teardown(); } };
  const esc = (ev) => { if (ev.key === "Escape") { menu.remove(); teardown(); } };
  const teardown = () => { document.removeEventListener("mousedown", dismiss); document.removeEventListener("keydown", esc); };
  setTimeout(() => { document.addEventListener("mousedown", dismiss); document.addEventListener("keydown", esc); }, 0);
}

const actions = {
  setTheme: applyTheme,
  nodeMenu: showNodeMenu,
  async openCallers(node, mode, asyncWalk = false, { recordHistory = true } = {}) {
    const from = node.id;
    set({ callers: { target: from, mode, async: asyncWalk, matched: false, loading: true } });
    try {
      const data = await api.callers(resolved(), explicit(), from, mode, asyncWalk);
      set({ callers: { target: from, mode, async: asyncWalk, matched: data.matched, entryPoints: data.entryPoints, roots: data.roots } });
      if (recordHistory)
        recordCrumb(
          "callers",
          (mode === "entrypoints" ? "EPs: " : "callers: ") + shortLabel(from),
          { callers: { target: from, mode, asyncWalk } },
        );
    } catch (err) {
      status("callers: " + err.message, true);
      set({ callers: null });
    }
  },
  closeCallers() {
    set({ callers: null });
  },
  async openReaches(node, { recordHistory = true } = {}) {
    const from = node.id;
    set({ callers: { target: from, mode: "reaches", loading: true } });
    try {
      const data = await api.reaches(resolved(), explicit(), from);
      set({ callers: { target: from, mode: "reaches", matched: data.matched, reachableCount: data.reachableCount, effects: data.effects } });
      if (recordHistory)
        recordCrumb("reaches", "reaches: " + shortLabel(from), {
          callers: { target: from },
        });
    } catch (err) {
      status("reaches: " + err.message, true);
      set({ callers: null });
    }
  },
  async openPath(fromFqn, targetId, { recordHistory = true } = {}) {
    set({ callers: { target: targetId, from: fromFqn, mode: "path", loading: true } });
    try {
      const data = await api.path(resolved(), explicit(), fromFqn, targetId);
      set({ callers: { target: targetId, from: fromFqn, mode: "path", matched: data.matched, nodes: data.nodes } });
      if (recordHistory)
        recordCrumb("path", "path → " + shortLabel(targetId), {
          callers: { from: fromFqn, target: targetId },
        });
    } catch (err) {
      status("path: " + err.message, true);
      set({ callers: null });
    }
  },
  setTab(id) {
    set({ tab: id });
    if (id === "eps" && !get().eps.length) loadEntrypoints();
  },
  setEpFilter(v) {
    set({ epFilter: v });
  },
  selectStore,
  openTree,
  setView(v) {
    set({ view: v });
  },
  setMode(v) {
    set({ mode: v });
  },
  setCollapse(v) {
    set({ collapse: v });
  },
  toggleToken(t) {
    set((s) => ({
      tokens: s.tokens.includes(t)
        ? s.tokens.filter((x) => x !== t)
        : [...s.tokens, t],
    }));
  },
  renderMsList,
  setFlag(key, val) {
    set({ [key]: val });
    if (key === "asyncWalk" && get().treeFrom) openTree(get().treeFrom); // async changes the fetched tree
    if (key === "rawTree" && get().treeFrom) openTree(get().treeFrom); // raw/folded changes the fetched tree
    if (key === "impactAsync" && get().impactBase && get().impactHead)
      loadImpact(); // async changes the diff
    if (key === "hazards" && val) loadHazards();
  },
  async purge() {
    await purgeCache();
    status("cache purged — refetching…");
    if (get().tab === "eps") {
      set({ eps: [] });
      loadEntrypoints();
    }
    if (get().treeFrom) openTree(get().treeFrom);
    else status("cache purged");
  },
  // impact mode
  setAppMode(m) {
    set({ appMode: m });
  },
  setImpactStore(which, id) {
    set(which === "base" ? { impactBase: id } : { impactHead: id });
  },
  setImpactFilter(v) {
    set({ impactFilter: v });
  },
  loadImpact,
  // cross-link: an impact EP card → open that EP's HEAD tree with the diff overlaid (added/removed effects'
  // enclosing methods highlighted). Uses the impact head store + the EP delta already loaded client-side.
  openDiffTree(p) {
    const enc = (arr) => [...new Set(arr.map((e) => e.enclosing))];
    const base = get().impactData?.base?.label || "base";
    const head = get().impactData?.head?.label || "head";
    const overlay = {
      from: p.fqn,
      base,
      head,
      effAdded: enc(p.added),
      effRemoved: enc(p.removed),
      addedReach: [],
      removedReach: [],
      changedOnly: true,
    };
    // Set the overlay immediately (effect deltas — already loaded), open the head tree, then ENRICH with the
    // structural reach delta (added/removed reachable methods) fetched from /api/impact/reach (warm lookup).
    set({
      appMode: "tree",
      storeId: get().impactHead, // view the HEAD store's tree
      from: p.fqn,
      asyncWalk: get().impactAsync, // match the diff's traversal mode so the tree reaches what the diff diffed
      diffOverlay: overlay,
    });
    refs.from.value = p.fqn;
    refs.view.value = get().view;
    // This IS the pivot's crumb — the nested openTree below is told not to double-record it.
    recordCrumb("tree", "diff: " + shortLabel(p.fqn), {
      from: p.fqn,
      diffOverlay: overlay,
      callers: null,
    });
    openTree(p.fqn, { recordHistory: false });
    api
      .impactReach(
        get().impactBase,
        get().impactHead,
        p.kind,
        p.route,
        get().impactAsync,
      )
      .then((r) => {
        const ov = get().diffOverlay;
        if (ov && ov.from === p.fqn)
          set({
            diffOverlay: {
              ...ov,
              addedReach: r.added.map((n) => n.id),
              removedReach: r.removed,
            },
          });
      })
      .catch(() => {}); // structural enrichment is best-effort; the effect overlay still stands
  },
  toggleChangedOnly() {
    set((s) =>
      s.diffOverlay
        ? {
            diffOverlay: {
              ...s.diffOverlay,
              changedOnly: !s.diffOverlay.changedOnly,
            },
          }
        : {},
    );
  },
  clearDiff() {
    set({ diffOverlay: null });
  },
  // Breadcrumb click: replay the browser's OWN back/forward to that entry — popstate does the actual
  // restore (restoreCrumb), so the trail and the real history stack never diverge.
  jumpToCrumb(index) {
    const delta = index - get().historyCursor;
    if (delta !== 0) history.go(delta);
  },
};

// ---- the provider checklist (built imperatively into refs.msList; state = selectedTokens) ---------------
function renderMsList(filter = "") {
  const s = get();
  const f = filter.trim().toLowerCase();
  const toks = new Set(s.tokens);
  const items = [
    ...s.providers.providers.map((t) => [t, false]),
    ...s.providers.providerOps.map((t) => [t, true]),
  ].filter(([t]) => !f || t.includes(f));
  mount(
    refs.msList,
    items.map(([t, op]) =>
      h(
        "label",
        { class: "ms-opt" },
        h("input", {
          type: "checkbox",
          value: t,
          checked: toks.has(t),
          onChange: () => actions.toggleToken(t),
        }),
        " " + t,
        op ? h("span", { class: "ms-op" }, "op") : null,
      ),
    ),
  );
}

// ---- preferences (localStorage) -------------------------------------------------------------------------
function applyTheme(mode) {
  if (mode === "system") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", mode);
  localStorage.setItem("rig-theme", mode);
  for (const b of refs.theme.children)
    b.classList.toggle("on", b.dataset.theme === mode);
}
function initSplitter() {
  const saved = localStorage.getItem("rig-rail");
  if (saved) document.documentElement.style.setProperty("--rail", saved + "px");
  let dragging = false;
  refs.splitter.addEventListener("mousedown", () => {
    dragging = true;
    refs.splitter.classList.add("drag");
    document.body.style.userSelect = "none";
  });
  document.addEventListener("mousemove", (e) => {
    if (dragging)
      document.documentElement.style.setProperty(
        "--rail",
        Math.min(640, Math.max(180, e.clientX)) + "px",
      );
  });
  document.addEventListener("mouseup", () => {
    if (!dragging) return;
    dragging = false;
    refs.splitter.classList.remove("drag");
    document.body.style.userSelect = "";
    localStorage.setItem(
      "rig-rail",
      parseInt(
        getComputedStyle(document.documentElement).getPropertyValue("--rail"),
      ) || 300,
    );
  });
}

// ---- search dropdown (transient DOM under #from) --------------------------------------------------------
let searchTimer = null,
  activeHit = -1;
function hideResults() {
  refs.results.classList.remove("show");
  refs.results.replaceChildren();
  activeHit = -1;
}
async function doSearch(q) {
  try {
    const hits = await api.search(explicit(), q);
    if (!hits.length) {
      hideResults();
      return;
    }
    activeHit = -1;
    mount(
      refs.results,
      hits.map((hh, i) =>
        h(
          "div",
          {
            class: "hit",
            dataset: { id: hh.id, i },
            onMousedown: () => {
              refs.from.value = hh.id;
              hideResults();
              openTree(hh.id);
            },
          },
          h("span", { class: "hkind" }, hh.kind),
          " " + hh.name,
          h("span", { class: "hfile" }, `${baseName(hh.file)}:${hh.line}`),
        ),
      ),
    );
    refs.results.classList.add("show");
  } catch {
    hideResults();
  }
}
function setupSearch() {
  refs.from.addEventListener("input", () => {
    clearTimeout(searchTimer);
    const q = refs.from.value.trim();
    if (q.length < 2) {
      hideResults();
      return;
    }
    searchTimer = setTimeout(() => doSearch(q), 220);
  });
  refs.from.addEventListener("keydown", (e) => {
    const hits = [...refs.results.querySelectorAll(".hit")];
    if (refs.results.classList.contains("show") && hits.length) {
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        activeHit =
          (activeHit + (e.key === "ArrowDown" ? 1 : hits.length - 1)) %
          hits.length;
        hits.forEach((hh, i) => hh.classList.toggle("active", i === activeHit));
        hits[activeHit].scrollIntoView({ block: "nearest" });
        return;
      }
      if (e.key === "Enter" && activeHit >= 0) {
        e.preventDefault();
        const id = hits[activeHit].dataset.id;
        refs.from.value = id;
        hideResults();
        openTree(id);
        return;
      }
      if (e.key === "Escape") {
        hideResults();
        return;
      }
    }
    if (e.key === "Enter") openTree(refs.from.value.trim());
  });
  document.addEventListener("click", (e) => {
    if (!e.target.closest(".fromwrap")) hideResults();
  });
  // multiselect popover open/close
  refs.chips.addEventListener("click", () => {
    if (!refs.ms.classList.contains("disabled"))
      refs.ms.classList.toggle("open");
  });
  document.addEventListener("click", (e) => {
    if (!e.target.closest(".ms")) refs.ms.classList.remove("open");
  });
}

// ---- impact mode: toggle Tree/Impact UI + populate the base/head store pickers --------------------------
function applyAppMode(m) {
  const impact = m === "impact";
  refs.treeToolbar.classList.toggle("hidden", impact);
  refs.tree.classList.toggle("hidden", impact);
  refs.impactToolbar.classList.toggle("hidden", !impact);
  refs.impact.classList.toggle("hidden", !impact);
  for (const b of refs.appmode.children)
    b.classList.toggle("on", b.dataset.app === m);
}
function populateImpactStores(s) {
  const opts = (ph) => [
    h("option", { value: "" }, ph),
    ...s.runs.map((r) =>
      h(
        "option",
        { value: r.storeId },
        `${r.storeId}${r.branch ? " · " + r.branch : ""}`,
      ),
    ),
  ];
  mount(refs.impactBase, opts("base…"));
  mount(refs.impactHead, opts("head…"));
  refs.impactBase.value = s.impactBase;
  refs.impactHead.value = s.impactHead;
}

// ---- sync uncontrolled inputs from state (once, after URL restore) --------------------------------------
function syncControls(s) {
  refs.from.value = s.from;
  refs.view.value = s.view;
  refs.filterMode.value = s.mode;
  refs.collapse.value = s.collapse;
  refs.async.querySelector("input").checked = s.asyncWalk;
  refs.raw.querySelector("input").checked = s.rawTree;
  refs.sig.querySelector("input").checked = s.signatures;
  refs.pred.querySelector("input").checked = s.predicates;
  refs.haz.querySelector("input").checked = s.hazards;
  refs.ms.classList.toggle("disabled", s.mode === "none");
  refs.impactBase.value = s.impactBase;
  refs.impactHead.value = s.impactHead;
  refs.impactAsync.querySelector("input").checked = s.impactAsync;
  refs.impactFilter.value = s.impactFilter;
  applyAppMode(s.appMode);
}

// ---- region subscriptions (re-render only the affected region when its slice changes) -------------------
function setupWatches() {
  watch(
    store,
    (s) => [s.runs, s.storeId],
    (s) => {
      mount(refs.runs, RunsList(s, actions));
      populateImpactStores(s);
      const latest = s.runs.find((r) => r.isLatest) || s.runs[0];
      const solPath = latest ? latest.solutionPath || "" : "";
      refs.storeDir.textContent = solPath;
      refs.storeDir.title = solPath; // full path on hover — the span ellipsis-truncates when narrow
    },
  );
  watch(
    store,
    (s) => [s.eps, s.epFilter],
    (s) => mount(refs.eps, EpList(s, actions)),
  );
  watch(
    store,
    (s) => [s.tab],
    (s) => {
      refs.tabRuns.classList.toggle("on", s.tab === "runs");
      refs.tabEps.classList.toggle("on", s.tab === "eps");
      refs.paneRuns.classList.toggle("on", s.tab === "runs");
      refs.paneEps.classList.toggle("on", s.tab === "eps");
    },
  );
  watch(
    store,
    (s) => [s.mode],
    (s) => refs.ms.classList.toggle("disabled", s.mode === "none"),
  );
  watch(
    store,
    (s) => [s.tokens.join(",")],
    (s) => {
      mount(refs.chips, Chips(s, actions));
      renderMsList(refs.msSearch.value);
    },
  );
  watch(
    store,
    (s) => [
      s.tree,
      s.view,
      s.mode,
      s.tokens.join(","),
      s.collapse,
      s.signatures,
      s.predicates,
      s.hazards,
      s.hazardMarks,
      s.diffOverlay,
    ],
    (s) => {
      mount(refs.tree, TreeView(s, actions));
      if (s.tree) status(treeStatus(s));
    },
  );
  watch(
    store,
    (s) => [s.callers],
    (s) => mount(refs.callers, CallersPanel(s, actions)),
  );
  watch(
    store,
    (s) => [s.history, s.historyCursor],
    (s) => mount(refs.crumbs, BreadcrumbTrail(s, actions)),
  );
  watch(
    store,
    (s) => [s.appMode],
    (s) => applyAppMode(s.appMode),
  );
  watch(
    store,
    (s) => [s.impactData, s.impactFilter, s.appMode, s.impactAsync],
    (s) => {
      if (s.appMode === "impact") mount(refs.impact, ImpactView(s, actions));
    },
  );
  watch(store, querySlice, (s) => serializeUrl(s)); // URL stays in sync with the query
}

// ---- boot -----------------------------------------------------------------------------------------------
(async function boot() {
  // Capture the incoming query BEFORE any watch runs — the serialize-watch fires on subscribe and would
  // otherwise rewrite the URL from empty defaults, destroying a shared deep-link's params.
  const initialSearch = location.search;
  const shell = Shell(actions);
  refs = shell.refs;
  mount(document.getElementById("app"), shell.root);
  applyTheme(localStorage.getItem("rig-theme") || "system");
  initSplitter();
  setupSearch();
  setupWatches();
  // Derivation version first — it keys the cache and purges a stale persisted store before any cached fetch.
  try {
    const meta = await api.meta();
    await setCacheVersion(meta.derivationVersion);
  } catch {
    /* cache degrades to per-session */
  }
  api.providers().then((p) => {
    set({ providers: p });
    renderMsList("");
  });
  try {
    const runs = await api.runs();
    set({ runs });
    const patch = readUrl(runs, initialSearch); // validate ?store= against known runs
    set(patch);
    syncControls(get());
    if (patch.appMode === "impact") {
      if (patch.impactBase && patch.impactHead) loadImpact();
    } else if (patch.from) {
      openTree(patch.from);
    }
  } catch (e) {
    status("failed to load runs: " + e.message, true);
  }
})();

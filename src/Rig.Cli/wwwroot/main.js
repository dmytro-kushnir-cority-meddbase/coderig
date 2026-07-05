// Controller / wiring: builds the Shell, mounts it, defines the actions (which call api + set state), and
// subscribes region re-renders to state slices. Preferences (theme, rail) and the transient search dropdown /
// status / busy live here as direct DOM (not app state). This is the only file that glues view↔state↔io.

import { h, mount, watch } from "./lib.js";
import { api, setCacheVersion, purgeCache } from "./api.js";
import { store, get, set, activeStoreId, querySlice, serializeUrl, readUrl } from "./store.js";
import { Shell, RunsList, EpList, TreeView, ImpactView, Chips, treeStatus, baseName } from "./components.js";

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
async function openTree(pattern) {
  if (!pattern) {
    status("enter a pattern", true);
    return;
  }
  hideResults();
  set({ from: pattern });
  setBusy(true);
  status("querying…");
  try {
    const data = await api.tree(resolved(), explicit(), pattern, get().asyncWalk);
    if (!data.matched) {
      set({ tree: null, treeFrom: "" });
      status(`no symbol matches '${pattern}'`, true);
      return;
    }
    set({ tree: data, treeFrom: pattern, hazardMarks: null });
    if (get().hazards) loadHazards();
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
  set({ storeId: latest && id === latest.storeId ? null : id });
  if (get().tab === "eps") loadEntrypoints();
  if (get().treeFrom) openTree(get().treeFrom);
}
async function loadImpact() {
  const { impactBase, impactHead } = get();
  if (!impactBase || !impactHead) {
    status("pick a base and a head store", true);
    return;
  }
  if (impactBase === impactHead) {
    status("base and head are the same store", true);
    return;
  }
  setBusy(true);
  status("diffing… (loads + derives BOTH stores — can take a while on a big store)");
  try {
    set({ impactData: await api.impact(impactBase, impactHead) });
    const d = get().impactData;
    status(
      `impact: ${d.perEp.length.toLocaleString()} behavioral change(s), +${d.addedEps.length}/−${d.removedEps.length} EPs`,
    );
  } catch (e) {
    status(e.message, true);
  } finally {
    setBusy(false);
  }
}

// ---- actions passed to components -----------------------------------------------------------------------
const actions = {
  setTheme: applyTheme,
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
    set((s) => ({ tokens: s.tokens.includes(t) ? s.tokens.filter((x) => x !== t) : [...s.tokens, t] }));
  },
  renderMsList,
  setFlag(key, val) {
    set({ [key]: val });
    if (key === "asyncWalk" && get().treeFrom) openTree(get().treeFrom); // async changes the fetched tree
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
  // cross-link: an impact EP card → open that EP's tree
  openTreeFrom(fqn) {
    set({ appMode: "tree", from: fqn });
    refs.from.value = fqn;
    openTree(fqn);
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
        h("input", { type: "checkbox", value: t, checked: toks.has(t), onChange: () => actions.toggleToken(t) }),
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
  for (const b of refs.theme.children) b.classList.toggle("on", b.dataset.theme === mode);
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
    if (dragging) document.documentElement.style.setProperty("--rail", Math.min(640, Math.max(180, e.clientX)) + "px");
  });
  document.addEventListener("mouseup", () => {
    if (!dragging) return;
    dragging = false;
    refs.splitter.classList.remove("drag");
    document.body.style.userSelect = "";
    localStorage.setItem(
      "rig-rail",
      parseInt(getComputedStyle(document.documentElement).getPropertyValue("--rail")) || 300,
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
        activeHit = (activeHit + (e.key === "ArrowDown" ? 1 : hits.length - 1)) % hits.length;
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
    if (!refs.ms.classList.contains("disabled")) refs.ms.classList.toggle("open");
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
  for (const b of refs.appmode.children) b.classList.toggle("on", b.dataset.app === m);
}
function populateImpactStores(s) {
  const opts = (ph) => [
    h("option", { value: "" }, ph),
    ...s.runs.map((r) => h("option", { value: r.storeId }, `${r.storeId}${r.branch ? " · " + r.branch : ""}`)),
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
  refs.sig.querySelector("input").checked = s.signatures;
  refs.pred.querySelector("input").checked = s.predicates;
  refs.haz.querySelector("input").checked = s.hazards;
  refs.ms.classList.toggle("disabled", s.mode === "none");
  refs.impactBase.value = s.impactBase;
  refs.impactHead.value = s.impactHead;
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
      refs.storeDir.textContent = latest ? latest.solutionPath || "" : "";
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
    ],
    (s) => {
      mount(refs.tree, TreeView(s));
      if (s.tree) status(treeStatus(s));
    },
  );
  watch(
    store,
    (s) => [s.appMode],
    (s) => applyAppMode(s.appMode),
  );
  watch(
    store,
    (s) => [s.impactData, s.impactFilter, s.appMode],
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

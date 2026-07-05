// View layer: pure-ish `f(props) -> DOM` components built with h(). No fetch, no URL. `Shell` builds the
// static frame ONCE (inputs live here, uncontrolled → no focus loss) and exposes refs to the regions that
// re-render from state (RunsList / EpList / TreeView / Chips / StatusBar). Sub-components (TreeNode, RunCard,
// …) map 1:1 to future React components.

import { h, mount } from "./lib.js";

export const baseName = (p) => (p ? p.split(/[\\/]/).pop() : "");

// ---- effect filter + projection (pure) -------------------------------------------------------------------
const effMatch = (e, t) => t === e.provider.toLowerCase() || t === (e.provider + ":" + e.operation).toLowerCase();
export function filterEffects(effs, mode, tokens) {
  if (mode === "none" || !tokens.length) return effs;
  const toks = tokens.map((t) => t.toLowerCase());
  return mode === "only"
    ? effs.filter((e) => toks.some((t) => effMatch(e, t)))
    : effs.filter((e) => !toks.some((t) => effMatch(e, t)));
}
function subtreeHasEffect(node, ctx) {
  return (
    filterEffects(node.effects, ctx.mode, ctx.tokens).length > 0 || node.children.some((c) => subtreeHasEffect(c, ctx))
  );
}

// ---- small components ------------------------------------------------------------------------------------
function EffectGlyphs(effects) {
  if (!effects.length) return null;
  return h(
    "span",
    { class: "effects" },
    effects.map((e) =>
      h(
        "span",
        { class: "eff", title: `${e.provider}:${e.operation} ×${e.sites}` },
        e.glyph,
        e.sites > 1 ? h("span", { class: "sites" }, e.sites) : null,
      ),
    ),
  );
}
function Loc(node) {
  return node.file ? h("span", { class: "loc" }, `${baseName(node.file)}:${node.line}`) : null;
}
function Trunc(node) {
  if (!node.truncated) return null;
  if (node.truncationCause === "AlreadyExpanded")
    return h(
      "span",
      { class: "trunc", title: "cycle / shared callee — expanded elsewhere in this tree" },
      "⋯ shown above",
    );
  if (node.truncationCause === "BudgetCapped")
    return h("span", { class: "trunc", title: "node-budget safety cap (50k) reached" }, "⋯ budget");
  return h("span", { class: "trunc" }, "⋯elided");
}
// hazard mark for a method: types sorted, each "type(worstConfidence)×N".
const CONF_RANK = { high: 0, medium: 1, low: 2 };
function HazardMark(marks) {
  if (!marks || !marks.length) return null;
  const label = [...marks]
    .sort((a, b) => a.type.localeCompare(b.type))
    .map((m) => `${m.type}(${m.confidence})${m.sites > 1 ? "×" + m.sites : ""}`)
    .join(", ");
  return h("span", { class: "haz", title: label }, "⚠ " + label);
}

// Merge effect occurrences into an aggregate map: "provider:op" -> {provider, operation, glyph, sites}.
function accInto(agg, effects) {
  for (const e of effects) {
    const k = e.provider + ":" + e.operation;
    const prev = agg.get(k);
    if (prev) prev.sites += e.sites;
    else agg.set(k, { provider: e.provider, operation: e.operation, glyph: e.glyph, sites: e.sites });
  }
}
// The subtree rollup shown ONLY when a branch is collapsed: distinct effects of everything hidden below (the
// node + all descendants, filtered), so a folded branch discloses what it touches at a glance. Hidden by CSS
// unless the node carries `.collapsed`; the effects are summed once at render (no recompute on toggle).
function Rollup(agg) {
  if (!agg.size) return null;
  const items = [...agg.values()].sort((a, b) => (a.provider + a.operation).localeCompare(b.provider + b.operation));
  const title = "subtree touches: " + items.map((e) => `${e.provider}:${e.operation}×${e.sites}`).join(", ");
  return h(
    "span",
    { class: "rollup", title },
    "↴ ",
    items.map((e) => h("span", { class: "eff" }, e.glyph)),
  );
}

// TreeNode(node, depth, ctx): recursive. Returns { el, agg } — agg is the subtree effect rollup, bubbled up
// so each ancestor can show what its folded branch touches. ctx = {view, mode, tokens, collapseLevel,
// signatures, predicates, hazards, hazById}. Collapse ≥ level hides children in the DOM (one click reveals).
function TreeNode(node, depth, ctx) {
  const prune = ctx.view === "paths";
  const kids = prune ? node.children.filter((c) => subtreeHasEffect(c, ctx)) : node.children;
  const hasKids = kids.length > 0;
  const heur = node.dispatchBasis === "heuristic";
  const edgeTxt = node.edgeKind && node.edgeKind !== "entry" ? node.edgeKind + (heur ? " ~heuristic" : "") : "";

  const own = filterEffects(node.effects, ctx.mode, ctx.tokens);
  const agg = new Map();
  accInto(agg, own);

  const twist = h("span", { class: "twist" + (hasKids ? "" : " leaf") }, hasKids ? "▾" : "•");
  const childEls = [];
  if (hasKids) {
    for (const c of kids) {
      const r = TreeNode(c, depth + 1, ctx);
      childEls.push(r.el);
      r.agg.forEach((v) => accInto(agg, [v])); // bubble descendant effects into this node's rollup
    }
  }

  const row = h(
    "div",
    { class: "row" },
    twist,
    h("span", { class: "name" }, node.name),
    ctx.signatures && node.signature ? h("span", { class: "sig" }, node.signature) : null,
    edgeTxt ? h("span", { class: "edge" + (heur ? " heur" : "") }, edgeTxt) : null,
    node.fanout > 1 ? h("span", { class: "fanout" }, `×${node.fanout} fan-out`) : null,
    node.callSites > 1 ? h("span", { class: "sites" }, `${node.callSites}×`) : null,
    EffectGlyphs(own),
    ctx.predicates && node.guards
      ? h("span", { class: "guard" }, "⎇ ", h("span", { class: "guardtxt", title: node.guards }, node.guards))
      : null,
    ctx.hazards ? HazardMark(ctx.hazById.get(node.id)) : null,
    Trunc(node),
    Loc(node),
    hasKids ? Rollup(agg) : null, // shown only when this branch is collapsed (CSS)
  );
  const wrap = h("div", { class: "node" }, row);
  if (hasKids) {
    wrap.append(h("div", { class: "children" }, childEls));
    if (depth >= ctx.collapseLevel) {
      wrap.classList.add("collapsed");
      twist.textContent = "▸";
    }
    twist.addEventListener("click", () => {
      wrap.classList.toggle("collapsed");
      twist.textContent = wrap.classList.contains("collapsed") ? "▸" : "▾";
    });
  }
  return { el: wrap, agg };
}

// effects view: flat DFS list of effectful methods (deduped).
function flatEffectful(roots, ctx) {
  const out = [],
    seen = new Set();
  const walk = (n) => {
    const fe = filterEffects(n.effects, ctx.mode, ctx.tokens);
    if (fe.length && !seen.has(n.id)) {
      seen.add(n.id);
      out.push([n, fe]);
    }
    n.children.forEach(walk);
  };
  roots.forEach(walk);
  return out;
}

// ---- region views (state -> nodes) ----------------------------------------------------------------------
export function ctxOf(s) {
  const hazById = new Map();
  for (const m of s.hazardMarks || []) {
    if (!hazById.has(m.methodId)) hazById.set(m.methodId, []);
    hazById.get(m.methodId).push(m);
  }
  const level = parseInt(s.collapse, 10);
  return {
    view: s.view,
    mode: s.mode,
    tokens: s.tokens,
    signatures: s.signatures,
    predicates: s.predicates,
    hazards: s.hazards,
    hazById,
    collapseLevel: level > 0 ? level : Infinity,
  };
}

export function TreeView(s) {
  if (!s.tree) return h("div", {});
  const ctx = ctxOf(s);
  if (s.view === "effects") {
    const out = flatEffectful(s.tree.roots, ctx);
    return h(
      "div",
      { class: "flat" },
      out.map(([n, fe]) =>
        h("div", { class: "frow" }, h("span", { class: "fname" }, n.name), " ", EffectGlyphs(fe), " ", Loc(n)),
      ),
    );
  }
  const roots = s.view === "paths" ? s.tree.roots.filter((r) => subtreeHasEffect(r, ctx)) : s.tree.roots;
  return h(
    "div",
    {},
    roots.map((r) => TreeNode(r, 0, ctx).el),
  );
}
// the status line for the current tree/view (used after a render so counts reflect the projection).
export function treeStatus(s) {
  if (!s.tree) return "";
  const ctx = ctxOf(s);
  if (s.view === "effects") return `${s.tree.from} — ${flatEffectful(s.tree.roots, ctx).length} effectful method(s)`;
  const roots = s.view === "paths" ? s.tree.roots.filter((r) => subtreeHasEffect(r, ctx)) : s.tree.roots;
  return roots.length
    ? `${s.tree.from} — ${roots.length} root(s), view=${s.view}`
    : `${s.tree.from}: no effects reachable (matching filters) — try view=full`;
}

function RunCard(r, active, onSelect) {
  return h(
    "div",
    {
      class: "run" + (r.isLatest ? " latest" : "") + (r.storeId === active ? " active" : ""),
      onClick: () => onSelect(r.storeId),
    },
    h("div", { class: "id" }, r.storeId),
    h("div", { class: "meta" }, `${r.commit || ""}${r.branch ? " (" + r.branch + ")" : ""}${r.dirty ? " +dirty" : ""}`),
    h("div", { class: "meta" }, `${r.symbols.toLocaleString()} symbols · ${r.references.toLocaleString()} refs`),
  );
}
export function RunsList(s, actions) {
  if (!s.runs.length) return h("div", {}, "(no runs)");
  const active = s.storeId || (s.runs.find((r) => r.isLatest) || s.runs[0] || {}).storeId;
  return h(
    "div",
    {},
    s.runs.map((r) => RunCard(r, active, actions.selectStore)),
  );
}

const epRow = (e, onOpen) => h("div", { class: "ep", title: e.fqn, onClick: () => onOpen(e.fqn) }, e.route);
export function EpList(s, actions) {
  if (!s.eps.length) return h("div", {}, "…");
  const f = s.epFilter.trim().toLowerCase();
  const match = s.eps.filter((e) => !f || e.route.toLowerCase().includes(f) || e.fqn.toLowerCase().includes(f));
  const byKind = {};
  for (const e of match) (byKind[e.kind] ||= []).push(e);
  const kinds = Object.keys(byKind).sort();
  if (!kinds.length) return h("div", {}, "(no matches)");
  return h(
    "div",
    {},
    kinds.map((kind) => {
      const list = byKind[kind];
      const klist = h("div", { class: "klist" });
      const head = h(
        "div",
        { class: "khead" },
        (f ? "▾ " : "▸ ") + kind,
        " ",
        h("span", { class: "kcount" }, list.length),
      );
      const group = h("div", { class: "kind" + (f ? " open" : "") }, head, klist);
      let populated = false;
      const populate = () => {
        if (!populated) {
          mount(
            klist,
            list.map((e) => epRow(e, actions.openTree)),
          );
          populated = true;
        }
      };
      if (f) populate(); // filtered groups are small → render now
      head.addEventListener("click", () => {
        const open = group.classList.toggle("open");
        head.firstChild.textContent = open ? "▾ " + kind : "▸ " + kind;
        if (open) populate(); // lazy: a 5000-EP kind only hits the DOM when opened (no truncation)
      });
      return group;
    }),
  );
}

export function Chips(s, actions) {
  if (!s.tokens.length) return h("span", { class: "ms-ph" }, "providers…");
  return h(
    "span",
    {},
    s.tokens.map((t) =>
      h(
        "span",
        { class: "chip" },
        t,
        h(
          "b",
          {
            onClick: (e) => {
              e.stopPropagation();
              actions.toggleToken(t);
            },
          },
          "×",
        ),
      ),
    ),
  );
}

// ---- the static Shell (built once) ----------------------------------------------------------------------
// Returns { root, refs }. refs holds the containers the regions re-render into + the (uncontrolled) inputs.
// Input events call `actions`; the shell never reads state after construction.
export function Shell(actions) {
  const refs = {};
  const themeBtn = (mode, label) =>
    h("button", { dataset: { theme: mode }, onClick: () => actions.setTheme(mode) }, label);
  refs.theme = h(
    "div",
    { class: "theme", id: "theme" },
    themeBtn("light", "Light"),
    themeBtn("dark", "Dark"),
    themeBtn("system", "System"),
  );
  refs.storeDir = h("span", { class: "store" });
  refs.purge = h(
    "button",
    { class: "purge", title: "clear the client cache (in-memory + persisted)", onClick: () => actions.purge() },
    "purge cache",
  );
  const header = h("header", {}, h("h1", {}, "rig · explorer"), refs.storeDir, refs.purge, refs.theme);

  // sidebar
  const tab = (id, label) => h("button", { dataset: { tab: id }, onClick: () => actions.setTab(id) }, label);
  refs.tabRuns = tab("runs", "Runs");
  refs.tabEps = tab("eps", "Entry points");
  refs.runs = h("div", {}, "…");
  refs.eps = h("div", {}, "…");
  refs.epFilter = h("input", {
    id: "epFilter",
    placeholder: "filter entry points…",
    onInput: (e) => actions.setEpFilter(e.target.value),
  });
  refs.paneRuns = h(
    "div",
    { class: "pane on" },
    h("div", { class: "hint" }, "click a run to query that store (● = active)"),
    refs.runs,
  );
  refs.paneEps = h("div", { class: "pane" }, refs.epFilter, refs.eps);
  const aside = h("aside", {}, h("div", { class: "tabs" }, refs.tabRuns, refs.tabEps), refs.paneRuns, refs.paneEps);

  // splitter
  refs.splitter = h("div", { class: "splitter", title: "drag to resize" });

  // toolbar
  refs.from = h("input", {
    id: "from",
    placeholder: "search a symbol, or type an entry-point / pattern…",
    autocomplete: "off",
  });
  refs.results = h("div", { id: "results" });
  const fromwrap = h("div", { class: "fromwrap" }, refs.from, refs.results);
  refs.view = h(
    "select",
    { title: "projection", onChange: (e) => actions.setView(e.target.value) },
    h("option", { value: "paths" }, "paths"),
    h("option", { value: "full" }, "full"),
    h("option", { value: "effects" }, "effects"),
  );
  refs.filterMode = h(
    "select",
    { title: "effect filter (only XOR exclude)", onChange: (e) => actions.setMode(e.target.value) },
    h("option", { value: "none" }, "no filter"),
    h("option", { value: "only" }, "only"),
    h("option", { value: "exclude" }, "exclude"),
  );
  refs.chips = h("div", { class: "ms-control" }, h("span", { class: "ms-ph" }, "providers…"));
  refs.msSearch = h("input", {
    class: "ms-search",
    placeholder: "filter tokens…",
    autocomplete: "off",
    onInput: (e) => actions.renderMsList(e.target.value),
  });
  refs.msList = h("div", { class: "ms-list" });
  refs.msPop = h("div", { class: "ms-pop" }, refs.msSearch, refs.msList);
  refs.ms = h("div", { class: "ms disabled" }, refs.chips, refs.msPop);
  refs.collapse = h("input", {
    id: "collapse",
    type: "number",
    min: "1",
    placeholder: "collapse ≥",
    title: "auto-collapse at/below this depth — full tree is fetched, children one click away",
    onInput: (e) => actions.setCollapse(e.target.value),
  });
  const toggle = (label, key, title) =>
    h(
      "label",
      { class: "chk", title },
      h("input", { type: "checkbox", dataset: { k: key }, onChange: (e) => actions.setFlag(key, e.target.checked) }),
      " " + label,
    );
  refs.async = toggle("async", "asyncWalk", "walk async handoffs (refetches)");
  refs.sig = toggle("sig", "signatures", "show parameter signatures");
  refs.pred = toggle("pred", "predicates", "show control-dependence guards");
  refs.haz = toggle("haz", "hazards", "overlay hazard marks");
  refs.go = h("button", { class: "go", onClick: () => actions.openTree(refs.from.value.trim()) }, "Tree");
  const toolbar = h(
    "div",
    { class: "controls" },
    fromwrap,
    refs.view,
    refs.filterMode,
    refs.ms,
    refs.collapse,
    refs.async,
    refs.sig,
    refs.pred,
    refs.haz,
    refs.go,
  );

  // status + tree
  refs.spin = h("span", { class: "spin" });
  refs.status = h("span", { id: "status" });
  refs.statusbar = h("div", { id: "statusbar" }, refs.spin, refs.status);
  refs.tree = h("div", { class: "tree" });
  const section = h("section", {}, toolbar, refs.statusbar, refs.tree);

  refs.root = h("main", {}, aside, refs.splitter, section);
  return { root: h("div", {}, header, refs.root), refs };
}

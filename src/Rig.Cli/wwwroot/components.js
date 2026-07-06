// View layer: pure-ish `f(props) -> DOM` components built with h(). No fetch, no URL. `Shell` builds the
// static frame ONCE (inputs live here, uncontrolled → no focus loss) and exposes refs to the regions that
// re-render from state (RunsList / EpList / TreeView / Chips / StatusBar). Sub-components (TreeNode, RunCard,
// …) map 1:1 to future React components.

import { h, mount } from "./lib.js";

export const baseName = (p) => (p ? p.split(/[\\/]/).pop() : "");

// ---- effect filter + projection (pure) -------------------------------------------------------------------
const effMatch = (e, t) =>
  t === e.provider.toLowerCase() ||
  t === (e.provider + ":" + e.operation).toLowerCase();
export function filterEffects(effs, mode, tokens) {
  if (mode === "none" || !tokens.length) return effs;
  const toks = tokens.map((t) => t.toLowerCase());
  return mode === "only"
    ? effs.filter((e) => toks.some((t) => effMatch(e, t)))
    : effs.filter((e) => !toks.some((t) => effMatch(e, t)));
}
function subtreeHasEffect(node, ctx) {
  return (
    filterEffects(node.effects, ctx.mode, ctx.tokens).length > 0 ||
    node.children.some((c) => subtreeHasEffect(c, ctx))
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
  return node.file
    ? h("span", { class: "loc" }, `${baseName(node.file)}:${node.line}`)
    : null;
}
// Opaque/collapse seam badge: a folded node is a labelled leaf (its subtree hidden server-side). A collapse
// badge shows the hidden node count; the union of effects it hides rides on node.effects (rendered as glyphs).
// Toggle "raw" in the toolbar to refetch the unfolded tree. Null when the node did not fold.
function FoldBadge(node) {
  if (!node.foldKind) return null;
  const collapse = node.foldKind === "collapse";
  const text = collapse
    ? `⋯ ${node.foldLabel}${node.foldHidden ? ` +${node.foldHidden} hidden` : ""}`
    : `${node.foldLabel}`;
  return h(
    "span",
    {
      class: "seam seam-" + node.foldKind,
      title: collapse
        ? `collapsed seam — ${node.foldHidden} nodes folded away (effects shown are the union it touches). Toggle "raw" to expand.`
        : `opaque type — subtree hidden. Toggle "raw" to expand.`,
    },
    (collapse ? "" : "◻ ") + text,
  );
}
function Trunc(node) {
  if (!node.truncated) return null;
  if (node.truncationCause === "AlreadyExpanded")
    return h(
      "span",
      {
        class: "trunc",
        title: "cycle / shared callee — expanded elsewhere in this tree",
      },
      "⋯ shown above",
    );
  if (node.truncationCause === "BudgetCapped")
    return h(
      "span",
      { class: "trunc", title: "node-budget safety cap (50k) reached" },
      "⋯ budget",
    );
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
    else
      agg.set(k, {
        provider: e.provider,
        operation: e.operation,
        glyph: e.glyph,
        sites: e.sites,
      });
  }
}
// The subtree rollup shown ONLY when a branch is collapsed: distinct effects of everything hidden below (the
// node + all descendants, filtered), so a folded branch discloses what it touches at a glance. Hidden by CSS
// unless the node carries `.collapsed`; the effects are summed once at render (no recompute on toggle).
function Rollup(agg) {
  if (!agg.size) return null;
  const items = [...agg.values()].sort((a, b) =>
    (a.provider + a.operation).localeCompare(b.provider + b.operation),
  );
  const title =
    "subtree touches: " +
    items.map((e) => `${e.provider}:${e.operation}×${e.sites}`).join(", ");
  // Reuse the inline treatment: one glyph per DISTINCT effect kind with a ×N count (not a raw repeated strip).
  return h(
    "span",
    { class: "rollup", title },
    "↴ ",
    items.map((e) =>
      h(
        "span",
        { class: "eff" },
        e.glyph,
        e.sites > 1 ? h("span", { class: "sites" }, e.sites) : null,
      ),
    ),
  );
}

// TreeNode(node, depth, ctx): recursive. Returns { el, agg } — agg is the subtree effect rollup, bubbled up
// so each ancestor can show what its folded branch touches. ctx = {view, mode, tokens, collapseLevel,
// signatures, predicates, hazards, hazById}. Collapse ≥ level hides children in the DOM (one click reveals).
function TreeNode(node, depth, ctx) {
  // prune gate: "changed only" (diff overlay) beats the paths gate; else paths prunes to effectful branches.
  const gate = ctx.changedOnly
    ? (c) => subtreeHasDiff(c, ctx)
    : ctx.view === "paths"
      ? (c) => subtreeHasEffect(c, ctx)
      : null;
  const kids = gate ? node.children.filter(gate) : node.children;
  const hasKids = kids.length > 0;
  const heur = node.dispatchBasis === "heuristic";
  const edgeTxt =
    node.edgeKind && node.edgeKind !== "entry"
      ? node.edgeKind + (heur ? " ~heuristic" : "")
      : "";
  const dstat = diffStatus(node, ctx); // "add" | "eff+" | "eff-" | ""
  const dcls =
    dstat === "add"
      ? "add"
      : dstat === "eff+"
        ? "effadd"
        : dstat === "eff-"
          ? "effdel"
          : "";
  const dglyph =
    dstat === "add"
      ? "+"
      : dstat === "eff+"
        ? "~"
        : dstat === "eff-"
          ? "−"
          : "";

  const own = filterEffects(node.effects, ctx.mode, ctx.tokens);
  const agg = new Map();
  accInto(agg, own);

  const twist = h(
    "span",
    { class: "twist" + (hasKids ? "" : " leaf") },
    hasKids ? "▾" : "•",
  );
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
    {
      class: "row" + (dcls ? " row-" + dcls : ""),
      // Right-click a node to pivot: re-root here / who reaches this / entry points reaching this.
      onContextMenu: ctx.actions
        ? (e) => {
            e.preventDefault();
            ctx.actions.nodeMenu(node, e);
          }
        : null,
    },
    twist,
    dcls
      ? h(
          "span",
          {
            class: "diffmark diff-" + dcls,
            title:
              dstat === "add"
                ? "newly reachable"
                : dstat === "eff+"
                  ? "gained effect"
                  : "lost effect",
          },
          dglyph,
        )
      : null,
    h("span", { class: "name" }, node.name),
    ctx.signatures && node.signature
      ? h("span", { class: "sig" }, node.signature)
      : null,
    edgeTxt
      ? h("span", { class: "edge" + (heur ? " heur" : "") }, edgeTxt)
      : null,
    node.fanout > 1
      ? h("span", { class: "fanout" }, `×${node.fanout} fan-out`)
      : null,
    node.callSites > 1
      ? h("span", { class: "sites" }, `${node.callSites}×`)
      : null,
    FoldBadge(node),
    EffectGlyphs(own),
    ctx.predicates && node.guards
      ? h(
          "span",
          { class: "guard" },
          "⎇ ",
          h("span", { class: "guardtxt", title: node.guards }, node.guards),
        )
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

// Param-free FQN from a DocID ("M:NS.Type.Method(args)~λ0" -> "NS.Type.Method"), to match a tree node against
// an impact effect's `enclosing` (which is the param-free FQN, StripParams = FqnFromDocId).
function enclFqn(id) {
  let s = id.replace(/^[A-Za-z]:/, "");
  const paren = s.indexOf("(");
  if (paren >= 0) s = s.slice(0, paren);
  const lam = s.indexOf("~");
  if (lam >= 0) s = s.slice(0, lam);
  return s;
}
// diff status of a node under an active overlay, in precedence order:
//   "add"  — STRUCTURAL: this exact node is newly reachable in head (base couldn't reach it)
//   "eff+" — the method gained an effect (behavior changed on an already-reachable node)
//   "eff-" — the method lost an effect
// (removed-reach methods are base-only → absent from the head tree, listed in the banner instead.)
function diffStatus(node, ctx) {
  if (!ctx.diffOn) return "";
  if (ctx.addedReach.has(node.id)) return "add";
  const f = enclFqn(node.id);
  if (ctx.effAdded.has(f)) return "eff+";
  if (ctx.effRemoved.has(f)) return "eff-";
  return "";
}
function subtreeHasDiff(node, ctx) {
  return (
    !!diffStatus(node, ctx) || node.children.some((c) => subtreeHasDiff(c, ctx))
  );
}

// ---- region views (state -> nodes) ----------------------------------------------------------------------
export function ctxOf(s) {
  const hazById = new Map();
  for (const m of s.hazardMarks || []) {
    if (!hazById.has(m.methodId)) hazById.set(m.methodId, []);
    hazById.get(m.methodId).push(m);
  }
  const level = parseInt(s.collapse, 10);
  // diff overlay: active only when it was opened for THIS tree's `from` (guards against a stale overlay after
  // navigating elsewhere). Carries the STRUCTURAL reach delta (addedReach node ids) + the EFFECT delta
  // (effAdded/effRemoved enclosing FQNs) from the source impact EP.
  const ov =
    s.diffOverlay && s.tree && s.diffOverlay.from === s.tree.from
      ? s.diffOverlay
      : null;
  return {
    view: s.view,
    mode: s.mode,
    tokens: s.tokens,
    signatures: s.signatures,
    predicates: s.predicates,
    hazards: s.hazards,
    hazById,
    collapseLevel: level > 0 ? level : Infinity,
    diffOn: !!ov,
    addedReach: new Set(ov ? ov.addedReach : []),
    effAdded: new Set(ov ? ov.effAdded : []),
    effRemoved: new Set(ov ? ov.effRemoved : []),
    changedOnly: !!(ov && ov.changedOnly),
  };
}

// The diff-overlay banner shown above a tree opened from an impact EP card: base→head, the +/− method counts,
// a "changed only" prune toggle, and a clear. Only rendered when the overlay is active for this tree.
function DiffBanner(s, actions) {
  const ov = s.diffOverlay;
  if (!ov || !s.tree || ov.from !== s.tree.from) return null;
  const removed = ov.removedReach || [];
  const line1 = h(
    "div",
    { class: "diffbanner-row" },
    "diff vs ",
    h("b", {}, ov.base),
    " · ",
    h(
      "span",
      { class: "diff-add" },
      `+${(ov.addedReach || []).length} reachable`,
    ),
    " ",
    h("span", { class: "diff-del" }, `−${removed.length} reachable`),
    " · ",
    h("span", { class: "diff-effadd" }, `~${(ov.effAdded || []).length}`),
    "/",
    h("span", { class: "diff-effdel" }, `−${(ov.effRemoved || []).length}`),
    " effect-changed",
    h(
      "label",
      { class: "chk", style: "margin-left:10px" },
      h("input", {
        type: "checkbox",
        checked: !!ov.changedOnly,
        onChange: () => actions.toggleChangedOnly(),
      }),
      " changed only",
    ),
    h(
      "button",
      { class: "diff-clear", onClick: () => actions.clearDiff() },
      "clear",
    ),
  );
  // removed-reach methods are base-only (absent from the head tree) → list them (collapsed).
  const removedList = removed.length
    ? h(
        "details",
        { class: "diff-removed" },
        h(
          "summary",
          {},
          `${removed.length} method(s) no longer reachable (base-only)`,
        ),
        h(
          "div",
          { class: "diff-removed-list" },
          removed.map((n) => h("div", { class: "diff-del" }, "− " + n.name)),
        ),
      )
    : null;
  return h("div", { class: "diffbanner" }, line1, removedList);
}
export function TreeView(s, actions) {
  if (!s.tree) return h("div", {});
  const ctx = ctxOf(s);
  ctx.actions = actions; // threaded so a node's context menu can pivot (re-root / who-reaches / EPs-reaching)
  const banner = DiffBanner(s, actions);
  if (s.view === "effects") {
    const out = flatEffectful(s.tree.roots, ctx);
    return h(
      "div",
      { class: "flat" },
      banner,
      out.map(([n, fe]) =>
        h(
          "div",
          { class: "frow" },
          h("span", { class: "fname" }, n.name),
          " ",
          EffectGlyphs(fe),
          " ",
          Loc(n),
        ),
      ),
    );
  }
  const rootGate = ctx.changedOnly
    ? (r) => subtreeHasDiff(r, ctx)
    : s.view === "paths"
      ? (r) => subtreeHasEffect(r, ctx)
      : null;
  const roots = rootGate ? s.tree.roots.filter(rootGate) : s.tree.roots;
  return h(
    "div",
    {},
    banner,
    roots.map((r) => TreeNode(r, 0, ctx).el),
  );
}
// the status line for the current tree/view (used after a render so counts reflect the projection).
export function treeStatus(s) {
  if (!s.tree) return "";
  const ctx = ctxOf(s);
  if (s.view === "effects")
    return `${s.tree.from} — ${flatEffectful(s.tree.roots, ctx).length} effectful method(s)`;
  const roots =
    s.view === "paths"
      ? s.tree.roots.filter((r) => subtreeHasEffect(r, ctx))
      : s.tree.roots;
  return roots.length
    ? `${s.tree.from} — ${roots.length} root(s), view=${s.view}`
    : `${s.tree.from}: no effects reachable (matching filters) — try view=full`;
}

function RunCard(r, active, onSelect) {
  return h(
    "div",
    {
      class:
        "run" +
        (r.isLatest ? " latest" : "") +
        (r.storeId === active ? " active" : ""),
      onClick: () => onSelect(r.storeId),
    },
    // Lead with the human label (branch); show the commit ONCE as a muted mono chip so runs sharing a
    // branch (e.g. several "HEAD") stay distinguishable. Drops the old double-SHA / double-dirty repetition.
    h(
      "div",
      { class: "id" },
      h("span", { class: "label" }, r.branch || r.commit || r.storeId),
      r.branch ? h("span", { class: "sha" }, r.commit || r.storeId) : null,
      r.dirty ? h("span", { class: "dirty" }, "dirty") : null,
    ),
    h(
      "div",
      { class: "meta" },
      `${r.symbols.toLocaleString()} symbols · ${r.references.toLocaleString()} refs`,
    ),
  );
}
export function RunsList(s, actions) {
  if (!s.runs.length) return h("div", {}, "(no runs)");
  const active =
    s.storeId || (s.runs.find((r) => r.isLatest) || s.runs[0] || {}).storeId;
  return h(
    "div",
    {},
    s.runs.map((r) => RunCard(r, active, actions.selectStore)),
  );
}

const epRow = (e, onOpen) =>
  h(
    "div",
    { class: "ep", title: e.fqn, onClick: () => onOpen(e.fqn) },
    e.route,
  );
export function EpList(s, actions) {
  if (!s.eps.length) return h("div", {}, "…");
  const f = s.epFilter.trim().toLowerCase();
  const match = s.eps.filter(
    (e) =>
      !f ||
      e.route.toLowerCase().includes(f) ||
      e.fqn.toLowerCase().includes(f),
  );
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
      const group = h(
        "div",
        { class: "kind" + (f ? " open" : "") },
        head,
        klist,
      );
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

// ---- impact (store-vs-store diff) -----------------------------------------------------------------------
const shortEncl = (fqn) => (fqn ? fqn.split(".").slice(-2).join(".") : "");
function EffectLine(sign, e) {
  return h(
    "div",
    { class: "effline " + (sign === "+" ? "add" : "del") },
    sign + " ",
    h("span", { class: "eprov" }, `${e.provider}:${e.operation}`),
    e.resource ? " " + e.resource : "",
    h("span", { class: "eencl" }, " " + shortEncl(e.enclosing)),
  );
}
function HazLine(sign, hz) {
  return h(
    "div",
    { class: "effline haz " + (sign === "+" ? "add" : "del") },
    `${sign} ⚠ ${hz.type}(${hz.confidence})`,
    h("span", { class: "eencl" }, " " + shortEncl(hz.enclosing)),
  );
}
function EpDeltaCard(p, actions) {
  const hazN = p.hazardsAdded.length + p.hazardsRemoved.length;
  // Cards start COLLAPSED — with 200 shown, the headers ARE the scannable blast-radius index; the effect
  // list opens on demand. Head click toggles the body; a separate "↗ tree" button cross-links to the tree.
  const wrap = h("div", { class: "epd collapsed" });
  const twist = h("span", { class: "epd-twist" }, "▸");
  const openBtn = h(
    "button",
    {
      class: "epd-open",
      title: "open this entry point's call tree with the diff overlaid",
      onClick: (e) => {
        e.stopPropagation();
        actions.openDiffTree(p);
      },
    },
    "↗ diff tree",
  );
  const head = h(
    "div",
    {
      class: "epd-head",
      title: p.fqn,
      onClick: () => {
        twist.textContent = wrap.classList.toggle("collapsed") ? "▸" : "▾";
      },
    },
    twist,
    h("span", { class: "epd-route" }, p.route),
    // colored add/remove tallies so "mostly additions" vs "heavy removals" reads at a glance across 200 cards
    h(
      "span",
      { class: "epd-counts" },
      h("span", { class: "add" }, `+${p.added.length}`),
      "/",
      h("span", { class: "del" }, `−${p.removed.length}`),
      " eff",
    ),
    hazN
      ? h(
          "span",
          { class: "epd-haz" },
          `⚠ +${p.hazardsAdded.length}/−${p.hazardsRemoved.length}`,
        )
      : null,
    p.sharedMutationOnPath
      ? h(
          "span",
          {
            class: "epd-shared",
            title: "shared-state mutation still on the path",
          },
          "shared-state",
        )
      : null,
    openBtn,
  );
  const body = h(
    "div",
    { class: "epd-body" },
    p.added.map((e) => EffectLine("+", e)),
    p.removed.map((e) => EffectLine("−", e)),
    p.hazardsAdded.map((hz) => HazLine("+", hz)),
    p.hazardsRemoved.map((hz) => HazLine("−", hz)),
  );
  wrap.append(head, body);
  return wrap;
}
// Live progress panel shown while a cold diff streams (SSE phase events). Replaced by ImpactView on done.
export function ImpactProgress(lines) {
  return h(
    "div",
    { class: "impact-progress" },
    h(
      "div",
      { class: "prog-title" },
      h("span", { class: "prog-spin" }),
      " diffing… (cold: loads + derives BOTH stores — a few minutes)",
    ),
    h("pre", { class: "prog-log" }, lines.join("\n")),
  );
}
export function ImpactView(s, actions) {
  const d = s.impactData;
  if (!d)
    return h(
      "div",
      { class: "impact-empty" },
      "pick a base and head store above, then press Diff.",
    );
  const f = s.impactFilter.trim().toLowerCase();
  const match = d.perEp.filter(
    (p) =>
      !f ||
      p.route.toLowerCase().includes(f) ||
      p.fqn.toLowerCase().includes(f) ||
      p.added.some((e) => `${e.provider}:${e.operation}`.includes(f)) ||
      p.removed.some((e) => `${e.provider}:${e.operation}`.includes(f)),
  );
  const CAP = 200;
  const summary = h(
    "div",
    { class: "impact-summary" },
    h(
      "div",
      { class: "impact-head-row" },
      h("b", {}, d.base.label),
      " → ",
      h("b", {}, d.head.label),
      h(
        "span",
        {
          class: "mode-chip",
          title: "traversal mode — toggle 'async' in the toolbar",
        },
        s.impactAsync ? "async" : "sync",
      ),
      // Profile this diff: opens the telemetry dashboard on a fresh timed run (CPU/mem/disk over time +
      // phase rail). A cold recompute — hence an explicit link, not auto-loaded.
      h(
        "a",
        {
          class: "load-graphs",
          href:
            "/telemetry.html?csv=" +
            encodeURIComponent(
              `/api/impact/telemetry?base=${s.impactBase}&head=${s.impactHead}` +
                (s.impactAsync ? "&async=true" : ""),
            ),
          target: "_blank",
          rel: "noopener",
          title:
            "profile this diff — CPU/mem/disk over time (runs a fresh timed diff, then opens the telemetry dashboard)",
        },
        "⧉ load graphs",
      ),
    ),
    h(
      "div",
      { class: "impact-stats" },
      h("span", { class: "add" }, `+${d.addedEps.length} EPs`),
      h("span", { class: "del" }, `−${d.removedEps.length} EPs`),
      h(
        "span",
        {},
        `${d.affectedEpCount.toLocaleString()} affected (structural)`,
      ),
      h("span", {}, `${d.perEp.length.toLocaleString()} behavioral`),
    ),
    // Disclose the sync-mode limitation in the web too (the CLI header does the same): async/scheduled
    // handoffs are cut, so effects reachable only that way are excluded until you toggle async.
    s.impactAsync
      ? null
      : h(
          "div",
          { class: "impact-mode-note" },
          "SYNC mode — effects reachable only via async/scheduled handoffs are excluded; toggle async above to include them.",
        ),
  );
  const note = h(
    "div",
    { class: "impact-note" },
    `${match.length.toLocaleString()} EP(s) with a behavioral effect change${f ? ` matching "${f}"` : ""}${match.length > CAP ? ` — showing first ${CAP} (filter to narrow)` : ""}`,
  );
  return h(
    "div",
    { class: "impact" },
    summary,
    note,
    h(
      "div",
      {},
      match.slice(0, CAP).map((p) => EpDeltaCard(p, actions)),
    ),
  );
}

// The reverse-navigation drawer: "who reaches this node". Opened from a tree node's context menu, backed by
// /api/callers. entrypoints mode groups the rule-detected EPs by owning deployed service (the "which services
// can trigger this" lens); roots mode lists no-predecessor origins. Any row re-roots the tree onto itself.
// The node-inspector drawer (state key `s.callers`, but it serves all reverse/inventory lenses). Modes:
//   entrypoints — rule-detected EPs reaching the target, grouped by owning service (/api/callers)
//   roots       — flat no-predecessor origins (/api/callers)
//   reaches     — flat effect inventory FROM the target (/api/reaches)
//   path        — one concrete From->To path (/api/path), opened per-EP from the entrypoints view
export function CallersPanel(s, actions) {
  const c = s.callers;
  if (!c) return h("div", { class: "callers-drawer hidden" });
  const isCallers = c.mode === "entrypoints" || c.mode === "roots";
  const title =
    c.mode === "entrypoints" ? "entry points reaching"
    : c.mode === "roots" ? "callers of"
    : c.mode === "reaches" ? "effects reachable from"
    : "path to";

  const header = h(
    "div",
    { class: "callers-head" },
    h("span", { class: "callers-title" }, title),
    h("span", { class: "callers-target", title: c.target }, shortLabel(c.target)),
    // Lens toggles only for the two reverse-callers modes; reaches/path are single-shot views.
    isCallers
      ? h(
          "span",
          { class: "callers-modes" },
          h("button", { class: "callers-mode" + (c.mode === "entrypoints" ? " on" : ""), onClick: () => actions.openCallers({ id: c.target }, "entrypoints", c.async) }, "entry points"),
          h("button", { class: "callers-mode" + (c.mode === "roots" ? " on" : ""), onClick: () => actions.openCallers({ id: c.target }, "roots", c.async) }, "roots"),
          // async opt-in: also walk async-handoff edges (background workers / actor inboxes / events). Refetches.
          h("button", { class: "callers-mode" + (c.async ? " on" : ""), title: "also walk async/scheduled handoffs (background workers, actor inboxes, events)", onClick: () => actions.openCallers({ id: c.target }, c.mode, !c.async) }, "async"),
        )
      : c.mode === "path"
        ? h("span", { class: "callers-modes" }, h("span", { class: "path-from", title: c.from }, "from " + shortLabel(c.from)))
        : null,
    h("button", { class: "callers-close", title: "close", onClick: () => actions.closeCallers() }, "✕"),
  );

  let body;
  if (c.loading) {
    body = h("div", { class: "callers-empty callers-loading" }, h("span", { class: "spinner" }), "querying…");
  } else if (!c.matched) {
    const msg =
      c.mode === "reaches" ? "no effects reachable"
      : c.mode === "path" ? "no path (pattern matched, but no route exists)"
      : "nothing reaches this (synchronously)";
    body = h("div", { class: "callers-empty" }, msg);
  } else if (c.mode === "entrypoints") {
    // group EPs by service (loaded-in). "—" bucket = no deployments.json / unattributed.
    const groups = new Map();
    for (const ep of c.entryPoints || []) {
      const svc = ep.services && ep.services.length ? ep.services.map((x) => x.name).join(", ") : "—";
      (groups.get(svc) || groups.set(svc, []).get(svc)).push(ep);
    }
    body = h(
      "div",
      { class: "callers-list" },
      [...groups.entries()].map(([svc, eps]) =>
        h(
          "div",
          { class: "callers-svc" },
          h("div", { class: "callers-svc-head" }, h("span", { class: "svc-chip" }, svc), h("span", { class: "svc-count" }, `${eps.length}`)),
          eps.map((ep) =>
            h(
              "div",
              { class: "callers-ep", title: ep.fqn, onClick: () => actions.openTree(ep.fqn) },
              h("span", { class: "ep-kind" }, ep.kind),
              h("span", { class: "ep-route" }, ep.route),
              // per-EP: prove this reverse candidate with a concrete forward path EP -> target.
              h("button", { class: "ep-path", title: "show a concrete path from this entry point to the target", onClick: (e) => { e.stopPropagation(); actions.openPath(ep.fqn, c.target); } }, "path"),
            ),
          ),
        ),
      ),
    );
  } else if (c.mode === "roots") {
    body = h(
      "div",
      { class: "callers-list" },
      (c.roots || []).map((r) =>
        h(
          "div",
          { class: "callers-ep" + (r.forwardConfirmed ? "" : " reverse-only"), title: r.id, onClick: () => actions.openTree(r.id) },
          h("span", { class: "ep-route" }, r.name),
          r.forwardConfirmed ? null : h("span", { class: "ep-unconfirmed", title: "reverse-only: no confirmed forward path (dispatch over-approximation)" }, "~"),
        ),
      ),
    );
  } else if (c.mode === "reaches") {
    // flat effect inventory: reachable-method count + provider:op tallies (the "what does this touch" lens).
    body = h(
      "div",
      { class: "callers-list" },
      h("div", { class: "reach-count" }, `${(c.reachableCount || 0).toLocaleString()} reachable methods`),
      (c.effects || []).map((e) =>
        h(
          "div",
          { class: "callers-ep reach-eff" },
          h("span", { class: "eff-glyph" }, e.glyph),
          h("span", { class: "ep-route" }, `${e.provider}:${e.operation}`),
          h("span", { class: "svc-count" }, `×${e.sites}`),
        ),
      ),
    );
  } else {
    // path: the ordered concrete chain from -> target; each hop re-roots the tree.
    body = h(
      "div",
      { class: "callers-list" },
      (c.nodes || []).map((n) =>
        h(
          "div",
          { class: "callers-ep path-node", title: n.id, onClick: () => actions.openTree(n.id) },
          h("span", { class: "ep-route" }, n.name),
          n.file ? h("span", { class: "loc" }, `${baseName(n.file)}:${n.line}`) : null,
        ),
      ),
    );
  }

  // In-place text filter over the rendered rows — no state round-trip (keeps focus, matches the SPA's
  // uncontrolled-input convention). Only the list-y reverse modes need it.
  const filter =
    !isCallers || c.loading || !c.matched
      ? null
      : h("input", {
          class: "callers-filter",
          placeholder: "filter…",
          onInput: (e) => {
            const q = e.target.value.toLowerCase();
            const drawer = e.target.closest(".callers-drawer");
            drawer.querySelectorAll(".callers-ep").forEach((el) => {
              el.classList.toggle("filtered-out", q.length > 0 && !el.textContent.toLowerCase().includes(q));
            });
            drawer.querySelectorAll(".callers-svc").forEach((g) => {
              const anyVisible = [...g.querySelectorAll(".callers-ep")].some((el) => !el.classList.contains("filtered-out"));
              g.classList.toggle("filtered-out", !anyVisible);
            });
          },
        });
  return h("div", { class: "callers-drawer" }, header, filter, body);
}
// last dotted segment or two of a DocID/FQN, for a compact drawer title.
function shortLabel(id) {
  const noParen = id.split("(")[0];
  const parts = noParen.replace(/^[MFP]:/, "").split(".");
  return parts.slice(-2).join(".");
}

// ---- the static Shell (built once) ----------------------------------------------------------------------
// Returns { root, refs }. refs holds the containers the regions re-render into + the (uncontrolled) inputs.
// Input events call `actions`; the shell never reads state after construction.
export function Shell(actions) {
  const refs = {};
  const themeBtn = (mode, label) =>
    h(
      "button",
      { dataset: { theme: mode }, onClick: () => actions.setTheme(mode) },
      label,
    );
  refs.theme = h(
    "div",
    { class: "theme", id: "theme" },
    themeBtn("light", "Light"),
    themeBtn("dark", "Dark"),
    themeBtn("system", "System"),
  );
  refs.storeDir = h("span", { class: "store" });
  const modeBtn = (m, label) =>
    h(
      "button",
      { dataset: { app: m }, onClick: () => actions.setAppMode(m) },
      label,
    );
  refs.appmode = h(
    "div",
    { class: "appmode" },
    modeBtn("tree", "Tree"),
    modeBtn("impact", "Impact"),
  );
  refs.purge = h(
    "button",
    {
      class: "purge",
      title: "clear the client cache (in-memory + persisted)",
      onClick: () => actions.purge(),
    },
    "purge cache",
  );
  const header = h(
    "header",
    {},
    h("h1", {}, "rig · explorer"),
    refs.appmode,
    refs.storeDir,
    refs.purge,
    refs.theme,
  );

  // sidebar
  const tab = (id, label) =>
    h(
      "button",
      { dataset: { tab: id }, onClick: () => actions.setTab(id) },
      label,
    );
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
  const aside = h(
    "aside",
    {},
    h("div", { class: "tabs" }, refs.tabRuns, refs.tabEps),
    refs.paneRuns,
    refs.paneEps,
  );

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
    {
      title: "effect filter (only XOR exclude)",
      onChange: (e) => actions.setMode(e.target.value),
    },
    h("option", { value: "none" }, "no filter"),
    h("option", { value: "only" }, "only"),
    h("option", { value: "exclude" }, "exclude"),
  );
  refs.chips = h(
    "div",
    { class: "ms-control" },
    h("span", { class: "ms-ph" }, "providers…"),
  );
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
    title:
      "auto-collapse at/below this depth — full tree is fetched, children one click away",
    onInput: (e) => actions.setCollapse(e.target.value),
  });
  const toggle = (label, key, title) =>
    h(
      "label",
      { class: "chk", title },
      h("input", {
        type: "checkbox",
        dataset: { k: key },
        onChange: (e) => actions.setFlag(key, e.target.checked),
      }),
      label,
    );
  refs.async = toggle("async", "asyncWalk", "walk async handoffs (refetches)");
  refs.raw = toggle("raw", "rawTree", "show the raw unfolded tree — bypass opaque/collapse seam folds (refetches)");
  refs.sig = toggle("sig", "signatures", "show parameter signatures");
  refs.pred = toggle("pred", "predicates", "show control-dependence guards");
  refs.haz = toggle("haz", "hazards", "overlay hazard marks");
  refs.go = h(
    "button",
    { class: "go", onClick: () => actions.openTree(refs.from.value.trim()) },
    "Tree",
  );
  const toolbar = h(
    "div",
    { class: "controls" },
    fromwrap,
    refs.view,
    refs.filterMode,
    refs.ms,
    refs.collapse,
    refs.async,
    refs.raw,
    refs.sig,
    refs.pred,
    refs.haz,
    refs.go,
  );

  refs.treeToolbar = toolbar;

  // impact toolbar (base/head store pickers + Diff + filter) — hidden until appMode=impact
  refs.impactBase = h(
    "select",
    {
      title: "base store",
      onChange: (e) => actions.setImpactStore("base", e.target.value),
    },
    h("option", { value: "" }, "base…"),
  );
  refs.impactHead = h(
    "select",
    {
      title: "head store",
      onChange: (e) => actions.setImpactStore("head", e.target.value),
    },
    h("option", { value: "" }, "head…"),
  );
  refs.impactGo = h(
    "button",
    { class: "go", onClick: () => actions.loadImpact() },
    "Diff",
  );
  // Sync/async toggle for the diff — mirrors the tree toolbar's `async`. Off (sync) cuts async/scheduled
  // handoffs (the sound default); on walks them, surfacing effects reachable only via background workers /
  // actor inboxes / events. Reuses `toggle` → setFlag("impactAsync"), which reloads the diff on change.
  refs.impactAsync = toggle(
    "async",
    "impactAsync",
    "walk async/scheduled handoffs (background workers, actor inboxes, events)",
  );
  refs.impactFilter = h("input", {
    placeholder: "filter EPs / effects…",
    autocomplete: "off",
    onInput: (e) => actions.setImpactFilter(e.target.value),
  });
  refs.impactToolbar = h(
    "div",
    { class: "controls impact-toolbar hidden" },
    refs.impactBase,
    h("span", { class: "arrow" }, "→"),
    refs.impactHead,
    refs.impactAsync,
    refs.impactGo,
    refs.impactFilter,
  );

  // status + content (tree OR impact, toggled by appMode)
  refs.spin = h("span", { class: "spin" });
  refs.status = h("span", { id: "status" });
  refs.statusbar = h("div", { id: "statusbar" }, refs.spin, refs.status);
  refs.tree = h("div", { class: "tree" });
  refs.impact = h("div", { class: "tree impact-wrap hidden" });
  refs.callers = h("div", { class: "callers-mount" }); // reverse-nav drawer mounts here (overlays the tree area)
  const section = h(
    "section",
    {},
    refs.treeToolbar,
    refs.impactToolbar,
    refs.statusbar,
    refs.tree,
    refs.impact,
    refs.callers,
  );

  refs.root = h("main", {}, aside, refs.splitter, section);
  return { root: h("div", {}, header, refs.root), refs };
}

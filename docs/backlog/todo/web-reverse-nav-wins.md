# Web explorer — reverse-navigation "wins" (breadcrumbs · service lens · path-highlight)

Follow-ups from the reverse-navigation session (2026-07-06). The backend endpoints and the first SPA surfaces
shipped that session:
- `/api/callers` (roots + entrypoints, service-annotated), `/api/path`, `/api/reaches` + `DeploymentAttributionLookup`
  (commit `8b37545c`).
- SPA node context menu + reverse-nav drawer (who-reaches / EPs-by-service, + loading/filter/async/reroot-sync)
  (commit `25355c65`); P3 path + P4 reaches SPA surfaces (drawer modes + per-EP path affordance).

These three are the deferred, SPA-heavy "web-only" wins — they're what a GUI does that the CLI can't, and each
touches the shared SPA files (`components.js`/`main.js`/`store.js`/`index.html`), so they're sequential, not
parallelizable, and were left out of the first pass to avoid a half-integrated pile.

## W1 — Pivot history / breadcrumbs
Every pivot today (re-root, drawer EP click, impact cross-link) replaces the tree with no trail. Push a crumb
per navigation (forward-tree → callers → path → re-root) so an investigation is a navigable session, not a
single query. Back/forward should restore the prior `from` + drawer state. State: a `history[]` + cursor in the
store; the URL already carries `?from=` (extend to encode the drawer/mode). This is the real 10× over the CLI —
the CLI makes you retype an FQN; the web should let you walk a chain and step back.

## W2 — Service lens on TREE nodes
Deployment attribution currently rides only on the **callers EP list** (`EntryPointDto.Services`). Extend it to
every tree node: add `Services` to `TreeNodeDto`, thread `DeploymentAttributionLookup` through
`TreeMapper.MapNode` (using `loc?.File` — the lookup is file-path based), and color/badge nodes by owning
service so a call crossing a deployment boundary is visible at a glance (the dual-write smell from the context
map). Note: loaded-in is an upper bound; a node with no `File` returns `[]`. Cost is a per-node lookup +
a `TreeNodeDto`/`TreeMapper` change (shared contracts — do it as its own change, not mixed with a fold edit).

## W3 — Reachable-from / path-highlight overlay
Like the existing diff overlay, but for reachability: pick an EP (e.g. from the callers drawer) and highlight
the edges/paths in the current tree that reach the selected node — a visual "why is this reachable from X".
Backs onto `/api/path` (already shipped) and/or a reach-set from `/api/callers`. Pairs with W1 (the highlighted
path becomes a crumb).

## Notes
- `dead` and global `derive` were deliberately skipped for the web (dead is CLI-disabled; derive is a
  whole-store dump).
- A nested REVERSE tree (vs the current flat `callers --roots` list) needs a parent-tracking reverse walker in
  `FactPathFinder` — see `callers-reaches-underreport-followups.md` / the roots-is-flat note in
  `CallersQueryService`. Prereq for a "reverse tree" drawer view.

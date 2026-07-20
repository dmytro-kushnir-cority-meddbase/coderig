## CLI Tier-1 global flags — uniform across all query commands

**Status:** PROGRESS — `--format`, `--limit`, and `impact --time` are shipped. Remaining work is
`--time` on `derive`/`entrypoints` and `--no-cache` on the cached derive/entry-point paths.
**Source:** extracted from `docs/rig-review-issues.md`, 2026-06-25 (#10 / E2 Tier-1 deferred section)

### Shipped slices (verified against code 2026-07-02)

- **`--format text|tsv` ✅ SHIPPED (`94bfd886`, 2026-06-16)** — on *every* query command via
  `CommonOptions.Format()` (callers/path/reaches/tree/dispatch-fans/effects-diff/derive/entrypoints/
  impact/dead), with real TSV emitters (e.g. `PathCommand.cs:154-168`, `CallersCommand.cs:339-350`).
  NOTE: this had already shipped when the card was written 2026-06-25 — the card's "exists ad-hoc on
  3 of N commands" was a stale snapshot.
- **`--limit` ✅ SHIPPED** — `94bfd886` put it (with truncation footers) on `callers`
  (`CallersCommand.cs:57`, footers `:266`, `:365`) and `reaches` (`ReachesCommand.cs:33`), plus
  entrypoints/impact/derive/dead/facts. `tree --limit` shipped 2026-07-02: bounds tree NODES via
  `BuildTree`'s existing `maxNodes` budget (absent = the 50k safety cap, NOT unbounded — deliberate
  divergence from the flat listings); the node hitting the cap renders `⋯elided` / `budget-capped`;
  the limit is part of `TreeCacheKey` (a capped forest is a different tree, not a rendering). Tests:
  `TreeNodeBudgetTests` (incl. the fencepost: budget N fully expands N−1 nodes — the final unit's
  node is conservatively capped).
- **`--time` ✅ PARTIAL (`6a713836`, 2026-06-26)** — rich index-style instrumentation (per-phase
  `PhaseTimings` + OS/proc CPU/disk/RAM sampler + `TimingReport`, via a disposable `QueryTiming` helper)
  on `tree`/`callers`/`reaches`/`path`/`dispatch-fans`/`effects-diff`. It paid off immediately —
  attributed the reverse-query ~8s floor to **graph load (disk-IO, 1.5 GB read/query, CPU-idle), not
  traversal** (see [warm-graph-across-queries.md](../todo/warm-graph-across-queries.md)).
- **`impact --time` ✅ SHIPPED (`d2c71d1b`, 2026-07-06)** — uses the same `QueryTiming`/
  `TimingReport` model and telemetry CSV as indexing. The finer phase split remains separately tracked in
  [web timing unification](aaa-web-timing-unification-ui.md).
  **Still absent on `derive`/`entrypoints`.**

### Remaining work

- `--time` on `derive`/`entrypoints` — the `QueryTiming` helper exists; additive wiring.
- `--no-cache` — today exposed only by `tree` and `impact`, while effect and entry-point derivation also use
  `QueryCache`. Thread an opt-out through those command paths; commands with no cache need no flag.

The broader E2 flag-surface audit (dead aliases, mode-group validation, rename deprecations) was DONE
2026-06-14. This item is specifically the **Tier-1 generalization** that was explicitly deferred as
"additive, can land incrementally" (register: E2 Deferred section, `docs/rig-review-issues.md:165-168`).

### Effort

Additive per-command plumbing. Each flag can land independently. No breaking changes. No re-mine.

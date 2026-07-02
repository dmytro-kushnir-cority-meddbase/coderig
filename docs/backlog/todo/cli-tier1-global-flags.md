## CLI Tier-1 global flags — uniform across all query commands

**Status:** todo — mostly shipped; what remains is `tree --limit` + `--time`/`--no-cache` uniformity.
**Source:** extracted from `docs/rig-review-issues.md`, 2026-06-25 (#10 / E2 Tier-1 deferred section)

### Shipped slices (verified against code 2026-07-02)

- **`--format text|tsv` ✅ SHIPPED (`94bfd886`, 2026-06-16)** — on *every* query command via
  `CommonOptions.Format()` (callers/path/reaches/tree/dispatch-fans/effects-diff/derive/entrypoints/
  impact/dead), with real TSV emitters (e.g. `PathCommand.cs:154-168`, `CallersCommand.cs:339-350`).
  NOTE: this had already shipped when the card was written 2026-06-25 — the card's "exists ad-hoc on
  3 of N commands" was a stale snapshot.
- **`--limit` ✅ PARTIAL (`94bfd886`)** — shipped with truncation footers on `callers`
  (`CallersCommand.cs:57`, footers `:266`, `:365`) and `reaches` (`ReachesCommand.cs:33`), plus
  entrypoints/impact/derive/dead/facts. **Still absent on `tree`.**
- **`--time` ✅ PARTIAL (`6a713836`, 2026-06-26)** — rich index-style instrumentation (per-phase
  `PhaseTimings` + OS/proc CPU/disk/RAM sampler + `TimingReport`, via a disposable `QueryTiming` helper)
  on `tree`/`callers`/`reaches`/`path`/`dispatch-fans`/`effects-diff`. It paid off immediately —
  attributed the reverse-query ~8s floor to **graph load (disk-IO, 1.5 GB read/query, CPU-idle), not
  traversal** (see [warm-graph-across-queries.md](warm-graph-across-queries.md)).
  **Still absent on `derive`/`entrypoints`/`impact`.**

### Remaining work

- `tree --limit <n>` — the flood-prone command still has no truncation; needs the same truncation
  logic + footer as `callers`/`reaches`.
- `--time` on `derive`/`entrypoints`/`impact` — the `QueryTiming` helper exists; additive wiring.
- `--no-cache` — today only `tree` (`TreeCommand.cs:70`) and `impact` (`ImpactCommand.cs:56`); extend
  to every command with a render cache.

The broader E2 flag-surface audit (dead aliases, mode-group validation, rename deprecations) was DONE
2026-06-14. This item is specifically the **Tier-1 generalization** that was explicitly deferred as
"additive, can land incrementally" (register: E2 Deferred section, `docs/rig-review-issues.md:165-168`).

### Effort

Additive per-command plumbing. Each flag can land independently. No breaking changes. No re-mine.

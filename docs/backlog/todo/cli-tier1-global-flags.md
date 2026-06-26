## CLI Tier-1 global flags — uniform across all query commands

**Status:** todo — **`--time` slice ✅ SHIPPED (2026-06-26, commit `6a713836`)**: rich index-style instrumentation
(per-phase `PhaseTimings` + OS/proc CPU/disk/RAM sampler + `TimingReport`, via a disposable `QueryTiming` helper)
now on `callers`/`reaches`/`path`/`dispatch-fans`/`effects-diff`. It paid off immediately — attributed the
reverse-query ~8s floor to **graph load (disk-IO, 1.5 GB read/query, CPU-idle), not traversal** (see
[warm-graph-across-queries.md](warm-graph-across-queries.md)). Remaining flags (`--no-cache`/`--format`/`--limit`
uniformity) are the still-open work in this card.
**Source:** extracted from `docs/rig-review-issues.md`, 2026-06-25 (#10 / E2 Tier-1 deferred section)

### What

Promote `--time`, `--no-cache`, `--format text|tsv`, and `--limit <n>` from ad-hoc per-command presence to
a consistent **Tier-1 global** set that works on every query command where sensible:

- `--time` / `--no-cache`: today wired only to `tree`; `PhaseTimer` plumbing already exists. Other query
  commands (`reaches`, `callers`, `path`, `derive`) have no cache/timer toggle exposed.
- `--format text|tsv`: exists ad-hoc on 3 of N commands; `tree`/`path`/`callers` need TSV emitters.
- `--limit <n>`: absent from the flood-prone `reaches`/`tree`/`callers`; needs truncation logic added.

The broader E2 flag-surface audit (dead aliases, mode-group validation, rename deprecations) was DONE
2026-06-14. This item is specifically the **Tier-1 generalization** that was explicitly deferred as "additive,
can land incrementally" (register: E2 Deferred section, `docs/rig-review-issues.md:165-168`).

### Design (from E2 table)

**Tier 1 global (target state):**
- `--rules` (already consistent)
- `--format text|tsv` (default text) — valid on every command; requires TSV emitters for `tree`/`path`/`callers`
- `--limit <n>` (default unbounded) — requires truncation on `reaches`/`tree`/`callers`
- `--time` — valid on every command where the phase-timer makes sense
- `--no-cache` — valid on every command that has a render cache

### Effort

Additive per-command plumbing. Each flag can land independently. No breaking changes. No re-mine.

## CLI Tier-1 global flags — uniform across all query commands

**Status:** 🔵 IN PROGRESS (2026-06-26) — the **`--time` slice is being built** (rich index-style instrumentation —
per-phase `PhaseTimings` + OS/proc CPU/disk/RAM sampler + `TimingReport` breakdown — on `callers`/`reaches`/`path`/
`dispatch-fans`/`effects-diff`, which currently print help on `--time`). Motivated by the reverse-query cost finding:
`callers --async` has a ~8s per-call floor (median 8.1s over 35 sites = 5.2 min) that we can't attribute to
load-vs-traversal without `--time`. Remaining flags (`--no-cache`/`--format`/`--limit` uniformity) stay todo.
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

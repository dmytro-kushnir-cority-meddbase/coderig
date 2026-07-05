# Web timing unification — live progress + "where time went" for expensive ops

**Priority: top of todo.** Deferred from the impact-vis session (2026-07-05) to keep focus on the tree-diff
visualization first.

## Motivation
`rig impact` (and a cold `tree`/`derive` on a big store) runs for minutes. In the web explorer that's a dead
spinner — "sad". Meanwhile a full timing toolkit already exists and is UNUSED by the web:
- `Rig.Analysis/PhaseTimings.cs` — per-phase intervals on a master clock + `ResourceSampler` (CPU/mem/disk)
  attributed to each phase's `[Start,End)`.
- `Rig.Cli/Telemetry/TimingReport.cs` — `WriteBreakdown` (per-phase table: wall% / cpu:self-vs-sys / gc% /
  peakRAM / alloc/s / diskR/W) + `WriteCsv` (per-sample `rig-index-telemetry.csv`). Its own comment names
  **impact as the intended next consumer**.
- `tools/telemetry-dashboard.html` (+ `tools/telemetry-web.cs`) — a polished CPU/mem/disk-over-time + phase-rail
  canvas dashboard that renders that CSV.

## Plan (as scoped in the session)
- **C1 — live progress via web events (SSE). ✅ DONE this session** (commit `a78cd6d5`; endpoint now
  `/api/impact/stream?base&head&async`). `ImpactEngine.DiffAsync` takes a `Func<string,long,Task>? onPhase`
  awaited between the top-level phases (provenance / head-load / branch-compute / base-assemble); the explorer
  shows a live phase log via `ImpactProgress`, then GETs the now-warm `/api/impact`. This is COARSE progress,
  NOT the full `PhaseTimings`/`ResourceSampler` breakdown — C0/C2 below remain.
- **C0 — instrument `ImpactEngine.DiffAsync` with `PhaseTimings`** (the finer phases: resolve+open, head-load,
  head-derive, branch reach-sets, footprints, hazards, base-side, assemble — the `Tick` boundaries already
  exist as coarse SSE phases; promote them to real `PhaseTimings` intervals + `ResourceSampler`). This gives
  CLI `rig impact --time` the per-phase breakdown `TimingReport.WriteBreakdown` was built for (its own comment
  names impact as the intended next consumer) and a `rig-impact-telemetry.csv`.
- **C2 — UNIFY the `--time` viz (the enqueued ask).** Today `index --time` emits `TimingReport` +
  `rig-index-telemetry.csv` rendered by `tools/telemetry-dashboard.html`; the web impact view has its own ad-hoc
  SSE phase log (C1). Fold these into ONE timing model + ONE viz: serve `telemetry-dashboard.html` from the web
  host (it's already copied to `wwwroot/telemetry.html`), point it at the C0 CSV, and reuse it for `index --time`,
  `impact`, and cold queries alike — so terminal `--time` and the web explorer show the same CPU/mem/disk +
  phase-rail timeline instead of two divergent representations.

## Notes
- The on-disk diff cache is already correct as of this session (`ImpactCacheKey` now folds the tool MVID), so a
  warm re-diff is instant — this is purely about the FIRST (cold) run's observability.
- Unify the terminal `--time` and the web timing so there's one timing model + one viz, not two.

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
- **C0 — instrument `ImpactCommand.DiffAsync` with `PhaseTimings`** (coarse phases: resolve+open, head-load,
  head-derive, branch reach-sets, footprints, hazards, base-side, assemble). Non-invasive — wrap the top-level
  awaits. This also gives CLI `rig impact --time` the breakdown `TimingReport` was built for.
- **C1 — live progress via web events (SSE).** `/api/impact/stream?base&head` streams each phase-completion as
  it happens; the explorer shows a live phase log/bar instead of a dead spinner, then the result (or a signal
  to GET the now-cached `/api/impact`). Threads an `IProgress<(phase, ms)>` through `DiffAsync`.
- **C2 — "where time went" (bonus, cheap).** The diff also dumps `rig-impact-telemetry.csv` via the existing
  `TimingReport.WriteCsv` + `ResourceSampler`; reuse `tools/telemetry-dashboard.html` (serve/link it, or fold
  its canvas charts into the explorer) to render the CPU/mem/disk/phase timeline. UNIFY: the same viz should
  serve `index --time`, `impact`, and cold queries.

## Notes
- The on-disk diff cache is already correct as of this session (`ImpactCacheKey` now folds the tool MVID), so a
  warm re-diff is instant — this is purely about the FIRST (cold) run's observability.
- Unify the terminal `--time` and the web timing so there's one timing model + one viz, not two.

# Web timing unification — live progress + "where time went" for expensive ops

**Status (2026-07-06): C1 + C2 shipped, C0 partially shipped — only the fine-phase split below is open.**
C0/C2 landed in `d2c71d1b` ("feat(impact): load/timing graphs"), committed ~40min after this doc was first
written but never reflected back into it. Moved todo → progress accordingly; don't re-build what's shipped.

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
- **C0 — instrument `ImpactEngine.DiffAsync` with `PhaseTimings`. ✅ MOSTLY SHIPPED (`d2c71d1b`), coarse form.**
  `rig impact --time` now wraps a real `QueryTiming`/`PhaseTimings`/`ResourceSampler` scope, fed by the
  SAME 4 coarse `Tick` boundaries C1 already had (provenance / head-load / branch-compute / base-assemble) —
  `TimingReport.WriteBreakdown` renders the per-phase CPU/mem/disk table and `rig-impact-telemetry.csv` is
  written next to the head store. **Still open:** promoting those 4 coarse ticks into the FINER phases
  originally scoped (resolve+open, head-load, head-derive, branch reach-sets, footprints, hazards, base-side,
  assemble) — `ImpactEngine.DiffAsync` itself was untouched by `d2c71d1b`, so this is the one real remaining
  slice, and it's a precision nice-to-have (better attribution in the breakdown/CSV), not urgent.
- **C2 — UNIFY the `--time` viz. ✅ SHIPPED (`d2c71d1b`).** `/api/impact/telemetry?base&head&async` runs a
  sampled cold diff and returns the CSV; `wwwroot/telemetry.html` gained a `?csv=<url>` loader, was reskinned
  to the explorer's theme (dark/light), and `ImpactView` links to it ("⧉ load graphs"). Terminal `--time` and
  the web explorer now share one timing model (`PhaseTimings`/`TimingReport`) and one viz
  (`telemetry-dashboard.html`/`wwwroot/telemetry.html`) — verified against MedDBase main↔MR-10840 in that
  commit (438-sample CSV, 200 from the endpoint, dashboard rendering in both themes).

## Notes
- The on-disk diff cache is already correct as of this session (`ImpactCacheKey` now folds the tool MVID), so a
  warm re-diff is instant — this is purely about the FIRST (cold) run's observability.
- Remaining scope going forward is JUST the C0 fine-phase split above — everything else in the original plan
  is done.

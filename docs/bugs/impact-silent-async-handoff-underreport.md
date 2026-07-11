# `rig impact` silently under-reports async-handoff-reachable effect deltas

**Status:** ✅ **Fixed** (sync-mode disclosure banner) — the silent-ness is removed; the quantified/inline
variants (#1/#3 below) remain a deferred opt-in. Verified against source (`ImpactCommand`/`ImpactEngine`,
`CallersCommand.cs`).
**Kind:** silent under-disclosure (correctness-of-presentation). The per-EP data shown is correct for the
mode; the defect is that impact never told you the mode HID entry points, so the headline read as the whole
picture when it isn't.

## Resolution

The default sync-mode human header now emits a disclosure line
(`ImpactCommand.SyncModeDisclosure` → `WriteHeader`):

> `Note: computed in SYNC mode — paths through async/scheduled handoffs (background workers, actor inboxes,
> events) are not followed, so effects reachable only that way are excluded. Re-run with --async to include them.`

SYNC-only (async modes already state their scope in the header's `asyncNote`); human-only (tsv stays
machine-clean — `EmitTsv` never calls `WriteHeader`); zero extra compute. This is fix #2 below — it removes
the silent-ness and points at the actionable remedy (`--async`). Unit test: `ImpactAsyncDisclosureTests`.

The quantified hint (#1) and the inline `⚡` async-only tag (#3) were **deliberately deferred**: both need a
second async-mode reach pass over both stores (~2× the per-EP walk), too costly to impose on every default
run for a disclosure the one-flag `--async` re-run already provides. Revisit if users want the count inline.
**Repro DB:** `C:\Git\meddbase-analysis` — MR 10840 (`--base 49e453cc15a4-dirty --head b522241f89ab-dirty`).
**Affected command:** `rig impact` (all output formats).
**Reported by:** user triage, 2026-07-05 (root-caused; ruled out depth-cap and trace-failure — see below).

---

## Symptom

For MR 10840 the new DFS read path (`FetchCommentsText`/`ResolveCommentsText`) shows a behavioral
`+object_store read` delta on **1** entry point (`PersonModelTransactions.Inbox`), while the matching write
path shows on **193** (+52 hazard). The "1" reads as if the read sink is nearly unreachable. It isn't —
**3** EPs reach it; 2 are omitted with no signal.

## Root cause (verified)

`rig impact` runs in **sync mode by default** (`ImpactQueryService.DiffAsync` / the CLI `RunAsync` both
default `async = false` → `CommonOptions.Mode(async: false, …)` → `TraversalMode.SyncCut`). Sync mode cuts
async/scheduled (`handoff`) edges — the documented soundness default, same lens as `callers`/`reaches`. The
read sink is reached:

- **1 EP synchronously:** `PersonModelTransactions.Inbox`
- **2 EPs only via Echo async/scheduled handoff:** `PersonModelCacheSystem.Inbox`, `AppStartupProcesses.Startup`
  (confirmed with `rig callers … --entrypoints --async`)

So sync-mode impact correctly reports 1. **The defect is that it does so silently.** Its header
(`… 244 entry point(s) with a changed behavior …`) presents the sync-mode count as the whole picture.

`rig callers` prints the mitigation right there — `CallersCommand.cs:588`:

> `… +N more entry point(s) reach this via async/scheduled handoff (not shown) — re-run with --async.`

plus the zero-EP variant at `:492`. `rig impact` prints no equivalent hint.

### Ruled out (both floated, both wrong)
- **Depth cap:** `ImpactEngine.ComputeFootprints` / `ComputeReachSets` / `ComputeHazardSets` already pass
  `maxDepth: int.MaxValue` (a prior fix). Not it. *(But see the sibling bug —
  [impact-reach-walks-silent-maxnodes-cap.md](impact-reach-walks-silent-maxnodes-cap.md) — for the `maxNodes`
  cap those same walks still inherit silently.)*
- **Effect-trace failure:** rig keys the effect at the call site `ResolveCommentsText`, reached through
  ordinary call edges; the LanguageExt composition traces fine and the `Task.Run` inside `RunSync` never
  needs crossing. Not it.

## Fix direction (ranked; report only — no code touched)

1. **Port the async-handoff hint to impact (highest value, lowest cost).** When an EP's async-mode footprint /
   hazard set differs from its sync set, emit — in the header and/or per affected sink —
   `; M additional entry point(s) gain/lose effects only via async handoffs — re-run with --async.` This kills
   the silent suppression, matching the "no silent caps" rule.
   - **Reuse nuance:** the callers hint is driven by `AsyncReachableEpCount()`, a **local function** inside
     `CallersCommand.RunEntryPointsAsync` that closes over `graph`/`toPattern`/`mode`/`maxDepth` — it is a
     reusable *pattern*, not a drop-in helper. impact needs its own probe: a second per-EP reach pass under
     `TraversalMode.AsyncExact` over the already-loaded head graph, counting EPs whose async footprint exceeds
     their sync footprint. Guard it like callers does (skip when already `--async`, or when the graph has no
     `handoff` edge — an O(E) presence check).
2. **State the mode in the banner (cheap, do regardless of #1).** One line, e.g.
   `Impact computed in SYNC mode; async-handoff-reachable effects excluded (see --async).` So the counts are
   never read as unconditional. This is a one-line render change with zero extra compute.
3. **(Richer, optional) Compute sync + async footprints in one run and tag async-only deltas `⚡`** — mirroring
   how `reaches` tags direct / async-scheduled `⚡` / fan-out. impact already parametrizes `mode`, so a second
   per-EP walk avoids the re-run entirely. Cost ≈ 2× the per-EP walk — acceptable for a minutes-long batch job.
   Supersedes #1's hint with an in-place mark.

**Recommendation:** ship **#2 now** (trivial, removes the "unconditional count" misread on its own) and **#1**
(the actionable per-sink hint) as the real fix; treat **#3** as a follow-up if reviewers want async deltas
inline rather than behind a re-run.

## Test to add

Two-store fixture where a sink effect is reachable from EP-A synchronously and EP-B only across a `handoff`
edge. Assert the default (sync) run lists EP-A, omits EP-B, **and emits the async-handoff hint naming the
count 1**; assert `--async` lists both and emits no hint. The existing behavioral-delta two-store fixtures
under `tests/Rig.Tests/Cli` are the natural home.

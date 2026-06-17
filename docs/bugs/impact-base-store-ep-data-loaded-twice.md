# Perf: `rig impact` loads the BASE store's entry-point data twice

**Status:** 🟡 Open — verified against source (`ImpactCommand.cs`, `Reads.cs`, `EntryPointContext.cs`).
**Kind:** redundant work / perf (not a correctness bug — output is identical).
**Repro DB:** `C:\Git\meddbase-analysis` (full solution; needs a base commit store, so run with `--base <ref>` against an indexed base, or `--base-store <path>`).
**Affected command:** `rig impact` (both the default and `--per-ep` paths).

---

## Summary

`rig impact` diffs a branch store against a base-commit store. The **branch** side is well-optimized:
`epData` is loaded once (`ImpactCommand.cs:190`) and *shared* into both entry-point derivation and effect
derivation (the deliberate sharing the `EntryPointContext.DeriveEntryPointsAsync` doc-comment at
`EntryPointContext.cs:42` calls out). The graph is loaded once per store, and the author already collapsed
the `--per-ep` base loads into a single pass (`ComputeBehavioralAndFootprintsAsync`, "2 store loads, not 4"
— `ImpactCommand.cs:251,631`).

The **base** side was not given the same treatment. The base store is opened in **two independent
`RigDbContext` instances** that don't coordinate, and the heavy `Reads.LoadFactEntryPointDataAsync` runs
**twice** against the base store (plus `DeriveEntryPointsAsync` twice in the `--per-ep` path).

`LoadFactEntryPointDataAsync` (`Reads.cs:559`) is heavy: it reads *all* base-type edges, *all* interface
edges, *all* method symbols (~217k on MedDBase), *all* type symbols, and *all* ctor refs. Running it twice
on the base store roughly doubles that read.

## Origin — corrected reading of a `rig tree` trace

A static `rig tree ImpactCommand.RunAsync` shows `LoadFactGraphAsync` ×4 and `AnalysisRuleSet.LoadForSolution`
×8 (with `↺seen` markers). That is a **static** call-site count, not the runtime path, and it over-reports:

- The fact graph is loaded **once per store** at runtime (branch ×1 `:175`, base ×1). The
  `DeriveHandoffEntryPointsAsync → LoadFactGraphAsync` node (`Reads.cs:552`) **never executes** on a graphed
  store — `call_edges` exists, so the raw-ADO fast path (`Reads.cs:519-548`) is taken. The two base-store
  graph-load nodes are the `--per-ep` vs non-`--per-ep` branches, which are mutually exclusive.
- So the graph-load is NOT redundant. Only the base **EP-data double-load** (below) and the rule re-reads
  (separate issue) survive verification.

## Runtime trace (verified)

`--per-ep` path:

| Step | Site | Base-store reads |
|---|---|---|
| `ComputeEpDiffAsync` | `:357` opens base ctx **#1** | `LoadFactEntryPointDataAsync` `:358`, `DeriveEntryPointsAsync` `:359` |
| `ComputeBehavioralAndFootprintsAsync` | `:646` opens base ctx **#2** (same `baseDbPath`) | `LoadFactGraphAsync` `:647`, `LoadDeadCodeMethodsAsync` `:652`, **`LoadFactEntryPointDataAsync` `:653`**, **`DeriveEntryPointsAsync` `:654`**, `LoadInvocationRefsAsync` `:655`, `LoadThrowRefsAsync` `:656` |

Non-`--per-ep` path:

| Step | Site | Base-store reads |
|---|---|---|
| `ComputeEpDiffAsync` | `:357` opens base ctx **#1** | `LoadFactEntryPointDataAsync` `:358`, `DeriveEntryPointsAsync` `:359` |
| `ComputeBehavioralDeltaAsync` → `ReachEffectsAsync` | `:498` opens base ctx **#2** | `LoadFactGraphAsync` `:466`, `EventSubscriptionSitesAsync` `:469`, `LoadInvocationRefsAsync` `:472`, `LoadThrowRefsAsync` `:473`, **`LoadFactEntryPointDataAsync` `:474`** |

In both paths: base store opened **2×**, `LoadFactEntryPointDataAsync` runs **2×** on the base store.
`ComputeEpDiffAsync` derives the base EP set; `ReachEffectsAsync` re-loads the same `epData` only to use
`BaseEdges` + `CtorRefs` for `DeriveEffects` (it does not derive EPs), so the load is duplicated even though
the *derivation* is not.

## Fix direction

Open the base store **once** and share its `epData` (and the derived base EP set) across the EP-diff and the
behavioral/footprint compute — mirroring what the branch side already does at `:190-191`.

Concretely: have `ComputeEpDiffAsync` and `ComputeBehavioral*Async` take a shared base `RigDbContext` and a
shared `FactEntryPointData` (loaded once by the caller), or fold the EP-diff into
`ComputeBehavioralAndFootprintsAsync` so the single base load there feeds the EP set-diff too. The base EP
set the EP-diff needs (`baseSet.Derived.Concat(baseSet.PromotedOrigins)`, `:360`) is exactly what
`ComputeBehavioralAndFootprintsAsync` already derives at `:654`.

Net effect: base store opened 1×, base `LoadFactEntryPointDataAsync` + `DeriveEntryPointsAsync` run 1× each.

## Out of scope (tracked separately)

`AnalysisRuleSet.LoadForSolution` (`AnalysisRuleSet.cs:71`) has no memoization, so a single `impact` run
re-reads `builtin-rules.json` + `~/.rig/rig.rules.json` + the solution `rig.rules.json` ~15–19 times and
rebuilds the merged set via `Concat().ToArray()` each time (every `FactXxxRuleProvider.LoadForWorkingDirectory`
and `ShapingRuleSet.Load`'s four providers call it fresh). Cheap per call, pure waste in aggregate — worth a
process-lifetime memo keyed on (anchor, extraRules, file mtimes). Lower priority; see a future
`rules-loadforsolution-no-memo.md` if it proves to matter.

## Test to add

A test that counts base-store opens (or `LoadFactEntryPointDataAsync` invocations) for one `impact` run with
a resolved base store — assert it is 1, not 2 — for both the default and `--per-ep` paths. The two-store
fixtures under `tests/Rig.Tests` for the behavioral-delta feature are the natural home.

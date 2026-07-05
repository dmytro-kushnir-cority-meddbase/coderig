# `rig impact`'s per-EP reach walks inherit a silent 20k-node cap

**Status:** 🟡 Open — verified against source (`ImpactEngine` reach walks, `FactPathFinder.cs`).
**Kind:** latent silent truncation (correctness). Unbitten on MR 10840 (every relevant EP reaches <3k nodes)
but a false "behavior unchanged" is possible on a large enough EP.
**Repro DB:** `C:\Git\meddbase-analysis` (29k-method graph — the class of store where it could trigger).
**Affected command:** `rig impact` (per-EP footprint / reach-set / hazard walks).
**Found alongside:** [impact-silent-async-handoff-underreport.md](impact-silent-async-handoff-underreport.md)
(same code path; a distinct issue).

---

## Summary

impact's three per-EP walks — `ImpactEngine.ComputeFootprints`, `ComputeReachSets`, `ComputeHazardSets`
(post-extraction home: `src/Rig.Cli/Impact/ImpactEngine.cs`; currently `ImpactCommand.cs`) — all correctly
pass `maxDepth: int.MaxValue` (a prior fix that stopped effects deeper than 20 hops from churning the diff).
But they call `FactPathFinder.ReachesFromEachSeed` / `ReachesInfoFromEachSeed` **without** a `maxNodes`
argument, so each inherits the default **`maxNodes = 20000`** (`FactPathFinder.cs:258`, and the
`ReachesWithFanout` overload).

The per-seed BFS stops on `while (queue.Count > 0 && info.Count < maxNodes)` (`FactPathFinder.cs:406`) and
returns the partial set with **no truncation signal** — unlike `BuildTree`, which marks `BudgetCapped`
(`FactPathFinder.cs:619-622`) when it hits its node budget. So an EP whose reachable set exceeds 20k nodes
would drop its tail effect / hazard deltas, and impact would render "behavior unchanged" (or an
under-counted delta) for that EP with **zero warning**.

On MR 10840 no EP's reach approaches 20k, so the result is correct today — this is latent, not active.

## Why it matters on this graph

MedDBase is ~29k methods. A high-fan-out entry point (e.g. an app-startup path or a broad actor inbox) can
plausibly exceed 20k reachable nodes once `R:` field/property-access leaf nodes are unioned in
(`ComputeReachSets` adds those). The failure is the worst kind for a review tool: silent, and it presents as
a *clean* diff.

## Fix direction

For a whole-store batch job that already accepts a minutes-long runtime, the depth cap was removed for exactly
this reason; the node cap should get the same treatment:

1. **Raise/remove the node budget** on impact's three walks — pass an explicit `maxNodes: int.MaxValue` (or a
   large store-scaled bound) as was already done for `maxDepth`. Simplest; matches the depth fix precedent.
2. **At minimum, propagate a truncation flag.** Have the per-seed reach return whether it hit the cap (mirror
   `BuildTree`'s `BudgetCapped`), and have impact **count and warn** how many EPs truncated —
   `N entry point(s) exceeded the reach budget; their deltas may be incomplete.` Never drop the tail silently.

Prefer #1 (correctness by construction) and add #2's flag as defense-in-depth so any future re-capping can't
silently regress.

## Test to add

A synthetic graph seeded so one EP's forward reach exceeds a small injected `maxNodes`, asserting either (a)
with the budget raised, the full delta is reported, or (b) with the flag propagated, the truncation is counted
and surfaced — not silently dropped. Unit-level against `FactPathFinder` per-seed reach + an impact-level
assertion on the warning line.

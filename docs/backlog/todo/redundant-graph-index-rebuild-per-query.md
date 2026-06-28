# Redundant `GraphIndex` rebuild per traversal — `impact` pays it 6× / run

**Status:** todo · **Found:** 2026-06-28 (app-wide query-redundancy investigation; opus agent, verified against code) · **Family:** perf / query-path-redundancy
**Related:** [[warm-graph-across-queries]] (the across-command structural lever) · `perf-redundant-work-per-ep.md` (F1–F9, the already-mined micro-redundancy seam) · fed by [[alloc-effect-detector]] (the detector that would surface this class automatically)

## The finding (CONFIRMED against the code)
`FactPathFinder.BuildIndex(graph)` (`FactPathFinder.GraphIndex.cs:280`) is rebuilt on **every** traversal call — its ~13 callers are essentially the whole query surface (`BuildTree`, `Find`, `Reaches*`, `ReachableFromAll`, `ReachedBy*`, `EntryRootsReaching`, `DispatchFanReport`, `AllDispatchEdges`, `BuildReverseMaps`). Each rebuild does the full adjacency build + four-key sort of every adjacency list + `MethodsByStrippedType`/`ImplsByInterface`/`StrippedBaseEdges`/context-families/mined-dispatch construction.

**The cleanly-fixable redundancy: `rig impact` rebuilds the index 6× per run** over byte-identical graphs. Verified at `ImpactCommand.cs:357/370/379` — on the cold (cache-miss) path, `ComputeReachSets` → `ReachesFromEachSeed`, `ComputeFootprints` → `ReachesInfoFromEachSeed`, `ComputeHazardSets` → `ReachesFromEachSeed`, all over the **same** `headData.Graph`, each calling `BuildIndex` afresh — **3× per side × (HEAD + BASE) = 6×**. The three passes run unconditionally on a cold impact; warm (cache hit) runs none.

## Scope of the fix
- **Cheap half (NON-CONFLICT, do first):** in `ImpactCommand.ComputeBranchSideAsync`/`ComputeBaseSideAsync`, build the `GraphIndex` **once per side** and pass it to all three passes. `ImpactCommand.cs` is fully clear of the concurrent render edits.
- **Engine half (coordinate/defer):** add an overload of `ReachesFromEachSeed`/`ReachesInfoFromEachSeed` (or a small internal `Reaches` that accepts a prebuilt `GraphIndex`). Lands in `FactPathFinder.cs` — same class the dispatch/render agent is editing; coordinate or defer until that settles. The index is already designed to be shared (its `DescendantsCache` is a `ConcurrentDictionary` precisely so the parallel `ReachesFromEachSeed` can share one).

## What this is NOT
- NOT the across-command cold-graph LOAD (`warm-graph-across-queries.md`) — that's the ~5s/~1.5 GB-disk structural lever and dwarfs this. This card is the *within-`impact`* CPU waste of rebuilding the index over an already-loaded graph.
- NOT the seed micro-redundancies (duplicate graph/EP/rule loads in one command): the investigation confirmed those are **already fixed** (F1–F9 in `perf-redundant-work-per-ep.md`; the "3–4× LoadFactGraphAsync" was mutually-exclusive `⎇` branches, not repeats). No ROI left there.

## Needs measurement
Size the 6× `BuildIndex` vs the 2× graph load on the **MedDBase** graph (via `bench/Rig.Benchmarks` `gcloop` once builds are safe). The graph load likely dominates (per the warm-graph measurement), but a full adjacency-sort + descendant-closure build of a 41k-closure-inflated graph isn't free; structurally it IS 3×-per-side redundant regardless.

## Pre-size hygiene already applied
`BuildIndex` now `EnsureCapacity`s `Adjacency`/`Nodes` (`FactPathFinder.GraphIndex.cs`) — immaterial on rig's tiny self-graph (2443→2449 KB, noise) but scales with graph size; the resize churn was ruled out as the cost, confirming the per-call *content* (the dicts/sorts/lookups) is what repeats — which is what hoisting/caching the index eliminates.

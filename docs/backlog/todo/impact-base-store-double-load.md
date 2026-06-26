## `rig impact` — base store entry-point data loaded twice

**Status:** todo — open perf bug
**Source:** promoted from `docs/bugs/impact-base-store-ep-data-loaded-twice.md` (🟡 open); leave the bugs/
file in place as the detailed record. 2026-06-25.

### Summary

`rig impact` opens the base store in **two independent `RigDbContext` instances** that don't coordinate, so
`Reads.LoadFactEntryPointDataAsync` (reads all base-type edges, all interface edges, ~217k method symbols,
all type symbols, all ctor refs) runs **twice** against the base store on every `impact` run — roughly
doubling the base-store read. Not a correctness bug; output is identical.

Both the `--per-ep` and default paths are affected. See the full runtime trace in the bugs/ file.

### Fix direction

Open the base store **once** and share its `epData` (and the derived base EP set) across both
`ComputeEpDiffAsync` and `ComputeBehavioral*Async` — mirroring what the branch side already does at
`ImpactCommand.cs:190-191`.

Concretely: have `ComputeEpDiffAsync` and `ComputeBehavioral*Async` take a shared base `RigDbContext` and a
shared `FactEntryPointData` (loaded once by the caller), or fold the EP-diff into
`ComputeBehavioralAndFootprintsAsync` so the single base load there feeds the EP set-diff too. The base EP
set the EP-diff needs (`baseSet.Derived.Concat(baseSet.PromotedOrigins)`, `:360`) is already derived by
`ComputeBehavioralAndFootprintsAsync` at `:654`.

Net effect: base store opened 1×, `LoadFactEntryPointDataAsync` + `DeriveEntryPointsAsync` each run 1×.

### Test to add

Count base-store opens (or `LoadFactEntryPointDataAsync` invocations) for one `impact` run with a resolved
base store — assert 1, not 2 — for both the default and `--per-ep` paths. Natural home: the two-store
fixtures in `tests/Rig.Tests` for the behavioral-delta feature.

### Detailed record

`docs/bugs/impact-base-store-ep-data-loaded-twice.md` — full runtime trace with line-number references.

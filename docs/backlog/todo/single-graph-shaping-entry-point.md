## Refactor: single graph-shaping entry point (`LoadShapedGraphAsync`)

**Status:** PARTIAL (audited 2026-07-02) — `Reads.LoadShapedGraphAsync` SHIPPED (`9caef5d1`) and is the
shaping entry point for `derive` (`DeriveCommand.cs`) and `impact` (`ImpactCommand.cs:298` head, `:1201`
base); test `LoadShapedGraphTests.cs`. That closed the derive double-load (F1) and impact's delivery-edge
gap. **Remaining live work: route `GraphMaterializer` through the shared entry point** (it still persists
via its own `RewriteGenericFactories`/`AddDeliveryEdges` sequence, `GraphMaterializer.cs:86,100`).
**EF-fallback routing is a WON'T DO** (2026-06-23 decision — see the residual note in
`../done/perf-redundant-work-per-ep.md`): the fallback only fires on `--no-graph`/pre-graph stores and the
fix risks the contended shaping path. So acceptance criteria 1–2 below are trimmed accordingly.

### Problem

The reachability-shaped call graph (`classify methodGroup→handoff` → `RewriteGenericFactories` → delivery
edges) is assembled in **three scattered, partial places**:

- `GraphMaterializer.BuildFromGraphAsync` — bakes classify + factory + delivery into the persisted `call_edges`.
- `DeriveCommand.RunAsync` — hand-rolls `LoadFactGraphAsync → RewriteGenericFactories → LoadDeliverySites →
  AddDeliveryEdges` inline (for FR-10 `event_cycle`).
- `FactPathFinder.ShapeGraph` (used by `impact` + the EF-fallback traversals) — does factory + cut + context
  but **omits delivery edges entirely**, so `impact`'s per-EP reach and EF-fallback `reaches`/`tree`/`path` do
  not see publish→consumer delivery at all.

Three definitions that can drift, a real coverage gap (impact/EF-fallback miss delivery edges), and a
documented-only ordering invariant (the delivery join consumes the classifier's `Kind=handoff` output, so it
must run after — enforced by comment, not structure).

### Proposed solution

One Storage entry point `Reads.LoadShapedGraphAsync(context, RuleSet rules, ct)` that returns the fully
in-memory-shaped graph: `LoadFactGraphAsync` (load + classify) → `RewriteGenericFactories` → delivery edges
(`LoadDeliverySitesAsync` + `AddDeliveryEdges`) → attach cut/context metadata. Every in-memory consumer
(`derive`, `impact`, EF-fallback traversals) calls it; `GraphMaterializer` persists **exactly its
edge-creating output** to `call_edges` (cut/context stay traversal-time, as today). Net:

- **Closes the gap**: `impact` per-EP reach + EF-fallback traversals gain delivery edges uniformly.
- **Resolves review finding #1a**: the graph is loaded + shaped **once** in `derive` and reused by both the
  handoff-EP derivation and the cycle pass (was loaded twice — `DeriveHandoffEntryPointsAsync` internal +
  `DeriveCommand:115`).
- **Dissolves the ordering coupling**: stage order lives inside one function, tested — not a cross-call comment.
- One shaping definition; `call_edges` becomes purely its materialization.

### Acceptance criteria

- [x] `derive` + `impact` obtain the shaped graph from the one entry point (`9caef5d1`). EF-fallback:
  WON'T DO (see Status). Remaining: `GraphMaterializer` — no hand-rolled `classify→factory→delivery`
  sequence at its call site.
- [x] `impact --per-ep` traverses delivery (event/actor) edges (`9caef5d1`). EF-fallback: WON'T DO.
- [x] `derive` loads the graph once (F1, `9caef5d1`).
- [ ] Behavior otherwise unchanged: `rig derive` output byte-identical; MedDBase `event_cycle` 24/all-high;
  persisted `call_edges` count unchanged; full suite green.
- [ ] `dead`'s unshaped-CHA-superset requirement still met (the raw/`--raw` path bypasses delivery shaping).

### Related: parallelise the independent query-side loads — INVESTIGATED, DOES NOT PAY (do not rebuild)

The derive (and impact) commands issue several **data-independent** loads — graph edges, EP data, delivery
sites, effect inputs — that run **sequentially** on one `DbContext`. They are temporally decoupled, so they
*looked* like candidates to overlap across **separate read `DbContext`s / connections** (sound — the store is
opened read-only and SQLite allows concurrent readers; not `Task.WhenAll` on one context, which throws).

**Profiled + built the lowest-risk slice + measured on the real store → reverted.** Findings (2026-06-23,
MedDBase, Threadripper 32-logical, NVMe):
- The synthetic raw-SQLite concurrency experiment looked promising: 2 concurrent `reference_facts` scans on
  separate connections ran **1.94–2.75× faster** than sequential. The reads ARE CPU/marshaling-bound, not
  single-disk-serialised, so in isolation they overlap.
- **But the real `derive` command got no win — a slight regression.** Built the cleanest slice
  (`LoadShapedGraphAsync ∥ LoadFactEntryPointDataAsync` via `Task.WhenAll` on a second read context, in both
  `derive` and impact's `LoadHeadSideDataAsync`). Output stayed **byte-identical** (correctness fine), but warm
  `derive` went **~13.2 s → ~13.7 s median** (5+ runs each). The DB-load region is only ~33–36 % of wall-clock
  (Amdahl ceiling ~1.1–1.3 ×), and even that didn't materialise: the two big loads contend on EF marshaling /
  memory bandwidth, and the second context's setup (per-connection `mmap_size=1 GB` + 256 MB page cache) +
  EF compiled-model warmup outweigh the DB-layer overlap.
- **Conclusion:** adding a second `DbContext` + concurrency for net-negative perf is the trade we explicitly
  rule out. The bottleneck is the single-threaded CPU passes (`FactEffectDeriver.Derive`, `FactCycleDeriver`)
  + EF row materialisation, which DB-connection parallelism can't touch. If derive latency ever matters,
  attack THAT (the CPU passes / marshaling), not the load sequencing. Do not re-attempt the connection
  parallelisation without a materially different store profile.

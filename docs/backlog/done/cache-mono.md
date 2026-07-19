# Bake query-time monomorphization into `rig graph` — superseded

**Status:** SUPERSEDED by the 2026-06-26 calibration in
[dispatch-precision-substrate.md](dispatch-precision-substrate.md). The proposed additive
`~mono` materialization leaves the dispatch edges that inflate the bounded closure in place, grows the
store, and therefore does not attack the measured 1.1 GB cold-load floor. Kept only as the rejected design
record; do not implement this version.

## Original proposal
Monomorphization currently runs **in-memory at query time** (`ShapeGraph`), NOT baked into the persisted
`call_edges`. The spec's end-state — *"materialize ONE narrowed graph → forward ≡ reverse BY CONSTRUCTION +
O(E) reverse"* — needs the materialization moved to `rig graph`: run `GenericInstantiationInventory` +
`GenericMonomorphizer` in `GraphMaterializer.BuildAsync`, persist the `~mono` nodes + substituted/redirected
edges into `call_edges`/`dispatch_edges` (+ a base→mono collapse map for display). Then the CTE walks the
already-narrowed graph (smaller bounded pull; query-time inventory/materialize disappears). Bumps
`SchemaVersion.Graph`.

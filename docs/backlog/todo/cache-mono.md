## 1. Bake the narrowed graph at `rig graph` time
Monomorphization currently runs **in-memory at query time** (`ShapeGraph`), NOT baked into the persisted
`call_edges`. The spec's end-state — *"materialize ONE narrowed graph → forward ≡ reverse BY CONSTRUCTION +
O(E) reverse"* — needs the materialization moved to `rig graph`: run `GenericInstantiationInventory` +
`GenericMonomorphizer` in `GraphMaterializer.BuildAsync`, persist the `~mono` nodes + substituted/redirected
edges into `call_edges`/`dispatch_edges` (+ a base→mono collapse map for display). Then the CTE walks the
already-narrowed graph (smaller bounded pull; query-time inventory/materialize disappears). Bumps
`SchemaVersion.Graph`.
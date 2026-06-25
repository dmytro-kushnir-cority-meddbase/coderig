# Dispatch-precision substrate — remaining work

**Shipped record + spec:** [../done/dispatch-precision-substrate-shipped.md]. The core landed (dispatch-fan
disclosure + static monomorphization; forward ≡ reverse on the synthetic seams; the `#4460` phantom gone).
What's left to reach the end-state and trust it at scale:

## 1. End-state not yet reached — bake the narrowed graph at `rig graph` time
Monomorphization currently runs **in-memory at query time** (`ShapeGraph`), NOT baked into the persisted
`call_edges`. The spec's end-state — *"materialize ONE narrowed graph → forward ≡ reverse BY CONSTRUCTION +
O(E) reverse"* — needs the materialization moved to `rig graph`: run `GenericInstantiationInventory` +
`GenericMonomorphizer` in `GraphMaterializer.BuildAsync`, persist the `~mono` nodes + substituted/redirected
edges into `call_edges`/`dispatch_edges` (+ a base→mono collapse map for display). Then the CTE walks the
already-narrowed graph (smaller bounded pull; query-time inventory/materialize disappears). Bumps
`SchemaVersion.Graph`. *(Also tracked in [../done/monomorphization-rework.md] "next perf lever" — should
consolidate here; see note at bottom.)*

## 2. Forward ≡ reverse — validate on the real store at scale
Only the motivating `#4460` case is validated. Broader sweep open. `rig dispatch-fans` (re-measured
2026-06-25): **676 un-narrowed hubs / 61 actionable** — the worklist:
- `EntityBase.Save` (115 × 11) / `EntityBase.Delete` (49 × 8) — type-parameter sites; capture the type-arg
  binding via a rule/EP def to monomorphize them.
- `Construct`N`.New` factories — type-parameter.
- `IGenericServiceProvider.ProvideService``1` (the #1 by blast radius, 5 × 980) — **service-locator bucket,
  NOT a monomorphization case**: resolve via the existing `di_registrations` facts to the registered impl
  instead of CHA-fanning. A small targeted build, independent of the materialization work.
- The rest are `irreducible` base-typed-receiver CHA cones (correctly disclosed, not bugs).

## 3. Single static SQL connection (perf)
Each query opens its own `RigDbContext`/connection; move to ONE shared static SQLite connection app-wide
(read pragmas + mmap/cache applied once, warm across queries). *(Also in [../done/monomorphization-rework.md];
consolidate.)*

## Hard constraints (apply to all of the above)
- Playground → green → real-store → iterate; synthetic-`FactGraphData` unit coverage required.
- **Unresolved generic → CHA cone, NEVER dead** (soundness; disclose, don't drop).
- Disclose + classify every fallback; a high-fan actionable fallback is a hypothesis that a rule/EP def is
  incomplete.

---
**Consolidation note:** items 1 + 3 also live in `done/monomorphization-rework.md`, which was moved to `done/`
but still carries these OPEN items. They should move fully here so `done/` is purely shipped. (Pending the
orchestrator's go-ahead — see session discussion.)

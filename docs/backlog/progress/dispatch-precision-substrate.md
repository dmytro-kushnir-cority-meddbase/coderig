# Dispatch-precision substrate — remaining work

**Shipped record + spec:** [../done/dispatch-precision-substrate-shipped.md]. The core landed (dispatch-fan
disclosure + static monomorphization; forward ≡ reverse on the synthetic seams; the `#4460` phantom gone).
What's left to reach the end-state and trust it at scale:

## 1. Narrow the residual actionable dispatch hubs (precision worklist)
**Forward** (`Successors`, context-carrying) is the PRECISE path; **reverse** (`ReachableFromAll` all-hops
oracle / `callers` edge-inversion) is a deliberately-sound fast OVER-APPROXIMATION that forward-verification
partitions (`CallersForwardVerified*`). So the goal is **forward precision + reverse soundness, NOT
forward ≡ reverse** — full equality is only the *materialized-graph* end-state (deferred with graph-time
materialization). This item is just the precision worklist: shrink forward's residual over-fan by narrowing
the actionable hubs. `rig dispatch-fans` (re-measured 2026-06-25): **676 un-narrowed hubs / 61 actionable**:
- `EntityBase.Save` (115 × 11) / `EntityBase.Delete` (49 × 8) — type-parameter sites; capture the type-arg
  binding via a rule/EP def to monomorphize them.
- `Construct`N`.New` factories — type-parameter.
- `IGenericServiceProvider.ProvideService``1` (the #1 by blast radius, 5 × 980) — **service-locator bucket,
  NOT a monomorphization case**: resolve via the existing `di_registrations` facts to the registered impl
  instead of CHA-fanning. A small targeted build, independent of the materialization work.
- The rest are `irreducible` base-typed-receiver CHA cones (correctly disclosed, not bugs).

_(Single static SQL connection was considered and dropped — ❌ WON'T DO, see done/monomorphization-rework.md.)_

## Hard constraints (apply to all of the above)
- Playground → green → real-store → iterate; synthetic-`FactGraphData` unit coverage required.
- **Unresolved generic → CHA cone, NEVER dead** (soundness; disclose, don't drop).
- Disclose + classify every fallback; a high-fan actionable fallback is a hypothesis that a rule/EP def is
  incomplete.

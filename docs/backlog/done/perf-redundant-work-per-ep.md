## Perf: redundant work per entry point (rig self-dogfood, F1–F9)

**Status:** DONE — every row terminal (moved to done 2026-07-02, verified against code): F1/F2/F4/F6/F7/F8/F9
FIXED (`9caef5d1`, `1be1094f`, `78dbe9c2`), F3 conn-reuse fixed + base/head double-load intentional,
F5 NOT-A-REDUNDANCY (investigated), residuals: `TruncationCause` split DONE (`861bd0c4`), EF-fallback
routing WON'T DO (2026-06-23 decision, recorded below). No open actionable work remains; kept as the
redundancy-pattern ledger. Follow-on structural work lives in
[redundant-graph-index-rebuild-per-query](../todo/redundant-graph-index-rebuild-per-query.md) and
[warm-graph-across-queries](../todo/warm-graph-across-queries.md).

Found by running `rig` on its own store and reading every EP's `--format llm` call tree (the `x-phase` flag
makes a re-reached node a first-class row). One command calling the same heavy load more than once in a
single invocation. Severity = the cost of the repeated work.

| # | Redundant work | EPs | Status |
|---|---|---|---|
| F1 | `LoadFactGraphAsync` (efcore:read ×4) loaded inside `DeriveHandoffEntryPointsAsync` AND again directly | Derive | **FIXED** (`9caef5d1`) — `LoadShapedGraphAsync` loaded once, threaded into `DeriveHandoffEntryPointsAsync` + the cycle pass |
| F2 | `LoadFactEntryPointDataAsync` (efcore:read ×5) loaded top-level AND again inside a derivation callee | **FIXED** (Derive + Tree/Reaches) (`1be1094f`) | the real duplicate was the EF-fallback path (`TraversalGraphLoader` + `EntryPointContext.DeriveEpSiteKind`); threaded one load via `ReachInputs.EpData`. Callers/Path/Impact load epData at their own level (no dup through `BuildEpContext`) |
| F3 | `LoadFactGraphAsync` HEAD + BASE in Impact; each opens a fresh ADO conn via `LoadDispatchFactsAsync` | Impact | conn-reuse part FIXED in `LoadFactGraphAsync`; the base/head double-load is **intentional** (different stores) |
| F4 | `LoadDeploymentsAsync` (io:read ×3, slnx+projrefs parse) runs **twice** (`calls=2`) | Impact | **FIXED** (`78dbe9c2`) — hoisted before the cache branch, reused on both paths |
| F5 | `EffectDerivation.DeriveEffects` (full effect-match loop) runs twice on cold cache | Tree/Reaches/Derive | **NOT A REDUNDANCY** (investigated, `1be1094f`) — the bounded tree-path derive and the whole-store hazard-augmented `DeriveHazardEffectsAsync` use different complementary inputs; merging would change semantics |
| F6 | `RuleSetLoader.LoadMergedDocument` re-run for fingerprinting (4× total per command) | **FIXED** (`1be1094f`) | derive + Tree + Impact + EntryPointContext.Materialize now use out-param `Load` + `ComputeFromPaths` (one caller, `LoadOrDeriveEpSiteKind`, has no nearby `Load` — left) |
| F7 | `StoreLayout.ResolveReadStoreDir` (io:read ×7) resolved in `OpenReadContext` AND again for `StoreKey` | Derive | **FIXED** (`78dbe9c2`) — `OpenReadContext` surfaces the dir via out-param, reused for `StoreKey` |
| F8 | `LoadStaticField{Write,Read}RefsAsync` — two reads, identical base query | **FIXED** (Derive + Impact) | derive + impact (both sides) use the combined `…AccessRefsByKindAsync` (`78dbe9c2`); Tree already routes through the shared `DeriveHazardEffectsAsync` (combined) |
| F9 | `LoadDeploymentsAsync` (io:read ×3) loaded in `RunEntryPointsAsync` AND again at depth-1 | Callers | **FIXED** (`78dbe9c2`) — `DeploymentMap` loaded at the call site, threaded into `RunEntryPointsAsync` |

Cross-EP heavy shared methods (benign at once-per-command, the F1–F9 cases are the >once ones):
`LoadFactGraphAsync` (7/9 EPs), `LoadFactEntryPointDataAsync` (7/9), `LoadDeploymentsAsync` (7/9),
`DeriveEffects` (4/9), `RuleSetLoader.Load` (9/9). The `LoadShapedGraphAsync` consolidation (above) plus a
shared per-command `DeploymentMap` cache and threading already-loaded data into callees would clear most of
the open rows; F6's non-derive instances want `RulesFingerprint` to accept pre-resolved paths everywhere.

### Residual follow-ups surfaced by the work

- **Route EF-fallback `TraversalGraphLoader` through `LoadShapedGraphAsync`. — WON'T DO.** The consolidation
  (`9caef5d1`) routed derive + impact through the single shaped-graph loader, closing impact's `--async`
  delivery-edge gap — but the EF-fallback traversal loader (reaches/tree/path/callers when not on the SQL
  `call_edges` path) was left doing its own `LoadFactGraphAsync + ShapeGraph + MarkEventSubscriptionHandoffs`
  WITHOUT `AddDeliveryEdges`, so those fallback paths don't see delivery edges. **Decided not to fix
  (2026-06-23):** it's a corner case — the EF-fallback only triggers when `rig graph` hasn't run (no
  `call_edges`: `--no-graph` or pre-graph stores), and every modern graph-by-default index takes the SQL path
  where delivery edges are baked into `call_edges`. The fix is delicate (shaping is split between the loader's
  `ShapeGraph` and the command's `MarkEventSubscriptionHandoffs`, so threading `AddDeliveryEdges` in with the
  load-bearing ordering — `AddDeliveryEdges` must precede `MarkEventSubscriptionHandoffs`, the one that cost a
  24→0 `event_cycle` regression — is fiddly) and is not validatable on the MedDBase store (which has
  `call_edges` and never hits the fallback) without constructing a `--no-graph` store. The risk to the
  contended shaping path outweighs fixing a fallback modern indexes don't reach; left as a known limitation.
- **`seen` flag: split into `seen` vs `depth-capped` via a `TruncationCause` on `TraceNode`. — DONE**
  (`861bd0c4`). `TruncationCause { None, AlreadyExpanded, DepthCapped, BudgetCapped }` is set by precedence in
  `BuildTree`; the llm `seen` flag maps only to AlreadyExpanded, with distinct `depth-capped`/`budget-capped`
  flags and `seen:<id>` back-ref only for AlreadyExpanded. Tree payload-schema version bumped v1→v2.

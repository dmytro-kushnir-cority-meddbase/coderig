# Dispatch-precision substrate — remaining work

**Shipped record + spec:** [../done/dispatch-precision-substrate-shipped.md]. The core landed (dispatch-fan
disclosure + static monomorphization; forward ≡ reverse on the synthetic seams; the `#4460` phantom gone).
The precision worklist is retired (below); one open item remains (graph-time materialization, perf).

## Precision worklist — RETIRED (2026-06-25, calibrated by demonstration, not assertion)
The framing first: **forward** (`Successors`, context-carrying) is the PRECISE path; **reverse**
(`ReachableFromAll` all-hops oracle / `callers` edge-inversion) is a deliberately-sound fast
OVER-APPROXIMATION that forward-verification partitions (`CallersForwardVerified*`). So the goal is **forward
precision + reverse soundness, NOT forward ≡ reverse** (full equality is only the materialized-graph
end-state, item 2). The `rig dispatch-fans` "676 hubs / 61 actionable" looked like a worklist; on inspection
**every bucket is redundant, runtime-scoped, or irreducible — there is nothing to build.** Verdicts:

- **type-parameter hubs** (`EntityBase.Save` 115×11, `EntityBase.Delete` 49×8, `Construct`N`.New`) →
  **LATENT, don't build.** Shipped static monomorphization already narrows the real concrete-caller paths
  (`SaveServices<concrete>` → 175; `ValidatePostData<LoginData>` → `LoginData.Validate`). The wide fan is the
  never-executed OPEN template; no real EP reaches it un-narrowed (verified: the one real EP that hits a
  type-param hub, `PatientPortalHttpHandler` → `PostDataObject.Validate`, renders narrowed).
- **service-locator** (`IGenericServiceProvider.ProvideService``1`, #1 by blast radius) → **❌ WON'T DO
  (disclose-don't-resolve).** The body explodes (×122 IService impls → a 49,155-line raw tree vs 14 cut) over the BASE
  `IService` via reflection; the type parameter `T` only casts the RETURN — it never enters the body's
  dispatch — so monomorphization can't help and resolution would need runtime-scoped, per-provider-instance
  DI facts (the `xml_di_miner` is already rig absorbing one project's bespoke registration). This generalizes:
  ANY DI/service-locator bottoms out in reflection/codegen at construction (.NET DI included). The **cut is
  the correct, complete handling** — it severs the T-independent reflection plumbing while the typed
  result-call resolves PRECISELY via single-impl interface dispatch (demonstrated: `ReferralHelper.ReferralService`
  getter → `ProvideService<IReferralService>()` **cut** → `…GetDefaultReferralSummaryText`
  → `ReferralService.GetDefaultReferralSummaryText «via IReferralService»`, single impl, ×1). Multi-impl
  service interfaces → a sound small CHA fan (which one runs is runtime DI scope → disclose). String locator
  (`ProvideService(string)`/`CreateService(string)`) is dead in app code → deletable.
- **base-typed-receiver** → irreducible CHA cones, correctly disclosed.

⇒ **No precision-rule worklist.** The substrate's precision is done: shipped monomorphization + sound
disclosure (cuts) cover it. The single open substrate item is graph-time materialization (item 2, perf).

## 2. Graph-time materialization (perf / the end-state — the only open item)
Monomorphization currently runs IN-MEMORY at query time (`ShapeGraph`), not baked into persisted `call_edges`.
Bake it at `rig graph` time (`GenericInstantiationInventory` + `GenericMonomorphizer` in
`GraphMaterializer.BuildAsync`, persist `~mono` nodes/edges) so the CTE walks an already-narrowed graph
(smaller bounded pull; query-time inventory/materialize disappears; reverse over the baked graph ≡ forward by
construction). Bumps `SchemaVersion.Graph`. Perf lever, not a correctness gap.

**Reranked by measurement (2026-06-26):** `--time` showed a reverse query is **100% graph LOAD, disk-IO bound
(1.5 GB read/query, cpu:self 4%)** — see [warm-graph-across-queries.md](warm-graph-across-queries.md). So this
item is the **second** perf lever: it shrinks the per-call load (smaller baked `call_edges`) but doesn't stop
the per-process re-read. The larger lever is holding the graph warm across queries (that card). Do both.

_(Single static SQL connection was considered and dropped — ❌ WON'T DO, see done/monomorphization-rework.md.)_

## Hard constraints
- **Unresolved generic → CHA cone, NEVER dead** (soundness; disclose, don't drop).
- Disclose + classify every fallback.

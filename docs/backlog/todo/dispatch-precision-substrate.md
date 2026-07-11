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

## 2. The per-query load — what it ACTUALLY is (calibration spike, 2026-06-26)

**The original plan in this slot — "bake `GenericMonomorphizer.Materialize` into `call_edges` at `rig graph`
time" — is KILLED by the spike. It does not fix the load.** Measured on the MedDBase store
(`ContactEntity.Delete`, a typical symbol with a 3-effect answer):

| measure | count |
|---|---|
| forward closure, `call_edges` only | 157 |
| forward closure, `call ∪ dispatch` | **41,626** (265× explosion) |
| `dispatch_edges` rows (whole store) | 10,261 |
| `call_edges` rows (whole store) | 613,924 |
| `call_edges` pulled in the 41k closure | 102,725 |
| `reference_facts` (effect-inputs) pulled in the 41k closure | **381,318** |

**Finding:** the constant ~1.5 GB / ~6 s per query is the **dispatch-fan-inflated bounded closure**. Just 10k
`dispatch_edges` blow the closure up 265× (157 → 41,626); the bounded CTE then pulls ~102k call edges + ~381k
`reference_facts` rows for that inflated set. Monomorphization narrows the 41k back toward low hundreds (code's
own example: `DebtorOverride.SaveIncludedServices` 7861 → 175) — but **in memory, AFTER the heavy load**. We pay
disk for ~40k nodes that are then pruned.

**Why the bake fails:** `GenericMonomorphizer.Materialize` *clones* generic instantiations into `~mono`
`call_edges` (additive — base methods kept for soundness) and narrows via in-memory `NarrowByReceiver` over
`dispatch_edges`. The CTE walks `call_edges ∪ dispatch_edges`; baking `~mono` call edges leaves the dispatch fan
that inflates the closure **untouched**. As specified it would *grow* the store and *not* cut the load. ❌

**Two-pass query (defer effect-inputs to the narrowed set) — PROTOTYPED + MEASURED, ~20% only, NOT worth it.**
Hypothesis: reachability needs only graph structure; the 381k `reference_facts` effect-inputs are only needed
for the *reached* methods, so defer them to the narrowed set. A throwaway prototype (env-gated skips, reverted)
attributed `reaches ContactEntity.Delete` graph-load by component (time is stable; diskR noisy from OS page cache):

| variant | graph load |
|---|---|
| baseline (full) | 6.8s |
| skip effect-inputs (= the two-pass ceiling) | 5.6s (−1.2s, ~18%) |
| skip bindings | 6.0s (−0.8s) |
| skip both (≈ pure `call_edges` CTE walk) | **4.7s / ~1.1 GB floor** |

So deferring effect-inputs saves only ~18–25%. **The dominant cost (~4.7s / 1.1 GB) is the `call_edges` recursive
CTE walk over the dispatch-inflated 41k closure** — which the two-pass MUST still load to narrow in RAM. Not
worth the wiring for ~20%.

**Conclusion — no CHEAP query-side lever cuts the cold load.** The floor is the CTE walking 41k nodes that
narrowing later prunes to a handful. Two real options remain:
- **Heavy: bake receiver-narrowed dispatch at graph time** so the CTE closure itself is ~157, not 41k (cuts the
  walk AND both reference_facts reads). Context-sensitive dispatch persistence — hard, `dispatch_edges`
  blow-up risk, schema bump. The only thing that attacks the 1.1 GB floor.
- **Amortize instead of cut: warm-graph/MCP** ([warm-graph-across-queries.md](warm-graph-across-queries.md)).
  Since the cold load is structurally ~5s and hard to cut cheaply, holding the graph warm across queries (pay
  it once) regains priority over trying to shrink it. The earlier "stateless-first" ordering was predicated on
  materialization cheaply cutting the cold load — the spike shows it can't, so the bounded/stateful tradeoff
  is back in play for the repeated/agent workflow.

_(Single static SQL connection: still ❌ WON'T DO — the cost is the CTE page walk, not the connection.
`rig graph` is a cheap ~15s re-graph over immutable facts — relevant only if the heavy narrowed-dispatch bake is built.)_

## Hard constraints
- **Unresolved generic → CHA cone, NEVER dead** (soundness; disclose, don't drop).
- Disclose + classify every fallback.

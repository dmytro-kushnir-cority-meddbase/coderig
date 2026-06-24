# Design: dispatch precision — static monomorphization (+ DI resolution)

**Status:** design approved 2026-06-24; **build = Lever A (static monomorphization)**, phased, playground-first.
Companion to [backlog.md](backlog.md) §"Dispatch-fan disclosure + generic monomorphization" (the substrate
spec + calibration) and [bug-callers-reverse-overreach.md](bug-callers-reverse-overreach.md) (the reverse
receiver-narrowing fix `c7fe4f0f` that captured the concrete-receiver bulk). Read the effect↔reachability and
two-stage sections of [../CLAUDE.md](../CLAUDE.md) first.

## Goal — two distinct prizes

1. **Kill the type-parameter over-fan.** A generic method `M<T>` reached with concrete `T=C` whose body does
   `x.Foo()` (`x : T`) currently CHA-fans to every override of `Foo` on `T`'s constraint cone (e.g.
   `SaveServices<TEntity>` → `s1.Delete()` fans to all 97 `EntityBase.Delete` overrides incl. the phantom
   `ContactEntity.Delete`). High-FAN hubs are the worst for tree/path readability — one call site makes 97
   methods "reachable".
2. **Eliminate the forward/reverse asymmetry class.** Today forward is a context-carrying traversal (threads
   `incomingReceiver`/`carriedBinding`), reverse is a static edge-inverted map — they agree on edge-local
   narrowing, diverge on path-dependent narrowing. Monomorphization makes narrowing a **pure function of the
   (instantiated) edge**, so both directions traverse one graph and **forward ≡ reverse by construction** — no
   asymmetry left to disclose. This is the architectural prize, independent of the FP count.

## Why not query-time binding (the rejected fork)

`(node, binding)`-keyed traversal — make the BFS visit-key `(node, carriedBinding)` so a hub reached under
different bindings expands separately. **Rejected:**
- Combinatorial blowup (a hub reached under K bindings → K expansions) without a per-node cap.
- Ephemeral/per-query — pays on every hot-path traversal.
- **Doesn't deliver prize 2.** Reverse is built from a static edge map, not a traversal; making it
  binding-aware needs per-instantiation *edges* anyway. So this fixes forward precision while leaving reverse a
  different algorithm — the asymmetry survives. The root defeat is structural: forward traversal is
  NODE-MEMOIZED (`Enqueue` keys `visited` by node id), `carriedBinding` is PATH-dependent, and shared hubs
  (`EntityBase.Delete`, thousands of callers) are expanded once under the first-arriving (usually empty)
  binding — first-reach-wins. The only robust fix is to make each instantiation a **distinct node**.

## Lever A — static monomorphization (the build)

Materialize each reachable instantiation `M<C>` of a generic `M<T>` as a **distinct node** whose internal
edges have `T` substituted by `C`. `SaveServices<BillingRule>`'s `s1.Delete()` edge then carries
`ReceiverType = BillingRule` → resolves to `BillingRule.Delete`; node-memoization holds (distinct nodes); and
the materialized edges are receiver-pure → **forward ≡ reverse**.

### Load-time, no re-index (confirmed query-side)
Done as a **stage-2 load projection** — no Roslyn, no extraction change, no re-index. The two inputs are
already in the store:
- **Call-site binding** — `MethodTypeArgBinding` / `DeclaringTypeArgBinding` on the edge into the generic
  (reference_facts, loaded onto `CallEdge` in `Reads.cs`). E.g. `DebtorOverride.SaveIncludedServices →
  SaveServices` carries `[C:BillingRule…IncludedEntity, C:int]`.
- **Type-param name→index map** — parsed from `symbol_facts.Signature`, which preserves the source signature
  WITH ordered names: `SaveServices<TEntity, Tv>(...)` ⇒ `TEntity→0, Tv→1`. (Declaring-type generics, e.g.
  `Construct``2`, map via the TYPE's `Signature` + `DeclaringTypeArgBinding`.) So the receiver token `"TEntity"`
  on the body edge → index 0 → binding `BillingRule…Entity`.

### Mechanism
1. **Instantiation inventory (fixpoint).** Worklist of `(genericMethod, positionalBinding)`, seeded from edges
   into generic methods. Process `M<C>`: substitute `T→C` in its body edges; any nested generic call
   `N<…C…>` enqueues `N`'s instantiation (binding propagates). Closure.
2. **Substitute** `T→C` in each cloned edge's `ReceiverType` and nested type-args (via the Signature
   name→index map + the carried positional binding).
3. **Materialize** instantiation nodes + substituted edges into the graph the traversals read.
4. **Cap → CHA-cone fallback (NEVER dead, disclosed via `dispatch-fans`).** Bounds: max instantiations/method,
   max total clones, max instantiation depth — catches polymorphic recursion (`F<List<T>>`). On overflow the
   un-monomorphized base node stands (full CHA cone), tagged in the fan worklist as "monomorphization-capped".
5. **Selectivity** bounds the work: only monomorphize generic methods on the `dispatch-fans` actionable list
   (~18) + their transitive generic callers (needed to learn `C`). Not every generic in the program.

### Hard problems (these are the real cost, not the substitution)
- **Display-collapse layer.** Monomorphization SPLITS nodes for traversal, but users think in one source
  `SaveServices`. `callers`/`tree`/`reaches` must collapse instantiation nodes → base method id for
  reporting/identity (traverse split, render collapsed), else output fragments into `SaveServices<X>`,
  `SaveServices<Y>`, … Real machinery; the existing `DeclaringTypeArgBinding` render labels (TreeRenderer) are
  a starting point.
- **Re-validate every traversal.** This is a cross-cutting graph change — reaches/tree/path/callers/derive all
  read the materialized graph. Each needs re-validation (golden-oracle on the real store) and FP-calibration
  before on-by-default.
- **Fixpoint termination/cost** — the cap is load-bearing; measure clone count on the real store.

### Forward ≡ reverse
Once materialized, both forward `Successors` and reverse `BuildReverseMaps` operate on the same instantiated
edges; narrowing is baked in (receiver = concrete), so node-memoization works identically both ways. The
reverse map inverts exactly the forward edges. The asymmetry disclosed in `c7fe4f0f` disappears for the
monomorphized seams (residual only at genuinely dynamic boundaries — reflection/`MakeGenericType`).

## Lever B — `di_registrations` dispatch resolution (complementary, not the chosen build)

The cut-aware `dispatch-fans` worklist shows the highest *edge-count* hubs are interface/base-typed-receiver
dispatch (`IPerformanceLogger.Split` 4×236, `IUpdateLog.Log` 2×174), DI-resolved interfaces. The
`di_registrations` facts (already in the store, no re-index) map service→impl, so interface dispatch can
resolve to the registered impl instead of CHA-fanning. Query-side, bounded, doesn't touch the traversal core.
Documented here as the cheaper complementary lever; **deferred** in favour of A (A wins prize 2; B is precision
only). Revisit B if the interface-dispatch edge-count proves to dominate real query noise.

## Cost / value

| | Lever A (monomorphization) | Lever B (DI resolution) |
|---|---|---|
| Precision target | high-FAN type-param hubs (tree explosion) | high-FREQUENCY interface hubs |
| Forward ≡ reverse | **yes** (the prize) | no |
| Re-index | no (load-time projection, Signature-parsed) | no |
| Blast radius | cross-cutting (loader+traversal+render) | query-side, localized |
| Effort | large, multi-phase | small/medium |

Chosen: **A**, for prize 2 (eliminates the asymmetry class) and the high-fan readability hubs. The
concrete-receiver bulk was already captured by `c7fe4f0f`; the disclosure (`dispatch-fans`, cut-aware) keeps
the residual honest during the build.

## Phased build plan (playground-first; green → big-boy → iterate; unit-tested)

0. **Substitution primitive** (pure fn, the leverage point): parse `symbol_facts.Signature` → ordered
   type-param names; given `(genericMethod, positionalBinding)` + a body edge whose `ReceiverType` is a
   type-param, return the substituted concrete receiver. Heavily unit-tested in isolation (method generics,
   declaring-type generics, nested `List<T>`, non-type-param receivers untouched, malformed signatures).
1. **Instantiation fixpoint + cap** — closure from seed edges, bounded; unit-tested incl. polymorphic-recursion
   cap → CHA-cone fallback.
2. **Materialize** the monomorphized subgraph (clone nodes/edges with substitution) as a load projection;
   playground: `SaveServices<BillingRule>` body's `s1.Delete()` narrows to `BillingRule…Entity.Delete` (not
   ×97); forward ≡ reverse on the synthetic graph.
3. **Display-collapse** layer (instantiation node → base id for reporting); callers/tree identity preserved.
4. **Wire into the real load path behind a flag**; big-boy: `dispatch-fans` type-param bucket shrinks,
   reaches/tree/callers re-validated, forward ≡ reverse spot-checks, FP-calibrate before on-by-default.

## Phase 4 status + calibration findings (2026-06-24)

**Phase 4a SHIPPED** (`dee67655`): materialization is wired into the shared `ShapeGraph` pass behind the
`RIG_MONOMORPHIZE` env var, default OFF. Wiring is correct + full-suite green; flag-off is byte-identical.
**It does NOT yet narrow anything on the real MedDBase store** — calibration (4b) surfaced two blockers, so
the flag stays OFF and the 5 parked reverse-dispatch tests (backlog.md) are NOT yet recovered.

1. **v1's coverage ∩ the real high-fan hubs ≈ ∅ (the substantive gap).** The `dispatch-fans` type-parameter
   hubs (`EntityBase.Save` 115×11, `EntityBase.Delete` 49×8, `Construct``3.New` 47×2) are reached via a
   type-parameter receiver (`x.Save()`, receiver token `TEnt`/`TEntity`) whose binding lives NOT on that
   edge but on the edge into the **enclosing** generic method — and frequently inside a **lambda** (`~λN`)
   in that method's body (verified in `reference_facts`: those Save/Delete call rows carry empty
   `MethodTypeArgBinding`/`DeclaringTypeArgBinding`). v1 (Phase 1 inventory = DIRECT, concrete binding ON the
   immediate edge into a generic; Phase 2 = clone that method's IMMEDIATE body edges) covers neither: it does
   no transitive binding flow and does not propagate an instantiation into lambda sub-nodes. The clean
   method-generic case it DOES cover (`BillingRuleHelper.SaveServices``2`, concrete `MethodTypeArgBinding`
   `["C:…IncludedEntity","C:int"]` on the edge) is real but its one type-param body dispatch
   (`EntityBase.Delete`) renders `«opaque: LLBLGen ORM runtime»` (a `render.opaqueTypes` leaf) so it never
   fanned to first-party overrides — nothing to narrow. ⇒ **Next increment: transitive + lambda binding
   propagation** (the refinement Phase 1/2 explicitly deferred). Until then monomorphization is a no-op on
   observable reachability here (firing-but-invisible at best — not wrong, just not reaching the cases).

2. **Bounded SQL graph drops the type-arg binding columns (a real prerequisite bug).** `reaches`/`tree`/
   `path`/`callers` use the SQL bounded loader (`SqlReachability.LoadGraphFromReachSetAsync`), and the
   persisted `call_edges` views carry NO `DeclaringTypeArgBinding`/`MethodTypeArgBinding`/`TypeArguments`
   columns (those live in `reference_facts`; only the EF `Reads.LoadFactGraphAsync` path projects them onto
   `CallEdge`). So on every real SQL-path query the inventory sees no bindings ⇒ materialization is a silent
   no-op. The fix is a query-side re-attach in `LoadGraphFromReachSetAsync` mirroring the EXISTING
   `TypeArguments` bulk-load (one pass over `reference_facts` keyed by `(caller, callee, line)`) to also
   carry the two binding columns — **no re-index**. A prototype matched the join key (both `Line=50` for the
   `SaveServices` edge) but the SQL-path tree label stayed `<T,U>` (binding still not reaching the rendered
   node), so it needs a focused **integration test on a real graph-views store** (the `AnalyzedPlaygrounds`
   fixture) to land correctly — the in-memory plumbing chain `edge → Successors.OutMethodBinding →
   MutableNode → TraceNode → renderer` is confirmed intact, so the gap is purely the bounded-path delivery.
   (A concrete generic LABEL on the SQL path is the cheapest proof-of-life; `derive`/EF-path tests won't catch it.)

## Hard constraints
- **Unresolved/over-cap generic → CHA cone, NEVER dead** (soundness; disclose via `dispatch-fans`).
- **Playground → green → big-boy → iterate**, unit coverage required (mirror `OneHopDispatchTests` /
  `ReverseReceiverNarrowingTests` / `DispatchFanReportTests` synthetic-`FactGraphData` style).
- **One agent at a time on `FactPathFinder.*`** — it's the contended core.
- **No on-by-default until FP-calibrated** on the real MedDBase store.

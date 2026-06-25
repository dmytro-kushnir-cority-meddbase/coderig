## Feature: Dispatch-fan disclosure + generic monomorphization (the dispatch-precision substrate)

**Status:** proposed · **Found:** 2026-06-24 (reverse receiver-narrowing fix `c7fe4f0f` + the design dialogue).
This is the concrete spec for the "dispatch-precision substrate" the CEP feature is gated on, and it folds in
the old perf note (precise full-closure reverse `O(E)` not `O(N×reach)`) and the forward/reverse asymmetry.

### Why
After per-edge receiver narrowing (`c7fe4f0f`), forward and reverse share the dispatch PRIMITIVE
(`DispatchTargets`) but are not the same ALGORITHM: forward is a context-carrying traversal (threads
`incomingReceiver`/`carriedBinding`/this-type along the path), reverse is a static edge-inverted map (raw
`edge.ReceiverType`, no binding). They agree where narrowing is edge-local (the receiver win); they diverge
where it is path-dependent (generic type-arg flow). The residual over-approximation concentrates at generic
seams: e.g. `BillingRuleHelper.SaveServices<TEntity>` → `s1.Delete()` with stored receiver `"TEntity"` (open
type parameter) → `ResolveNarrowRoot` can't place a type-parameter → full CHA cone over the 97-way mined
`EntityBase.Delete` fan.

### Calibration (MedDBase store caa9373ffbf6-dirty)
- Mined dispatch dominates: **9,981 roslyn** vs **266 heuristic** edges (97.4% precise). Roslyn binding is NOT
  the problem; we are not reimplementing the compiler.
- Pure CHA-fallback (heuristic basis) is tiny: 266 edges, **1** high-fan source (`GenericImportEntity.Save~λ0`,
  fan 26), the rest fan-1 (precise anyway).
- The REAL over-approximation is **mined multi-target fans that receiver-narrowing fails to trim**: 103 mined
  sources fan ≥10 (26 of them ≥40); `EntityBase.Delete` = 97 mined targets. Dominant cause is the receiver
  gap: **212,357 / 613k call edges (34%) carry NO receiver** → narrowing can't even start → full CHA cone.
- ⇒ The actionable signal is NOT "heuristic basis"; it is "**un-narrowed multi-target dispatch site**", and the
  cause to disclose is *why the receiver couldn't narrow* (absent / type-parameter / base-typed / unbound).

### The model (reified generics → monomorphization)
C# generics are reified: every `Foo<Account>`/`Foo<Invoice>` is a distinct monomorphic type at runtime; an open
`SaveServices<TEntity>` never executes — only concrete instantiations do. So the precise analysis treats a
generic method as a TEMPLATE and materializes it per concrete type arg seen at call sites (RTA/VTA-style).
Three outcomes per generic/dispatch site:
1. **Monomorphize** where the concrete type arg is in scope — precise; kills the fan (`SaveServices<ServiceEntity>`
   → `s1.Delete()` resolves to `ServiceEntity.Delete`, not all 97).
2. **CHA cone over the constraint** where it isn't — SOUND, never a dead node. "Can't resolve" ≠ "dead": the
   binding may be supplied at runtime (framework/DI/reflection-bound open entry point), the constraint
   (`where TEntity : EntityBase`) still bounds the runtime type to a cone, and monomorphization can be
   unbounded (`F<List<T>>` recursion) so it must cap and fall back. Treating unresolved as dead is the one
   UNSOUND move (silent false negative) — the direction rig never takes.
3. **Prune (RTA)** only where the program-wide instantiation set is empty — the *only* place "dead" is sound
   (a generic whose type is never instantiated anywhere is genuinely unreachable).

### Disclosure as diagnostic (load-bearing)
Every CHA-cone fallback is DISCLOSED, attributed to a site, and **classified**:
- **actionable** (`likely missing rule / EP def`): open-generic entry point bound by the host, DI open
  registration, a type-arg or receiver that flows from a seam we could capture with a rule — fixing the
  rule/EP def monomorphizes it. This is the valuable worklist.
- **irreducible** (`hard boundary`): polymorphic-recursion past the fuel cap, fully-dynamic
  `MakeGenericType(Type.GetType(...))` — no rule fixes these; tag, don't TODO.
Rank by fan degree × incoming-edge count (a ×97 fallback is a "go write a rule" flag; a ×2 is noise). Builds on
existing disclosure infra (`~heuristic` tag, the "dispatch fan-out (NOT a real call)" bucket, the `×N fan-out`
annotation) — makes it attributed + prioritized instead of merely present.

### End-state
Narrowing becomes a PURE FUNCTION OF THE (instantiated) EDGE → materialize ONE narrowed graph; forward and
reverse traverse the identical edge set → **forward ≡ reverse by construction** (closes the asymmetry) and
precise full-closure reverse is `O(E)` (closes the perf note).

### Increments (playground-first: synthetic unit tests green → validate on the real store → iterate)
1. **Dispatch-fan disclosure / diagnostic** — ✅ **SHIPPED** (`027819ca`, `rig dispatch-fans`). Pure-additive,
   zero traversal change. Calibration result: 841 un-narrowed hubs / 71 actionable; top irreducible
   `IGenericServiceProvider.ProvideService``1` (fan 5 × 980 edges — a service-locator seam); top type-parameter
   actionable `EntityBase.Save` (115 × 11), `EntityBase.Delete` (49 × 8), `Construct``N.New` factories.
2. **Static monomorphization** (was "increments 2 + 3" — they COLLAPSE; see revision below). The single change
   that kills the type-parameter over-fan AND delivers forward ≡ reverse + `O(E)` reverse. DESIGN-FIRST.

### Revision (2026-06-24): increments 2 and 3 are the SAME change
Empirical finding while scoping #2 — the concrete type arg IS already in the facts: the call
`DebtorOverride.SaveIncludedServices → SaveServices<…>` carries `TypeArguments =
BillingRule…IncludedEntity,int` (reference_facts), and `NarrowByTypeArguments` already monomorphizes a fan
against `carriedBinding`. So a query-side "thread carriedBinding" fix LOOKS cheap — but it is **structurally
defeated**: the forward traversal is NODE-MEMOIZED (`Enqueue` keys `visited` by node id, FactPathFinder.cs),
while `carriedBinding` is PATH-dependent. `EntityBase.Delete` is a hub reached from thousands of sites, so
**first-reach-wins**: whichever path hits the hub first expands its dispatch fan with its (usually empty)
binding and marks the node visited; the binding-carrying path arrives to an already-expanded node. Binding
narrowing therefore can't fire at exactly the shared hubs that matter. (Same node-memoization-vs-path-dependent
conflict as the forward/reverse asymmetry.)
⇒ The only robust fix is to make each instantiation a **distinct node** — STATIC monomorphization (clone the
instantiated generic body, keyed by its type-arg binding), which IS the materialized-graph change. There is no
cheap separate query-side increment. 2 ≡ 3.

### Design forks for static monomorphization (decide before building)
- **(node, binding)-keyed traversal** vs **static body-cloning into distinct nodes.** Keying risks
  combinatorial blowup; cloning needs an instantiation INVENTORY (which `<T=concrete>` actually occur) and a
  fuel cap for polymorphic recursion (`F<List<T>>`). Cloning is the principled end-state (pure edge function →
  forward ≡ reverse); keying is a smaller but leakier step.
- **Instantiation source**: the per-call-site `TypeArguments` / `MethodTypeArgBinding` already in
  reference_facts (loaded onto `CallEdge`, Reads.cs) supply the inventory — no re-index needed for v1.
- **Service-locator bucket is SEPARATE** (`ProvideService``1`, the #1 actionable by blast radius): not a
  monomorphization case — resolve via the existing `di_registrations` facts (table present in the store) to the
  registered impl, instead of CHA-fanning. A small targeted build, independent of the monomorphization work.
- **Bound**: instantiation count cap + CHA-cone fallback on overflow (NEVER dead; disclose via #1).

### Hard constraints
- **Playground → green → big-boy → iterate**, unit-test coverage required (mirror `ReverseReceiverNarrowingTests`
  / `OneHopDispatchTests` synthetic-`FactGraphData` style).
- **Unresolved generic → CHA cone, NEVER dead** (soundness; disclose, don't drop).
- **Disclose + classify every fallback**; a high-fan actionable fallback is a hypothesis that a rule or EP def
  is incomplete.

### Reconcile-on-settle: parked reverse-dispatch tests (recover when the substrate settles)
Five unit tests are `[Skip]`-parked, each pinning the PRE-substrate reverse-dispatch over-approximation that
per-edge receiver narrowing (`c7fe4f0f`) intentionally changed. They document the phantom reverse reach that
**forward ≡ reverse** (the monomorphization prize, design goal #2) is meant to eliminate — so they cannot pass
until the substrate is on. **Trigger to recover: after static monomorphization (the materialized graph) is
wired into the load path on-by-default AND FP-calibrated on the real MedDBase store** (Phase 4 of
[design-dispatch-precision.md](../../design-dispatch-precision.md)). At that point, re-enable each and flip its
assertion from "documents the over-approximation / includes the phantom" to the narrowed truth (no phantom
caller; reverse == forward at the dispatch hop). The five:
- `CallersForwardVerificationTests.Reverse_reach_includes_both_eps_documenting_the_over_approximation`
- `CallersForwardVerifiedClosureTests.Reverse_closure_includes_the_phantom_caller_documenting_the_over_approximation`
- `CallersForwardVerifiedClosureTests.Forward_verify_confirms_the_real_reacher_and_partitions_the_phantom_as_reverse_only`
- `FactPathFinderFanoutTests.ReachedBy_finds_transitive_callers_including_interface_dispatch`
- `FactPathFinderFanoutTests.Reverse_dispatch_narrows_by_receiver_at_the_dispatch_hop`

# Dispatch-precision substrate — shipped record + spec

The shipped core of the dispatch-precision substrate (the CEP gate). **Remaining/open work:**
[../progress/dispatch-precision-substrate.md]. Impl status of the monomorphization rework:
[./monomorphization-rework.md].

Summary: per-edge receiver narrowing + static monomorphization landed; forward ≡ reverse holds on the
synthetic seams; the motivating real-store phantom (`#4460`) is gone.

## Shipped
- **Increment 1 — dispatch-fan disclosure** `rig dispatch-fans` ✅ (`027819ca`). Pure-additive diagnostic;
  classifies every un-narrowed multi-target dispatch site (absent / type-parameter / base-typed / unbound
  receiver) and ranks by fan × incoming-edges. Zero traversal change.
- **Increment 2 — static monomorphization** ✅, unconditional + FP-calibrated (`e05e77d0`). Clones each
  generic instantiation into a distinct node, killing the type-parameter over-fan (`DebtorOverride`
  7861 → 175 reachable, adversarially verified sound).
- **Parked reverse-dispatch tests** ✅ reconciled (`cc9a529b`, suite has zero skips) — forward ≡ reverse at
  the dispatch hop on the synthetic seams.
- **2026-06-25 — motivating real-store case validated**: the `#4460` `EntityBase.Delete` CHA-fan phantoms are
  gone (reverse attribution of `RemovePersonContactLinks`: 5 EPs → 1, no phantoms).

## Why (the asymmetry this fixed)
After per-edge receiver narrowing (`c7fe4f0f`), forward and reverse share the dispatch PRIMITIVE
(`DispatchTargets`) but were not the same ALGORITHM: forward is a context-carrying traversal (threads
`incomingReceiver`/`carriedBinding`/this-type along the path), reverse is a static edge-inverted map (raw
`edge.ReceiverType`, no binding). They agree where narrowing is edge-local (the receiver win); they diverge
where it is path-dependent (generic type-arg flow). The residual over-approximation concentrated at generic
seams: e.g. `BillingRuleHelper.SaveServices<TEntity>` → `s1.Delete()` with stored receiver `"TEntity"` (open
type parameter) → `ResolveNarrowRoot` can't place a type-parameter → full CHA cone over the 97-way mined
`EntityBase.Delete` fan.

## The model (reified generics → monomorphization)
C# generics are reified: every `Foo<Account>`/`Foo<Invoice>` is a distinct monomorphic type at runtime; an
open `SaveServices<TEntity>` never executes — only concrete instantiations do. So the precise analysis treats
a generic method as a TEMPLATE and materializes it per concrete type arg seen at call sites (RTA/VTA-style).
Three outcomes per generic/dispatch site:
1. **Monomorphize** where the concrete type arg is in scope — precise; kills the fan
   (`SaveServices<ServiceEntity>` → `s1.Delete()` resolves to `ServiceEntity.Delete`, not all 97).
2. **CHA cone over the constraint** where it isn't — SOUND, never a dead node. "Can't resolve" ≠ "dead": the
   binding may be supplied at runtime (framework/DI/reflection-bound open EP), the constraint
   (`where TEntity : EntityBase`) still bounds the runtime type to a cone, and monomorphization can be
   unbounded (`F<List<T>>` recursion) so it must cap and fall back. Treating unresolved as dead is the one
   UNSOUND move (silent false negative) — the direction rig never takes.
3. **Prune (RTA)** only where the program-wide instantiation set is empty — the *only* place "dead" is sound.

## Disclosure as diagnostic (shipped as `dispatch-fans`)
Every CHA-cone fallback is DISCLOSED, attributed to a site, and **classified**:
- **actionable** (`likely missing rule / EP def`): open-generic EP bound by the host, DI open registration, a
  type-arg/receiver that flows from a capturable seam — fixing the rule/EP def monomorphizes it.
- **irreducible** (`hard boundary`): polymorphic-recursion past the fuel cap, fully-dynamic
  `MakeGenericType(Type.GetType(...))` — no rule fixes these; tag, don't TODO.
Ranked by fan degree × incoming-edge count.

## Calibration snapshot (store `caa9373ffbf6-dirty`, at spec time)
- Mined dispatch dominates: **9,981 roslyn** vs **266 heuristic** edges (97.4% precise) — Roslyn binding is
  NOT the problem; we are not reimplementing the compiler.
- Pure CHA-fallback (heuristic basis) is tiny: 266 edges, 1 high-fan source, the rest fan-1.
- The REAL over-approximation is **mined multi-target fans receiver-narrowing fails to trim**: 103 mined
  sources fan ≥10; `EntityBase.Delete` = 97 mined targets. Dominant cause: **34% of call edges carry NO
  receiver** → narrowing can't start → full CHA cone. ⇒ the actionable signal is "un-narrowed multi-target
  dispatch site", and the disclosed cause is *why the receiver couldn't narrow*.

## Revision (2026-06-24): increments 2 and 3 are the SAME change
The concrete type arg IS already in the facts (`DebtorOverride.SaveIncludedServices → SaveServices<…>` carries
`TypeArguments` in reference_facts), and `NarrowByTypeArguments` already monomorphizes a fan against
`carriedBinding`. A query-side "thread carriedBinding" fix LOOKS cheap but is **structurally defeated**:
forward traversal is NODE-MEMOIZED (`visited` keyed by node id) while `carriedBinding` is PATH-dependent — so
at a shared hub like `EntityBase.Delete`, first-reach-wins expands the fan with the (usually empty) binding
and marks the node visited; the binding-carrying path arrives to an already-expanded node. ⇒ the only robust
fix is a **distinct node per instantiation** — STATIC monomorphization (the materialized-graph change). 2 ≡ 3.

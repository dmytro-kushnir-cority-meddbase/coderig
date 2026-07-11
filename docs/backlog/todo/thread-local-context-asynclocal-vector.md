# Extend the thread-local reroute to AsyncLocal<>/ThreadLocal<> (a distinct risk vector)

**Status:** todo · **Found:** 2026-06-26 (lazy_init_race verification, site 11) · **Family:** detector-precision / FR-2

## What
`FactHazardDeriver` already reroutes a read→write on a `[ThreadStatic]` cell away from `race_window`/`lazy_init_race`
to a disclosed `thread_local_context` candidate (FR-2: the value may be lost when an async continuation resumes on a
different thread — the production-reverted `[ThreadStatic]`→`AsyncLocal` migration). The cell set comes from
`Reads.LoadThreadStaticFieldIdsAsync` (static fields carrying the `[ThreadStatic]` attribute).

**Gap:** `AsyncLocal<T>` and `ThreadLocal<T>` backing fields are NOT in that set, so a lazy-init-shaped read→write on
one is currently a `lazy_init_race` **false positive** (verification site 11: `Echo.ActorContext.get_DefaultSystem`,
cell `Context` backed by `AsyncLocal<SystemName>` — flagged lazy_init_race, but it's per-flow, no cross-thread race).

## Why it's a separate item (not the lock-enclosure work)
This is a **different risk vector** from lazy-init double-construction. `AsyncLocal`/`ThreadLocal` cells are NOT a
double-init race — they're the FR-2 **context-propagation** surface (a flow-local value silently dropped across an
`await`/thread hop). So the fix is twofold and belongs with FR-2, not the lazy-init precision pass:
1. **Stop the lazy_init_race FP** — broaden the rerouted cell set to include `AsyncLocal<>`/`ThreadLocal<>`-typed
   static fields, so they classify as `thread_local_context`, not `lazy_init_race`.
2. **Improve `thread_local_context` recall** — those same cells are exactly what FR-2 should be reasoning about.

## How
`LoadThreadStaticFieldIdsAsync` (or a sibling): also collect static fields whose declared type is
`System.Threading.AsyncLocal<…>` / `System.Threading.ThreadLocal<…>` (type-name match on `SymbolFact`), union into
the reroute set passed to `DeriveRaceWindows(threadStaticCells: …)`. Low cost — reuses the existing reroute machinery.

## Calibration note
Confidence stays LOW (we can't prove the read crosses an await/thread boundary without flow modeling — FR-2 tier-3).
The value is corpus-grounding + FP suppression, not a per-path proof.

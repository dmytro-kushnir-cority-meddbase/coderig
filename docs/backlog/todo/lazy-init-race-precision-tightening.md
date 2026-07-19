# `lazy_init_race` precision tightening (calibration: 57.5% → ~92%)

**Status:** TODO / MEDDBASE-DEPENDENT — bucket #1 ✅ SHIPPED (2026-07-02); bucket #2 needs condition
extraction plus a MedDBase re-index and source-backed recalibration. Moved back to `todo/` 2026-07-19 while
those inputs are unavailable. · **Found:** 2026-06-26 (adversarial source verification of all 40 MedDBase
sites) · **Family:** detector-precision

## Shipped: bucket #1 — the lock-enclosed tier (2026-07-02)

Rode the existing span machinery exactly as designed: the builtin `lock_held_across_effect` resourceSpan
observation (already on shared_state writes inside a `lock`) classifies a lazy-init pair to the DISTINCT
disclosed reason **`lazy_init_lock_enclosed_verify_dcl`** (a tier, not a suppression — per the caveat
below). **MedDBase verification: exactly the 10 predicted FPs moved to the tier, name for name** (the 8
FDB `*.initialised` + `ImportMsgHub` + `TestbedHub`); 28 sites stay at `lazy_init_heuristic`, including
all 23 TPs and the 2 real bugs below → top-tier precision ~82% (from 57.5%), every TP kept.

The hard-suppress landed too but is **inert until the next MedDBase re-index**: it requires the cell to
be `volatile` AND the publish to be the LAST lock-held effect in the method (publish-last, line order) —
and `volatile` was NOT in `symbol_facts.Modifiers` (the card's assumption was wrong). Now mined
(`FactExtractor.BuildModifiers`/`ModifierKey`, `VolatileModifierTests`) + loaded
(`Reads.LoadVolatileFieldIdsAsync`) + threaded (derive/impact both sides); a pre-volatile store yields an
empty set = never corroborated, tier still applies. `HazardEffectsCacheKey` bumped v2→v3 (classifier
changed with no key input changing — a warm cache would have served stale reasons). Tests:
`LazyInitLockTierTests` (6: the safe-DCL/DCL-without-volatile regression pair, publish-early, non-lazy
race_window unaffected).

## What was measured
All 40 `lazy_init_race` sites on the MedDBase store, read against actual source. **23 TP / 17 FP / 0 unsure → precision 57.5%.**
The heuristic (`FactHazardDeriver.LooksLikeLazyInit`) is shape-only: a read→write on a static cell in a getter/init/ctor
(arm A) or a single-write init-named cell (arm B). It never sees the `if`-condition, so it can't tell a guarded lazy-init
from a getter that mutates already-valued state. The 17 FPs cluster into three structurally-fixable buckets.

## FP buckets + tightening signals
1. **Double-checked locking — 10 FPs** (the FDB `*.Initialise` family ×8: `AdministrationMethod`/`AdministrationSite`/
   `Diluent`/`DoseDateTime`/`DoseFrequencyRange`/`UnitOfMeasure`/`BaseRoute`/`CircumstantialTime`; + `ImportMsgHub.Init`,
   `TestBedHub.Init`). All are textbook `if(flag) return; lock(sync){ if(flag) return; …; flag=true; }` — write inside the
   lock, correctly synchronized. **Signal:** the write is lexically inside a `lock`/`Monitor.Enter` region. rig already has
   the resource-span machinery (`FactResourceSpanRule` emits `transaction_spans_effect`); a `lock(){}` is the same span
   shape. Biggest single bucket → 57.5% → ~77% alone.
2. **Ctor/Init with an UNCONDITIONAL write — 5 FPs** (`EmailAutoLinkService`/`WorkItemAssignmentCheckingService`/`AnyProcess`
   `.ctor` singleton registration — the "read" is only `Debug.Assert(Singleton==null)`, the write `Singleton=this` is
   unconditional; `TaskRegister.Initialise` ×2 — writes the static dict unconditionally in both branches). **Signal:** require
   the write to be **control-dependent on the null/flag read** (write dominated by an `if` testing the cell). This is the one
   needing NEW extraction — the if-condition the heuristic deliberately skips today.
3. **Thread-local / per-session cell — 2 FPs** (`ActorContext.get_DefaultSystem` cell backed by `AsyncLocal<>`;
   `Main.get_…Certificate` mutating a session-scoped `Settings`). Tracked separately:
   [[thread-local-context-asynclocal-vector]].

**Combined #1 + #2 (write must be null-guarded AND not lock-enclosed) → ~92% precision, every TP kept.**

## ⚠ Design caveat — lock-enclosure is a PROXY, not proof (do NOT blanket-suppress)
Bucket #1's signal ("write inside lock") does NOT prove correctness. A trace can look like a textbook DCL yet fail:
- **DCL without `volatile`** — the lock-free OUTER read (`if(instance==null)` before the lock) can observe a non-null but
  partially-constructed object (reordering on weak memory models). The lock serializes *writers*; it does nothing for the
  lock-free *reader*.
- **Flag/reference set before the work completes** — reorder `flag=true` above the init body and the identical-looking DCL
  hands a lock-free reader half-built state. (The FDB FPs are safe ONLY because they set `initialised=true` last.)
- **Wrong lock object** — per-instance `lock(sync)` guarding a `static` cell, or two writers using different locks.

So follow rig's existing **"disclose, don't suppress"** rule (the way `transaction_spans_effect` *downgrades* `race_window`
rather than dropping it): a lock-enclosed lazy-init → a **lower disclosed tier**, not vanish. Hard-suppress only when
corroborated by signals rig actually has: the cell is **`volatile`** (in `symbol_facts.Modifiers`) **AND** the publishing
write is the **last effect in the lock region** (rig has line order). Lock-enclosed but non-volatile, or with effects after
the publish → keep flagging (lower tier) — that's exactly the dangerous DCL-without-volatile variant.

**Build order:** bucket #1 first (reuses span machinery, biggest yield, but as a disclosed tier per the caveat — needs a
safe-DCL fixture AND a DCL-without-volatile fixture as the regression pair). Defer #2 (new condition extraction).

## Real bugs surfaced (higher than `low` — these are MedDBase defects, not rig work)
- **`PdfService`/`PdfService2 Paginator.Initialise`** (Paginator.cs:33 / :87) — `paginatorSections` is assigned the array
  *before* the fill loop runs, so a racing thread sees a non-null, partially-filled array (garbage/null sections). Confirmed
  in source. Publish-before-init, worse than benign double-init.
- **`PerformanceLogger.get_Factory`** (PerformanceLogger.cs:19-32) — a lost double-init constructs two loggers and runs
  `instance.Startup()` on the loser: a side-effecting startup run twice, not an idempotent cache fill.
- _These argue `lazy_init_race` should not be uniformly `low`: an init with side effects or partial-publish deserves a
  severity bump over the idempotent regex/dict cases. File the two bugs against MedDBase (GitLab) separately from this card._

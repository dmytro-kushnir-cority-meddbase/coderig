# `race_window` recall gap: check-then-act over a concurrent/shared COLLECTION

**Status:** todo · **Found:** 2026-06-26 (MR !10788 review) · **Family:** detector-recall (FR-1)

## The gap
`race_window` (`FactHazardDeriver.DeriveRaceWindows`) pairs a `shared_state:read` with a `shared_state:mutate`
of the **same static-field cell** (keyed on `ResourceType` = the field-slot DocID) — the `if (_x == …) _x = …`
shape. It does **NOT** fire on a **check-then-create over a concurrent/shared COLLECTION** — the classic
`if (!dict.ContainsKey(k)) dict[k] = create();` / `TryGetValue`-then-`Add` TOCTOU — because the "read" leg is a
method call on the collection (`ContainsKey`/`TryGetValue`), not a `shared_state:read` of a field cell, so the
two legs never pair. rig emits only `shared_state:mutate <Collection>` with **no** `race_window` tag.

**Evidence (!10788, `Cache.ProvideEpisode`):** a non-atomic `ConcurrentDictionary` check-then-create over
`EpisodeDic` under parallel field-import workers — a real duplicate-creating race — showed in rig as just
`shared_state mutate ConcurrentDictionary`, no hazard. The race was caught via `impact` (guard added on the fix)
+ source reading, NOT the detector. So a reviewer trusting "no `race_window`" would miss the class. (Captured as
a caveat in the `meddbase-review` skill, now in `C:\Git\meddbase-skills`; this card is to close the recall gap itself.)

## Shape to detect
A method that, on a shared/static collection `C` (a `ConcurrentDictionary`/`Dictionary`/`HashSet`/… in a static
or shared-instance field), does a membership **read** (`ContainsKey`/`TryGetValue`/`Contains`/indexer-get-in-`if`)
followed (later line, same method) by an **add/set** (`Add`/`TryAdd`/indexer-set/`[k]=`) of the same collection —
i.e. a check-then-act that isn't atomic.

## Calibration is the hard part (why it's not on-by-default yet)
Concurrent collections are *often used safely* — `GetOrAdd(k, factory)` is atomic; a check-then-act **inside a
lock** is the correct double-checked pattern (exactly !10788's FIX). A naive "ContainsKey then Add" detector
would over-fire. Gate it:
- **Suppress when the write is lock-enclosed** — reuse the lock-span signal proposed in
  [[lazy-init-race-precision-tightening]] (the `FactResourceSpanRule`/`lock(){}` span). A check-then-act inside
  a `lock` is the safe shape, not a race.
- **Suppress atomic APIs** — `GetOrAdd`/`AddOrUpdate`/`TryAdd`-only (no prior read) are not check-then-act.
- **Confidence LOW + disclosed heuristic** (like `lazy_init_race`); FP-calibrate on the real MedDBase store
  before on-by-default (a structurally-true detector that fires 100× is noise).

## Why it matters
This is a genuine under-report: a whole class of real concurrency bug (the duplicate-create TOCTOU) is invisible
to the highest-severity-adjacent detector. Recall gaps are worse than precision gaps for a bug-finder. Pairs
naturally with the lock-span work — build that first, then this detector rides the same lock signal for its FP
suppression.

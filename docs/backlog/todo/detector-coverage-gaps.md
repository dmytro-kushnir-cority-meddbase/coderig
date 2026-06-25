## Detector coverage gaps (RCA production corpus)

Source: `meddbase-analysis/docs/rca-corpus-meddbase.md` (real production reverts/fixes), made executable by
`tests/Rig.Tests/Fixtures/ProductionFixCorpus.cs` + `…/Analysis/ProductionFixCorpusTests.cs` — each bug is
compiled in-memory and run through the real extract→derive with shipped rules; `_Gap_`-named tests pin a
KNOWN blind spot. **Status (2026-06-23): 4 of 7 FR families implemented + corpus-proven** (FR-1/1b shared-
mutation-under-concurrency *candidate*; FR-3 N+1 looped read; FR-4/1e per-EP effect/read-set + hazard delta in
`impact`; FR-6 unserializable `object_store` payload). The uncovered families, promoted here:

- **FR-7 — cache coherence (entity_cache write with no matching invalidation). ✅ SHIPPED** (see
  [done/fr7-cache-coherence.md](../done/fr7-cache-coherence.md)). Maps the largest RCA cluster: !7721 (Redis
  entity-cache invalidation), #4199 (import doesn't invalidate person cache), #3941
  (billing↔import invalidation missing), #4367/#4235 (signing-key cache miss), #940 (corrupted cache keys via
  race). Likely shape: a derive-side reachability rule — an `entity_cache:write` (or its keyed variant)
  reachable on an EP whose reach lacks a corresponding invalidation call for the same key/region. Design first:
  what counts as an "invalidation", per-key vs blanket, and how to avoid the FP class FR-1 hit (disclose
  candidate, don't claim proof). Ship with a corpus fixture per mapped case.
- **FR-1 PRECISION (not recall) — the pinned `_Gap_` sub-patterns. PARTIALLY DONE (`039d2eec`).** FR-1 already
  fires (recall is fine); the gap is false positives + uncoupled findings.
  - **DONE this pass** (the triage half — UX panel #2, no new extraction): `#cctor` exemption (CLR type-init
    lock → not a race; was a `lazy_init_race` FP class), per-`(type, method)` dedup with a `×N` count in the
    rollup (the 26-site `HandleSettingsToBeLogged` cluster → one row), and a `--exclude-namespace` filter for
    framework/vendored noise. Validated on MedDBase (`#cctor` 16→0, real findings survive).
  - **STILL OPEN** (needs NEW extraction + a re-index, NOT query-side): (a) **#2930** TOCTOU coupling /
    conditional-overwrite-vs-true-RMW — distinguishing `S.X = f(S.X)` (real RMW) from `S.X = independentValue`
    (conditional overwrite, agent C's dominant `high`-tier FP) needs a fact for whether a write's RHS DEPENDS
    on the read cell; the extractor doesn't capture it today. (b) **#4246** lock-attribution across a
    wrapper/callback boundary — needs cross-method happens-before/span propagation. (c) **#2892** quantified
    per-EP query-count. These are the FR-1 follow-up; until then race_window stays a disclosed candidate.
- **FR-2 — AsyncLocal/ThreadStatic flow + deadlock / lock-ordering. WON'T DO (declined by design).** Motivating
  bugs (!10208 ThreadStatic→AsyncLocal, !7194 SQL background deadlock, #311) stay pinned in the corpus as named
  targets, but detecting them needs AsyncLocal/ThreadStatic *flow* modeling and lock-ordering analysis — both
  beyond the fact-based, query-time reachability model (same boundary as the "no path-sensitive analysis"
  principle). Recorded so it isn't re-attempted; revisit only if rig ever grows a real type/value-flow pass.

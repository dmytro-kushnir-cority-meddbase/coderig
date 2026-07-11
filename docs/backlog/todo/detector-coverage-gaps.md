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

---

## New detector families (from 2026-06-14/15 validation sweep)

**Source:** extracted from `docs/rig-review-issues.md`, 2026-06-25 (VS-G2, VS-G3, VS-G4 residual, VS-G5
residual, VS-G7 residual; the quick-win slices of these were already shipped 2026-06-15).

### VS-G2 — `permission:assert` family (new detector family)

**Status: rules-only base is cheap; the valuable per-right-name extraction needs nth-argument engine work.**

`CertificateEntity.AssertRight/AssertAnyRight/AssertAccountRight`, `HasRight` (non-throwing),
`PersonCache.IfCanView` (every patient load gates `CanViewPatientDemographic`) are entirely unmodeled.
The `throw:raise CertificateAccessException` IS visible, but the Rights flag / gate semantic is not.
50+ entities affected. Evidence: `CertificateEntity.cs:977`, `PersonCache.cs:53`, `BillingItemEntity.cs:120`.

Design (2026-06-15 survey):
- **"An assert happened here" — rules-only**, zero effort: `declaringType=CertificateEntity` gate already
  excludes the polluting LLBLGen entity-nav `AssertRight()`. Emit `permission:assert` with
  `resource:declaring_type`. Ships the provider.
- **Per-right-name capture — needs nth-argument engine primitive.** `Rights.*` sits at DIFFERENT positions:
  arg-1 for dominant `CertificateEntity.HasRight(cert, Rights.X.Y, txn)`, arg-0 for chamber/workflow
  wrappers. `argument_name` lifts arg-0 wrappers only; arg-1 majority needs `argumentIndex:1` support.
  Bitwise-OR composites (`A | B`) and param-flowed variables are further nuances.
- Secondary clean surfaces: `[Authorize(Roles=Roles.X)]` attributes (EnterpriseApi ~68), isolated
  `RequirePermission(PermissionKinds)` (WebDav).

### VS-G3 — `config:read` family (new detector family, PARTIALLY SHIPPED)

`Settings.*` (`CallerMemberName`-keyed), `ConfigurationManager.AppSettings[key]`,
`AccountConfiguration.GetItem` are/were unmodeled. Feature-flag branches that gate whether SOAP submission
fires are invisible without this.
Evidence: `Settings.cs:878`, `Master_HealthcodeServiceImpl.cs:1043/1048`.

**Note:** `config:read` on `IConfiguration`/`ConfigurationManager`/`ConfigurationBinder` was SHIPPED
2026-06-15 (`f9fc2e0`, builtin rule — `GetSection`/`GetValue`/`GetConnectionString`, `resource:string_argument
argumentIndex:0`). The residual is MedDBase-specific `Settings.*` CallerMemberName pattern and legacy
`AppSettings["x"]` (0 invocation facts — confirmed dead). Verify what remains open in the meddbase
`rig.rules.json` vs. what was added.

### VS-G4 — Generic SOAP proxies (PARTIALLY SHIPPED — verify residual)

The generic `SoapHttpClientProtocol.Invoke` builtin rule was SHIPPED 2026-06-15 (lights Labs/Mirth/LabsServer
proxies). The Healthcode per-method rules were deduplicated 2026-06-16 (#17). Verify: any SOAP proxy type
that inherits `SoapHttpClientProtocol` but whose `Invoke` call rig attributes to a DIFFERENT declaring type
would still miss the rule. Use `rig refs --method Invoke` to audit.

### VS-G5 — FHIR (`Hl7.Fhir.Rest.FhirClient`/`BaseFhirClient`) — SHIPPED

FHIR provider SHIPPED 2026-06-15 (builtin-rules.json — ops read/search/create/update/delete/transaction/
operation; ILSpy-verified against Hl7.Fhir.Base 5.2.0). Verified live: GPConnect `fhir:operation`/`fhir:read`,
PDS `fhir:search`/`fhir:read`. No residual.

### VS-G7 — `object_store:read` missing on generic `GetInstance<T>` — root cause still open

The 2026-06-15 fix (adding `LookupByDTO`/`GetDynamicQueryWithDTO` + 4 `*.ObjectStoreExtensions` types to
the meddbase rule) addressed the SHALLOW attribution gap. The original report — generic `GetInstance<T>`/
`GetObjectInstances<T>` missing because of a `` `1 `` arity suffix in the DocID — was reclassified: the
deriver already strips `` `N `` at match time (`FactEffectDeriver.cs:90`). Root cause of the
`GetInstance<T>` miss is still open; needs a targeted `rig refs` dig to find the exact declaring type and
method name rig sees in the store before adding a rule.
Evidence: `ObjectStore.cs:622`, `:1095`.

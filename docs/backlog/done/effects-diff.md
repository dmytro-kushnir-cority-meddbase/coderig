# `effects-diff` (was: `write_set_divergence`)

> **GENERALIZED 2026-06-25 (commit `1a2cdc94`):** collapsed into the neutral generic primitive
> **`rig effects-diff <a> <b> [--only provider:op]`** (symmetric diff of two EPs' reachable effect-sets).
> "write-set divergence" is now a USAGE — `effects-diff … --only llblgen:write,bulk_write,delete` — with the
> operator/agent supplying the "same logical op" pairing + interpretation. The config-driven detector + rule
> schema were removed. Open follow-ups: **precise EP resolution** (strip `~λ`/disambiguate overloads — today
> over-matches silently), **effect-set scoping** (deep transitive reach pollutes it — the `ProfileEntity` FP),
> and a correctly-paired GI-4385 calibration (`DocumentImportEntity.Save`, not the `SaveImportedDocument` stub).
> The original detector spec below is kept for context.

---

## Detector: `write_set_divergence` — import/API path writes fewer tables than the UI path (NEW, corpus-surfaced)

**Status:** ✅ CORE SHIPPED (2026-06-25, commit `write_set_divergence`) — pure deriver + rule schema + provider
+ `HazardKinds` + `DeriveCommand` wiring + 7 unit tests, mirrors `cache_coherence` end-to-end. Rule-declared EP
pairs (option 1 below). **Family:** consistency/dual-write sibling · **Found:** 2026-06-24 (100-bug GitLab triage).

### Calibration reality (2026-06-25) — the PAIRING granularity is everything; treat every finding as a candidate
First real-store calibration (Document `Save` vs `SaveImportedDocument`) flagged PersonEvent + Profile — and an
**independent adversarial source-verification REFUTED both**:
- **PersonEvent = mis-paired EP.** `SaveImportedDocument` is a narrow building-block stub; the real import-level
  peer is `DocumentImportEntity.Save`, which DOES write PersonEvent one frame up (`BuildPersonEvent`,
  `DocumentImportEntity.cs:180`). Pairing the canonical full save against a sub-step manufactures a phantom gap.
  ⇒ **EP pairs must be the same GRANULARITY** (entry-point-level save vs entry-point-level import), the "hard
  part" flagged below. This is the dominant FP source.
- **Profile = transitive-reach artifact.** Neither path writes Profile as part of the save; the primary's Profile
  write is a deep (d12-13) transitive reach through session/logout machinery. ⇒ the per-EP write-set is polluted
  by deep unrelated reach; consider a depth/scoping bound or an exclude-namespace filter on the write-set.
- **Substring EP-resolution over-matches** overloads + `~λ` lambdas: `DocumentImportEntity.Save` matched 5 nodes
  → the pair was silently skipped (need exactly 1). ⇒ resolve EP patterns to the METHOD node precisely (strip
  `~λ` suffixes; disambiguate overloads / accept an exact DocID), else useful pairs can't be expressed.
Net: the detector is mechanically sound but is a DISCLOSED CANDIDATE generator (medium) requiring per-finding
source verification — like FR-7. Next increments: (a) precise EP resolution, (b) write-set scoping/depth bound,
(c) calibrate on a correctly-paired corpus dyad (GI-4385 needs `DocumentImportEntity.Save`, not the stub).

### The hazard
Two entry points that perform the "same" logical operation on the same entity — typically the **canonical UI
save path** and an **import / Enterprise-API path** — write **different sets** of tables. The
secondary/derived tables the UI path maintains (`PERSON_EVENT`, junction/link tables, counter or denormalized
columns, audit rows) are silently skipped by the import path, leaving them stale or inconsistent. No
exception, no missing row in the primary table — just a quietly-incomplete write-set.

### Corpus evidence (the standout pattern from the 100-bug sweep)
- **GI-4385** — import updates `DOCUMENT` but leaves `PERSON_EVENT` untouched (status read goes stale).
- **GI-3951** — import path misses ~5 junction tables that the UI write maintains.
- Recurs across the dual_write cluster; the triage flagged this as a class NOT captured by FR-1..7 +
  `static_init_capture`. Full evidence in `docs/meddbase-bug-corpus.md`.

### Why it's rig-shaped (cheap — no new extraction)
rig already has per-EP reachable `db:write`/`llblgen:*` resource-sets. The detector is a **structural set-diff**:
for a pair of EPs (import-EP, ui-EP) operating on entity T, compute each EP's reachable write-set and flag
tables in `writes(ui) \ writes(import)` (and vice-versa) as candidate divergence. Set-algebra over facts rig
already derives.

### The hard part — pairing the EPs (the real design question)
"Same logical operation" is not a fact rig has. Options, cheapest-first:
1. **Rule-declared pairs** — `{entity, uiEntryPoint, importEntryPoint}` triples in `rig.rules.json`. Precise,
   zero false pairs, but manual; good for a first slice on known import/UI dyads.
2. **Anchor-table heuristic** — EPs whose write-set contains the same *primary* entity table T are treated as
   peers; diff their full write-sets. Automatic but noisier (an EP that legitimately does a narrower op trips
   it) → emit as a disclosed CANDIDATE, never a verdict.
3. **Reference write-set** — pick the EP with the maximal write-set for T as the "canonical" baseline; flag the
   others' shortfalls. Risky (maximal ≠ correct) — research only.
Start with (1) on the corpus dyads to prove the set-diff core, then evaluate (2) for recall.

### FP / honesty notes
- A narrower write-set is often CORRECT (the import genuinely shouldn't touch T2). This is candidate
  generation; significance needs the pairing rule or a human — same posture as FR-1.
- Bounded by what's instrumented: an EP that maintains a secondary table via an in-memory/cache path rather
  than a `db:write` won't show in the write-set.

### Validation methodology (applies to the whole corpus — note the closed-bug trap)
**A fixed/closed bug is already corrected in a recent index**, so the detector will show it absent there. To
validate against a fixed case, index the **fix commit's PARENT** and run the detector on that store
(`--store <parent-sha>`); confirm it fires, then confirm the fix commit's store is silent — a before/after
golden check. Open bugs (e.g. GI-4199, GI-4385) are still present in current `LATEST` and validatable as-is.
For `write_set_divergence`: build fixture dyads from GI-4385 / GI-3951 at their pre-fix commits.

# Production RCAs → detectors / review invariants

A running, curated index of **production incidents** (root-cause analyses) that motivate `rig` work — either a
**generic detector** (baked into the engine) or a **project-specific review invariant** (domain knowledge the
operator/LLM supplies; not engine config). Future reference; the planned project-dependent **review skill** will
be derived from this list + [backlog.md](backlog.md), enriched step by step.

Detail for the broader bug sweep lives in [meddbase-bug-corpus.md](meddbase-bug-corpus.md); this file is the
curated **incident → what-it-motivates** index, plus the generic-vs-domain split.

## The split (why some incidents become engine detectors and some don't)
- **Generic detector** — the hazard is a universal structural property (a cycle, a race window, a missing
  invalidation after *any* bulk write). Stable across projects → lives in the engine, config-light.
- **Domain-bound check** — utilizing it needs project knowledge that churns (which two paths are "the same
  logical op", which entity's companion writes are required). Brittle if frozen in engine config → exposed as
  an explicit operator-driven CLI command and/or a review-skill invariant; the human/LLM supplies the domain
  input per use. (`write_set_divergence` is the first of this kind.)

## Index

| Incident | Shape | Motivated | Kind | Status |
|---|---|---|---|---|
| **Cache invalidation** (recurring; a top class in prod triage that kicked off this work) | entity mutation with no companion cache invalidation on the path | `FR-7` `cache_coherence` | generic detector | shipped + FP-calibrated |
| **GI-#4460** (contact delete) | `RemovePersonContactLinks` bulk-writes Person FKs, no `PersonCache` invalidation | the `cache_coherence` candidate it surfaced | generic detector | candidate → reviewed **benign FP** (stale FK resolves via just-invalidated `ContactCache`) |
| **GI-4385** | import updates `DOCUMENT` but leaves `PERSON_EVENT` untouched (write-path divergence) | `write_set_divergence` | **domain-bound** (CLI command) | command-ized; calibrate with the *import-level* EP (`DocumentImportEntity.Save`, not the `SaveImportedDocument` stub — mis-pairing was an FP) |
| **GI-3951** | import path misses ~5 junction/link tables the UI path maintains | `write_set_divergence` | domain-bound | same command |
| **GI-862** | consultation SNOMED wording wrong until restart — config frozen in a `static readonly` field initializer | `static_init_capture` | generic detector | proposed (backlog) |
| **GI-4199** | existing document import does not invalidate the person cache (cross-resource: Document write ⇒ Person cache) | `FR-7` cross-resource enhancement | generic detector | proposed (backlog) |
| **GI-4448** | location name change doesn't reach existing cached SbS sessions (cross-resource: Location ⇒ cached session) | `FR-7` cross-resource enhancement | generic detector | proposed (backlog) |
| **GI-4367** | Entra signing-key cache should invalidate on cache miss (external change, no local mutation) | a distinct "revalidate-on-miss" staleness detector | generic detector | proposed (backlog) |

## Conventions
- Add a row when a prod incident drives detector/invariant work. Keep it one line; link the bug + the backlog
  section for detail.
- Mark **generic** vs **domain-bound** explicitly — it decides whether the logic lands in the engine or as an
  operator/skill-driven check.
- When a candidate is reviewed, record the verdict (real / benign FP / mis-paired) so the review skill inherits
  the calibration, not just the hypothesis.

## MedDBase staleness/cache-coherence corpus (validates FR-7 + `static_init_capture`)

Recent `Bug 🐛`-tagged GitLab issues confirming the cache/staleness family is a live, recurring defect class
(probed 2026-06-24, `mms/meddbase-main-application`). Use as FR-7 / `static_init_capture` regression corpus.

| Issue | Shape | Maps to |
|---|---|---|
| **GI-862** Consultation SNOMED wording wrong until restart | config value frozen in `static readonly` field init | `static_init_capture` (NEW) |
| **GI-4199** Existing document import does not invalidate person cache | write (doc import) with no companion cache:invalidate on patient-record cache; "wait 30 min" lifetime flush | reads textbook but **FR-7 misses it as configured** — see validation below |
| **GI-4448** Location name change doesn't reach existing SbS sessions | write (location rename) not invalidating cached sessions; "eventually updates" on unrelated event | FR-7 variant, **cross-resource** (location → cached session) — same hard cross-resource shape as 862 |
| **GI-4367** Entra signing key cache should invalidate on cache miss | cache lacks negative/miss revalidation on external key rotation | staleness, but **not** FR-7 (no local mutation; external change) — a "cache miss should revalidate" pattern; track as a distinct future detector |

Takeaway: FR-7's "missing invalidation after a mutation" plus the new "frozen-in-static" capture cover GI-862,
GI-4199, GI-4448. The cross-resource cases (4448, 862, **4199**) need the resource-correlation to bridge a
derived dependency, which FR-7's same-resource scoping does not yet do — the known cross-resource limit.
GI-4367 is a separate "stale cache on external change / revalidate-on-miss" class worth a future entry.

### GI-4199 validation (2026-06-24, store `caa9373f` — substrate YES, current rule NO)
Traced the import write path against the store (the bug is OPEN, so the buggy code is in `LATEST`):
- **Substrate sees it:** `DocumentEntity.SaveImportedDocument` reaches **5 `llblgen` writes and 0
  `cache:invalidate`** (`rig reaches … --only cache,llblgen`). The person-cache invalidation API it should call
  exists (`Application.Core.Messages.PersonModelCacheAddOrUpdate*.Tell(...)`). The missing-invalidation fact is
  present and queryable.
- **FR-7 as configured does NOT fire, for TWO reasons:** (1) **anchor mismatch** — the rule anchors on
  `llblgen:bulk_write`; this is a *per-entity* `DocumentEntity.Save` (FR-7 deliberately skips per-entity saves,
  assuming self-invalidation). (2) **same-entity scoping** — the mutation is on **Document**, the stale cache is
  the **Person** record (patient record aggregates its documents). The rule requires a *same-entity*
  invalidate; it cannot express "Document.write ⇒ Person cache:invalidate."
- **Conclusion:** GI-4199 demotes from high→**medium** fit, gated on the SAME cross-resource enhancement as
  862/4448. Three proven cases now justify the **declared cross-resource dependency** feature for FR-7
  (`{ownerEntity: Person, partEntity: Document, …}` → a write on `partEntity` obligates an invalidate on
  `ownerEntity`'s cache). This is the single highest-leverage FR-7 upgrade; the substrate already supports the
  query.

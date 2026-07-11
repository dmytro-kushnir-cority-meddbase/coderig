# MedDBase bug corpus — rig detector triage (100 latest open `Bug 🐛`)

**Source:** `mms/meddbase-main-application` GitLab, `glab issue list --label "Bug 🐛" --per-page 100` (open issues).
**Probed / triaged:** 2026-06-24.
**Method:** all 100 triaged by title + labels into rig hazard-detector families; **10 drilled** (description fetched and mechanism verified — marked ✅ below). Title-only rows are weaker — a backend hazard can hide behind a UI-sounding title and vice-versa.
**Purpose:** regression/validation corpus for the rig bug-detector backlog (FR-1..7, `static_init_capture`, `write_set_divergence`) in [backlog.md](backlog.md).

> **Provenance note.** A first automated pass reported this file as written; it was not (the agent did one tool call and its per-issue mechanisms were title-inferred, not fetched). This version is rebuilt from the real list + 10 verified descriptions. Family/fit counts here are **stricter** than that pass — in particular `cache_coherence` is split from `stale_read`, and `static_init_capture` has **no** instance in this 100-window (GI-862, its corpus case, is older).

## Detector families (what rig could plausibly detect)
Backend/structural only — NOT UI copy, layout, validation messages, permission display, typos, error-handling polish.
- **cache_coherence** — in-memory/distributed cache not invalidated after a change → stale until lifetime flush/restart.
- **stale_read** — a DB-level derived/denormalized value (counter, status copy, recompute) not refreshed when its source changes.
- **write_set_divergence** — an import/API write path persists *fewer* tables than the canonical UI path for the same entity (secondary/junction/counter tables left stale). NEW detector.
- **race_window** — check-then-act / double-submit / concurrent-insert TOCTOU.
- **serialization_contract** — payload/type-contract mismatch (cross-runtime, versioning, unsupported types).
- **n_plus_1** — read amplification.
- **out_of_scope** — not a code-structural hazard rig models.

**fit** = likelihood rig could actually flag it: `high` (substrate present + structural + pairable/same-resource) · `medium` (tractable but needs a feature — cross-resource dependency, EP pairing) · `low` (rig sees the effects but detection needs semantic knowledge it lacks).

---

## Summary

| Family | Count | Fit spread |
|---|---|---|
| race_window | 8 | 4 high · 1 med · 3 low |
| stale_read | 7 | 0 high · 3 med · 4 low |
| write_set_divergence | 5 | 2 high · 1 med · 2 low |
| cache_coherence | 3 | 1 high · 2 med · 0 low |
| serialization_contract | 1 | 0 high · 0 med · 1 low |
| n_plus_1 | 1 | 0 high · 1 med · 0 low |
| **in-scope total** | **25** | **6 high · 8 med · 11 low** |
| out_of_scope | 75 | — |

---

## Drilled — verified promising cases (✅ description read)

| # | Family (fit) | Verified mechanism | Maps to detector / fit rationale |
|---|---|---|---|
| **#4385** | write_set_divergence (high) | Patient Portal reads a doc's deletion status from `PERSON_EVENT`; data-import updates `DOCUMENT` but **not** `PERSON_EVENT` → portal shows deleted docs as live (and vice-versa). | Textbook `write_set_divergence`: import write-set ⊊ UI write-set. rig has per-EP `db:write` sets; diff import-EP vs portal/UI-EP. |
| **#3951** | write_set_divergence (high) | Importing billing items doesn't create `BILLING_ITEM_APPOINTMENT_SERVICE(_MODULE)` rows that manual invoice-raise always creates → import-sourced invoices can't be sent to Healthcode. | `write_set_divergence` — the founding corpus case. Pairable: import-EP vs raise-bill-EP on the billing-item entity. |
| **#4016** | write_set_divergence (med) | Billing items missing from imported invoice (same family as #3951; not separately drilled in depth). | Same set-diff; medium pending confirmation it's the same junction-table shortfall. |
| **#4155** | race_window (high) | Booking endpoints (`book-time-slot-02`, `book-02`, `reserve-time-slot-02`) double-book when requests arrive near-simultaneously — check-availability-then-insert with no guard. | Classic TOCTOU. FR-1 candidate: mutation on a slot resource with no `lock`/`async_lock` on the path. |
| **#3891** | race_window (high) | Reserve a slot on an active charge band, then delete the band; confirm/book throws no error — the confirm path re-validates nothing. | TOCTOU on charge-band state between reserve and confirm; FR-1 / guard-delta shape. |
| **#3696** | race_window (high) | Patient double-clicked "authorise report"; IIS logs show two requests; report ended not-properly-authorised, no idempotency guard. | Double-submit; FR-1 / missing-idempotency candidate on the authorise EP. |
| **#3250** | race_window (med) | Parallel anonymous OH-report requests (authorise in one tab, 2FA in another, concurrently) → unexpected state; reporter says "can see it in code." | Concurrency on report-release; FR-1 candidate. Medium: anonymous/parallel path harder to pair. |
| **#3835** | stale_read (med) | Changing a charge band via eAPI does not enable the Patient-Portal options in the Patient Record — a derived portal-visibility flag isn't recomputed. | Cross-resource derived staleness (charge band → portal visibility). Same cross-resource gap as FR-7. |
| **#3375** | stale_read (med) | Rolling back a tasks import removes the tasks but the Work **counter** isn't decremented — counter column not maintained on the rollback path. | Denormalized counter not recomputed; write_set/stale on the rollback EP vs the import EP. |
| **#3236** | stale_read (med) | Task counter not updated when a patient moves inactive→active (task appears, counter doesn't); counter only updates on a new task arriving. | Derived counter recompute missing on the status-change path. |

---

## Full triage (all 100)

| # | Title (short) | Family | Fit |
|---|---|---|---|
| 4448 | Location name change doesn't reach existing SbS sessions | cache_coherence | med |
| 4406 | Error importing new contract | out_of_scope | — |
| 4405 | Contract end date required when importing | out_of_scope | — |
| 4403 | Reminder email sent after DNA status | out_of_scope | — |
| 4392 | New import template overrides existing one | race_window | low |
| 4386 | Lab result template code formatting | out_of_scope | — |
| 4385 | Portal should read DOCUMENT for doc status ✅ | write_set_divergence | high |
| 4367 | Entra signing key cache invalidate on miss | cache_coherence | high |
| 4363 | Pathways not triggered when label has spaces | out_of_scope | — |
| 4347 | Rework referral collaboration message feed | out_of_scope | — |
| 4315 | Issues deleting departments and companies | out_of_scope | — |
| 4309 | Pathway expr broken with string and/or/not | out_of_scope | — |
| 4296 | Patient Metafield allows duplicate keys ✅ | race_window | low |
| 4293 | Import error when removing entity | out_of_scope | — |
| 4291 | 'Deleted' status shown for both CF actions | out_of_scope | — |
| 4282 | Negative grouped service credits not shown | out_of_scope | — |
| 4260 | Imported entity rollback error | out_of_scope | — |
| 4233 | Able to delete medical policy used in config | out_of_scope | — |
| 4215 | Referral appt invalid initial date/time | out_of_scope | — |
| 4203 | Allow API to whitelist IP (feature) | out_of_scope | — |
| 4199 | Existing document import doesn't invalidate person cache ✅(prev) | cache_coherence | med |
| 4169 | Activity logs credit allocate when it's not | out_of_scope | — |
| 4158 | Driver's licence expiry date validation | out_of_scope | — |
| 4155 | Booking endpoints can be double booked ✅ | race_window | high |
| 4144 | SSO IP-range error not visible | out_of_scope | — |
| 4120 | Data field added without permission | out_of_scope | — |
| 4113 | Pathway 'Key doesn't exist in map' | out_of_scope | — |
| 4112 | Bulk invoices use today's date | out_of_scope | — |
| 4078 | @prettydate on Medical History print | out_of_scope | — |
| 4075 | Unexpected eapi doc changes (pre-versioning) | serialization_contract | low |
| 4055 | Slot finder date resets on Contract dropdown | out_of_scope | — |
| 4048 | Typo in referral v1 | out_of_scope | — |
| 4027 | Error downloading empty CSV (MI reports) | out_of_scope | — |
| 4023 | XSS vulnerability on referral | out_of_scope | — |
| 4021 | 'Query Modified' log logs nothing | out_of_scope | — |
| 4016 | Billing items missing from imported invoice | write_set_divergence | med |
| 3986 | T&C activity log mixed with consent setting | out_of_scope | — |
| 3951 | Import invoices/appts doesn't populate all tables ✅ | write_set_divergence | high |
| 3931 | Liquid medical report template for new chamber | out_of_scope | — |
| 3925 | Index out of range selecting address | out_of_scope | — |
| 3907 | Adding patient blocks SbS breadcrumb return | out_of_scope | — |
| 3906 | Networked doc type shows [Deleted] cross-chamber | stale_read | low |
| 3904 | PPQ shown on referral attached docs | out_of_scope | — |
| 3896 | Doc type dropdown account types differ | out_of_scope | — |
| 3894 | Module length not added to EAPI appt duration | out_of_scope | — |
| 3891 | Confirm slot on deleted charge band ✅ | race_window | high |
| 3874 | T&C cannot be withdrawn once accepted | out_of_scope | — |
| 3863 | New appt type/company assigns template unbidden | write_set_divergence | low |
| 3862 | E&C shows credits for different patient sex | out_of_scope | — |
| 3859 | Meeting not showing in 1-day view | out_of_scope | — |
| 3837 | Structured template duplicates header image | out_of_scope | — |
| 3835 | Chargeband via eApi doesn't enable portal options ✅ | stale_read | med |
| 3827 | Slot finder slow when appt type denied | n_plus_1 | med |
| 3818 | Cancellation % includes VAT of exempt services | out_of_scope | — |
| 3771 | OIDC client secret not masked | out_of_scope | — |
| 3761 | Refunds fail when membership charge changed | out_of_scope | — |
| 3744 | Submit Appointment Response bug (vague) | out_of_scope | — |
| 3705 | Pathway var saves to wrong var (case-insensitive key) | stale_read | low |
| 3696 | Double-click to authorise report ✅ | race_window | high |
| 3688 | Unfriendly error on blank date (match payments) | out_of_scope | — |
| 3682 | Localisation in questionnaire answers | out_of_scope | — |
| 3617 | Sent docs/invoices don't display images | out_of_scope | — |
| 3593 | EAPI time-slot booking 500 response | out_of_scope | — |
| 3581 | Error adding rule with no name | out_of_scope | — |
| 3575 | Price list schema capitalisation | out_of_scope | — |
| 3566 | No error importing companies w/ same sign-up code | race_window | low |
| 3553 | Dependency error importing invoices | out_of_scope | — |
| 3543 | Create patient w/ same billing & employer co. via portal | race_window | low |
| 3542 | Auto-complete field bugs | out_of_scope | — |
| 3524 | Service not provided if in 2 credit rules | out_of_scope | — |
| 3523 | Parent/child dept assign differs OHP vs app | out_of_scope | — |
| 3521 | OH accessible doc types list issues | out_of_scope | — |
| 3515 | Medical user certificate not recalculating | stale_read | low |
| 3509 | Auth code with chars like > | out_of_scope | — |
| 3475 | Overlapping name on invoice generation | out_of_scope | — |
| 3456 | No preview attaching Word docs | out_of_scope | — |
| 3428 | Unclear EAPI doc-type create error | out_of_scope | — |
| 3425 | Can't import doc types of all ACCOUNT_TYPE | out_of_scope | — |
| 3387 | Inconsistent SLA checks/logs for referrals | out_of_scope | — |
| 3382 | Error selecting 'Other' as Payer | out_of_scope | — |
| 3380 | Referral History doesn't show manager change | write_set_divergence | low |
| 3375 | Tasks import rollback doesn't update counters ✅ | stale_read | med |
| 3344 | CK editor glitch on resize | out_of_scope | — |
| 3343 | Checkbox parameters on report not working | out_of_scope | — |
| 3339 | EAPI book w/ non-current employer | out_of_scope | — |
| 3328 | XSS opening doc-type jpg | out_of_scope | — |
| 3326 | Pathway started date wrong format | out_of_scope | — |
| 3321 | Unhelpful error: Invoice Number vs ID | out_of_scope | — |
| 3290 | Pathways form task not saving on change | out_of_scope | — |
| 3289 | Snomed codes not maintained on bind-field form | stale_read | low |
| 3283 | Charts missing in review-task preview | out_of_scope | — |
| 3275 | Can't import Invoice Grouping services | out_of_scope | — |
| 3273 | Billing Rules import schema checks | out_of_scope | — |
| 3266 | Charts error on duplicate datapoint labels | out_of_scope | — |
| 3250 | Parallel anonymous OH report requests ✅ | race_window | med |
| 3246 | Validation warning for future-dated payments | out_of_scope | — |
| 3245 | Import validation for Strict Invoice Validation | out_of_scope | — |
| 3241 | Invoice error when too many matches | out_of_scope | — |
| 3240 | Refunds show Payment ID on invoice templates | out_of_scope | — |
| 3236 | Task counter not updated on inactive→active ✅ | stale_read | med |

---

## Net read

- **The dominant rig-addressable theme is consistency/staleness**, not classic concurrency: `stale_read` (7) + `write_set_divergence` (5) + `cache_coherence` (3) = **15 of 25 in-scope**. These share one root — *a change to source X leaves a derived/secondary store Y unrefreshed* — across three layers: in-memory cache (cache_coherence), denormalized DB columns/counters (stale_read), and parallel write paths (write_set_divergence).
- **`race_window` is the next cluster (8)** and the most *immediately* detectable: TOCTOU/double-submit on EAPI booking & authorise endpoints (#4155, #3891, #3696, #3250) — FR-1 candidate-generation territory (mutation on a resource with no `lock`/`async_lock`/idempotency on the path).
- **`write_set_divergence` is corpus-confirmed** (#3951, #4385, #4016) — import/API write-sets ⊊ UI write-sets. Validates promoting it from idea to a specced detector ([backlog.md](backlog.md)).
- **Recurring blocker, evidence-backed:** the cross-resource cases (#4448, #4199, #3835, plus older GI-862/4448) all need **declared cross-resource dependencies** (write on X obligates invalidate/recompute on Y) — the single highest-leverage FR-7 upgrade; the substrate already supports the query, only same-resource scoping blocks it.
- **No NEW family beyond the known set** surfaced this round. #4367 ("revalidate cache on miss" for rotated Entra keys) is a distinct *negative-cache / revalidate-on-miss* sub-pattern worth a future entry; #3705 (case-insensitive key collision in a dictionary) hints at a possible *key-normalization* hazard but is a single weak instance.
- **Caveat:** fit is conservative and title-triage is fallible. The 75 out_of_scope are dominated by UI/validation/error-message/permission-display bugs rig structurally can't model; a few (#4120 authz, #4233 referential-delete, #3339 employer-authz) are reachability-auth-adjacent and could be revisited if an auth-reachability detector is built.

---
name: meddbase-review
description: Review a MedDBase code change (merge request / diff / PR) for the defect classes the 500-issue triage corpus shows actually ship — using rig to ground the whole-program questions a diff-local reviewer can't answer. Use when asked to review a MedDBase MR/diff/change, check a save/import/endpoint/entity/migration change for missing or divergent effects/guards, audit an entry point's reachable effects, verify a "behavior-preserving" refactor didn't change behavior, or check that a storage/access migration's permission coverage is complete on both the user side and the system/migration side. Composes with the `rig` skill (drives `rig` for reachability/effects) and the MedDBase corpus (`meddbase-analysis/docs/reviewer-pitfalls.md`, `bug-triage-scan.md`).
---

# meddbase-review — corpus-grounded LLM review of a MedDBase change

Distilled from triaging 500 closed MedDBase Severity bugs (`meddbase-analysis/docs/reviewer-pitfalls.md` =
the full checklist; this skill is the operating procedure). You review the **local semantics**; **rig** answers
the **whole-program** questions. The discipline that catches the most: **rig narrows where to look; the source
code decides whether it's real.**

## Division of labor — know what you own vs what rig owns
- **You (the reviewer) own LOCAL semantics** — is this predicate/encoding/validation/id/logic correct. ~87% of
  real defects are this kind and **rig is blind to all of them** (it sees the effect fires, not whether the
  *value* is right). Don't ask rig these.
- **rig owns WHOLE-PROGRAM questions** a diff-local reviewer literally cannot answer: what else does this reach,
  does the sibling path do the same, which entry points reach this write **without** the guard, did a
  "behavior-preserving" refactor change the reachable effect set. **Ask rig these — then confirm in code.**

## The one meta-heuristic (it dominates the corpus — ~half of in-scope defects)
**A change touches one path; the bug is in the path it didn't touch.** Print vs on-screen, sibling endpoint,
EAPI vs UI, US vs UK locale, a re-save firing a hidden write, two tabs, the import counterpart of a manual save.
**When a diff changes one path, enumerate the parallel paths — other consumers, sibling endpoints, the other
locale/tenant/render target, the concurrent/re-entrant caller, the import-vs-manual counterpart — and verify
each.** This single habit is the highest-yield thing in this document.

## Per-EP workflow — map the effect tree, THEN verify in code (the spine)
For every **important** entry point the change touches — state-changing EPs in the diff, EPs that reach a
migrated/cert-gated access, and their **siblings** (not all 9,955; the ones that change state or that the
change's blast radius reaches):

1. **Map its full effect tree.** Don't review a save/import/endpoint from the diff alone — the diff shows the
   *local* edit; the effect tree shows what it *reaches*. Pull the complete reachable set and read it as a whole
   (writes, guards `permission:assert`, audit, cache-invalidation, the cert-gated cached access):
   - `rig tree <EP> --view full` — everything reachable
   - `rig tree <EP> --view effects` — just the effectful methods
   - `rig tree <EP> --view hazards` — the annotated hazards (cache_coherence / race_window / dual_write / …)
2. **Review the effect graph, then ALWAYS check the code.** rig's graph is a *grounding over-approximation* —
   reconstructed dispatch (CHA), rule-derived effects, one-hop devirtualization. **Every node, edge, and hazard
   is a CLAIM, not a verdict.** Before flagging anything, open the source at the `file:line` rig prints and
   confirm the effect/guard/path is real. (The bar from `docs/verified-bugs/`: a detector firing is a
   *candidate*; it becomes a finding only when the source genuinely exhibits it.) **The graph tells you where to
   look; the code tells you whether it's true.** This prevents both failure modes: trusting a false rig finding
   (e.g. a "race" that is actually a correct double-checked lock), and reviewing a diff blind to what it reaches.

## The `[LLM]` semantic checklist (you own these — rig is blind)
Full list + issue refs in `meddbase-analysis/docs/reviewer-pitfalls.md`. The recurring ten:
1. **Soft-delete / deleted-state** — does a `Status`/`IsDeleted` entity leak into a list/validation, or get
   wrongly excluded, or fail to cascade/label dependents — *for this context*?
2. **Validation missing / mis-ordered** — every external input bounds/type/uniqueness-checked, **before** the
   durable write, with **parity to sibling endpoints**?
3. **Business rule applied inconsistently** — is this VAT/credit/eligibility rule implemented identically at
   every call site? Grep the others.
4. **Representation divergence** — does a parallel renderer/projection exist (print vs view, list vs detail, API
   vs UI) and does the change make them disagree?
5. **Null / sentinel / zero-vs-null** — does this default/clear path write what the column's nullability expects?
6. **Insert-vs-update / stale "new" flag / dedup** — after the first save, does re-save **update** or duplicate?
   Is the saved id captured back into page/session state?
7. **Tenant / chamber isolation & authz** — every multi-tenant query carries the chamber/owner predicate; every
   mutating endpoint asserts the right on the **target's** owner?
8. **Localization / date / TZ** — configured culture (not server culture); date-only treated TZ-agnostic?
9. **Wrong argument / enum / id** — call shape correct but a wrong value flows in (effect fires, but wrong). Does
   each id/enum's provenance match the intended subject?
10. **External / cross-service contract** — emitted payload exactly matches the consumer's contract, incl. across
    the net48↔net8 boundary?

## The rig playbook (ask rig, confirm in code)
| Question the diff can't answer | rig command |
|---|---|
| Two paths to one write — do their effect+guard sets differ? | `rig effects-diff <epA> <epB> --only @parity` — read the **labeled** divergence (`permission:assert X` = a guard one enforces and the other doesn't; `llblgen:write Y` / `audit:write` = writes one does). |
| Does THIS entry point reach a hazard? | `rig tree <EP> --view hazards` (cache_coherence/race_window/dual_write/…) |
| Did my "behavior-preserving" refactor change effects? | `rig impact --base <pre-commit> --expect-no-effect-change` |
| Who reaches this write WITHOUT the guard? | `rig callers <write> --entrypoints` → for each, `rig reaches <EP> --only permission` (or effects-diff vs a known-guarded EP) |

The `@parity` preset = `--only permission --only llblgen:write --only llblgen:bulk_write --only llblgen:delete
--only audit` (the guard+durable-write+audit subset — the actionable ~25 rows, not the 318-row everything diff).

**Index the MR branch fresh — PREFERRED for any migration / new-reader change.** The store usually holds `main`,
not the branch, so the change's *new* sinks/readers aren't queryable. **For a migration review, index the branch
first:** check it out, build, `rig index … ` into its **commit-scoped store** (`.rig/<branch-sha>/`, which sits
*beside* `main`'s `LATEST` — non-destructive), then query *that* store. Only the fresh index sees **reach paths
the branch ADDS** — and a migration that introduces a new reader of the cert-gated access is exactly the gap a
`main`-only audit is blind to. (Cost: a full MedDBase index is heavy — whole monorepo + MSBuild for net48/.sqlproj
— so it's a setup step, not free.)

**Fallback (only when you can't build the branch): the pre-change CONSUMER surface.** Query `main` for
reachability + EP classification of the *consumers* (the call sites *above* the changed methods, which are
unchanged), and confirm the new sink wiring from **branch source** (`git show <branch-sha>:<path>`). **Disclose
the blind spot:** this cannot see EPs/readers the branch newly introduces — so "all readers guarded" is only
verified for readers that already existed on `main`. Re-index to be sure.

### Migration guard-gap — BIDIRECTIONAL (a refactor is a guard-gap factory)
Worked exemplar: **MR !10645** (object-store → cached-entity migration + permission safeguard). A storage/access
migration into a **cert-gated cached store** (in MedDBase, cached stores check a certificate on read/write
*themselves*) changes guard coverage on **both** sides. Enumerate every EP reaching the new access —
`rig callers <CachedEntity.access> --entrypoints --async` (`--async` to include background/scheduled runners):
- **User-facing EP reaching it but NOT the permission guard → leak** (the under-guard gap).
- **Background / system / migration EP reaching it → confirm its runner carries the certificate** the cached
  store requires. A profile-less background/migration path hitting a cert-gated access is **denied at runtime** →
  the migration "ran" but read/wrote nothing — a *silent functional break* with no error a diff-local reviewer
  sees. The migration adds permission on the *user* side; check the migration's *own runner* has it on the *new*
  side.
rig grounds *which* EPs reach the cert-gated access and *that* it carries a `permission:assert`; whether a given
runner's **identity satisfies** the cert is the `[LLM]`/human call rig can't resolve. Parameterize on the actual
`<CachedEntity.access>` and `<Permission/cert>` for the change under review.

**Where the gate actually lives:** the cert assert is usually **two hops below the getter** —
`<Cache>.New(id) → IfCanView → CertificateEntity.AssertRight(…, <Right>)` — not on the method you're reading.
Follow the cache indirection to confirm the gate is real.

**The canonical safeguard shape to verify (MedDBase idiom):** a target-cert grant —
`using (new GrantAccess(<owner>Cache.New(id).FkCertificate, <Right>)) { …read… }`. It self-grants the *target's*
cert, **profile-keyed and thread-scoped**, so the assert is satisfied even for a profile-less/wrong-profile
background or migration runner — which is *exactly* how a cert-gated migration safeguards its own system/queue
path without cert-denial. **Verify the grant and the assert resolve the same `ActiveProfile`** (both inside the
same `using`, same thread); if so the background path is covered, regardless of the runner's real rights.

**But that same property is the catch — a grant is not a check.** `GrantAccess(target.cert, Right)` *satisfies*
the assert; it does **not verify the caller holds the right**. So on USER paths the migration's "permission
safeguard" enforces nothing on its own — its correctness rests **entirely on upstream authorization** being
established before the read (the user is acting on that target because an earlier check let them). When a
safeguard is grant-based, the real review question shifts up: *is every path that reaches the grant already
caller-authorized for that target?* If yes, the grant is a scoped elevation (fine); if any path reaches it
without prior authz, the grant silently hands out the right. rig can enumerate the reaching paths; whether each
is pre-authorized is the `[LLM]` call.

## Don't over-flag
~29 of 200 in one window were **by-design / wontfix**, ~8 were **typos/copy**. By-design behavior and cosmetic
strings exist — don't manufacture defects from intended behavior. A clean rig result is not proof of safety
(see below), but a rig finding is not proof of a bug either — confirm in source before you flag.

## Gate vs guide — where a confirmed finding goes
- **Guide (most findings):** a review comment. The reviewer applies judgment; nothing durable to encode.
- **Gate (the rare CRISP invariant):** when a confirmed finding is a deterministic policy you want enforced
  **forever, without a reviewer in the loop** — e.g. after a migration, "every EP reaching `<CachedEntity>` must
  assert `<Permission>`" — propose a structured `rig assert` rule (NOT prose, NOT a "lossy English" rule: a gate
  must be deterministic). This is the bridge from "reviewer spotted it once" to "CI catches the next EP someone
  adds in 2027." Reserve it for the crisp few; everything else is guide.

## What rig CANNOT ground (disclose, don't claim)
A clean rig result does not clear these — they need value/dataflow rig lacks:
- **Swallowed result before a security effect** (e.g. an `Either`/`Option` error eaten before the guard runs).
- **A model field set in code but never mapped to a DB write** (the write rig "sees" may not carry the field).
Say so when you rely on a rig result near these, so the reviewer doesn't over-trust a green graph.

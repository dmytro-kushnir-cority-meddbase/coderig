All seven verification tasks complete. Here is the full deliverable, followed by the requested rig bug report and new-detector suggestions.

---

# Audit report — MR !10645 (Healthcode settings: object-store → chamber config + entity columns)

**Audited branch:** `health-code-settings-move-to-tables-code` @ `1848f88466690c1c795d0f98ebbfbb42464a6563` ("revert perm scope").
**Index provenance:** `.rig` in `C:\Git\meddbase-analysis`, 134 runs, mined `--from MedDBase.Pages.csproj --identity 1E8E07368F463951`. Pages run `aa9a69b774b4401e…` indexed **2026-06-09 16:27:23Z**, EP=5467 / effects=7620 / methods=34436 — healthy. HEAD commit is 16:05:53 UTC, so the index **postdates** HEAD (verified against the timezone). Sanity edges resolved; transitive-reference recall fix present.

---

## V1 — Write blast radius (R1) — **CONFIRMED** (with stated boundaries)

- `rig reaches` on each setter shows a direct `llblgen write` on the **shared global entity** at d0: `Master.SetCompanySettings → llblgen write CompanyEntity`; `…SetMedicalPersonSettings → MpEntity`; `…SetSiteSettings → SiteEntity`. Source: `Master_HealthcodeServiceImpl.cs:1590/1609/1628` regions — each does `*Cache.NewEntity(id,txn)` → set columns → `entity.Save()`.
- **Webhooks / ePrescribe / audit / OCC are NOT statically visible.** Under the entity `Save()` rig sees only `llblgen`/`throw`/`entity_cache` providers — *no* webhook/eventbus/http/messaging. The 642-method / 310-effect cascade under `CompanyEntity.Save` is a **dispatch over-approximation** (every `*Entity.Save` override), not real fan-out. rig *does* corroborate the cascade re-saves `AccountEntity` and writes `PersonEventEntity` (the diff's "re-saves Account" / audit-ish). **Boundary:** the webhook/ePrescribe firing crosses an Echo `.tell` / `[ClientAction]` / LLBLGen-lifecycle boundary → needs runtime/DB verification, not rig.
- **No owner-chamber guard on the write path:** `rig tree …SetCompanySettings --full` = `Set* → NewEntity → ctor`, `+ EntityBase.Save` — nothing else. Source confirms. The R1 TODO marker (`R1 — owner-chamber write guard (DECISION: add the check)`) is in the diff verbatim → "decided, not built". Confirmed.
- **Clobber on a shared row** is a data-model fact rig can't adjudicate (no FK_OWNER_ACCOUNT scoping visible statically); source + the TODO confirm the columns are global and the write is unguarded.

## V2 — Read-path permission asymmetry — **CONFIRMED**

- `CompanyCache.New(int)` and `MpCache.New(MpId)` route through **`IfCanView`** which calls `CertificateEntity.AssertRight(… Rights.Company.CanViewCompanySummaryRecord …)` (CompanyCache.cs:53-61) and `… Rights.MedicalPerson.CanViewDemographic …` (MpCache.cs:53-61). `SiteCache.New` is a bare `Entity.New<…>(pk)` with **no gate** (SiteCache.cs:25-26). rig matches exactly: Company/Mp `New` reach `IfCanView`; Site reaches only a plain `entity_cache read`.
- `AssertRight` **throws `CertificateEntity.CertificateAccessException`** (rig: `d1 throw raise CertificateEntity.CertificateAccessException`; CertificateEntity.cs:988). It's a hard deny, not a silent filter.
- The gate now sits on the **claim-build path**: concrete `rig path` = `SubmitToHealthcode → BuildClaim → GetClaimSource → GetCompany → CompanyCache.New(int) → IfCanView`. It is *also* reached from UI (`PageService.DoRequest`, `Work.Edit`, several `ConfigurationPane`s, `Company.Edit.Save` reading `GetCompanySettings` at Edit.cs:722). **This is a newly-introduced gate the old in-memory blob read never had.**
- **rig recall gap (noted):** `rig path *Cache.New → AssertRight` returned "No path" — rig missed the terminal `AssertRight(…, Func<R>)` continuation-overload edge. It captured `IfCanView` + the inner `AccountCache` read; source is the decisive evidence.
- **Mitigation found in V7:** the InvoiceDebtChase/Billing special profiles already hold both rights, so a background builder running under such a profile passes the gate (whether it does is an Echo-boundary/runtime question).

## V3 — Clinician-import cross-chamber template leak (R5) — **CONFIRMED** (by source)

- `SaveClinicians.SaveClinician` (SaveClinicians.cs:243) fetches `templateMp = new MpEntity(templateMpId, transaction)` **by id only — no owner check** (line 266), and in the `existingMpId == 0` branch copies `mp.HealthcodeRegistered = templateMp.HealthcodeRegistered` / `…OverridePayee` (lines 285-286), then `mp.Save()` (289). Replaces the old service-mediated (chamber-scoped blob) copy. The R5 TODO ("cross-chamber template divergence (DECISION NEEDED)") is present.
- **rig recall gap (noted):** `rig reaches SaveClinician` surfaced only 2 throws — it **missed** both the `templateMp` ctor-fetch and the `mp.Save()` write here, although it captured the identical pattern in the Master setters (V1). The worker's invoker is the TableFlow reflection/registry dispatch (shows as its own root → boundary). Source is ground truth.

## V4 — Inert site grant + `Rights.Site.*` — **CONFIRMED (stronger than claimed)**

- `rig refs Rights.Site.CanViewSite` = **0**. Repo-wide grep: `CanViewSite` (singular) appears **exactly once — its own declaration** (Rights.cs:264); **zero** `Rights.Site.`-qualified usages anywhere. All real site-view enforcement uses a *different* flag, `Rights.Company.CanViewSites` (plural, `1L<<42`). No `InstallCertificateRight` wires `Rights.Site.CanViewSite`.
- Combined with V2 (`SiteCache.New` ungated), the right is enforced nowhere → any grant is inert/stillborn. The brief's "only a TestBed reflection references it" is **not even present as a literal**; the only conceivable consumer is enum-reflection (a boundary). Sibling members `CanModifySite/CanRemoveSite/CanAddSession` are likewise unreferenced (rig).

## V5 — Migration + `Settings.Save` extra emissions — **CONFIRMED** (Settings.Save) / **N-A for the SQL migration**

- There is **no C# `MigrateHealthcodeChamberSettingsFromObjectStoreToConfig`** — the migration is SQL-only (`GI4327`, `GI4328`); rig/grep can't adjudicate SQL. The brief's method name is inaccurate.
- Runtime persist path is real: `ConfigurationPane.Save → PersistHealthcodeSettings (Master:214) → Settings.Save (Settings.cs:854)`. **`Settings.Save` fires both** `Chamber.Async.OnChamberConfigurationChanged(…)` (line 860) **and** `Process.tell(ProcessDns.App.Local, new ChamberChanged(…))` (line 861, Echo broadcast). rig saw the `ChamberChanged.#ctor` edge in the tree; **boundary:** it could not follow the `Process.tell` publish or the `.Async` event. The old blob write (object_store) didn't route through `Settings.Save`, so both emissions are new on this path.

## V6 — Deploy-order: no object-store read fallback — **CONFIRMED** (by source; rig partial)

- `HealthcodeSettings.cs` **deletes** the blob-backed `Companies`/`MedicalPeople`/`Sites` dicts and their accessors — no blob dict remains to fall back to. Master `Get*` read `*Cache.New` (entity columns) exclusively; config creds hydrate from `Settings.*`. `rig reaches` on all six Get paths shows only `entity_cache`/`llblgen`/`throw` effects — **no object_store**. The lone object-store ref in the runtime file is `Chamber.ObjectStore.GetQueryWithDTO<HealthcodeQueueDTO>` (line 258) — the **claim queue**, not settings.
- **rig caveat (important):** the ruleset has object_store **write/delete only — no read detector** (rules lines 506-523; 16 write/delete instances fire elsewhere, confirmed via `rig derive`). So rig's "no object_store" on read paths is meaningful for writes but **vacuous for reads**; the deleted blob dicts in source are the decisive evidence. → #4328 backfill must complete before #4327 binding serves traffic; the ordering risk is **real**.

## V7 — Unflagged blast radius / new risks

- **N1 (amplifies R1):** the setters are reached from **mainstream record editors** — `Company.Edit.Save` (insurer/"N" companies, Edit.cs:736), `Doctor.Personal.EditLive.Save`/`SavePolicyConfirm`, `Admin.Sites.EditSite.Save` — not just the InvoiceDebtChase config the brief framed R1 around. The V2 read-gate also fires on these UI saves (`Company.Edit` reads `GetCompanySettings` at Edit.cs:722).
- **N2 (transactional):** `Company.Edit.cs:736` calls `SetCompanySettings` with the **2-arg overload (no transaction)**, so the entity `Save()` runs *outside* the page's own transaction (`ent.Save()` at 706 used the page txn) → partial-commit / inconsistency window between the company save and its Healthcode columns.
- **N3 (config coupling / security):** `Settings_Healthcode.cs` stores the Healthcode **password in plaintext** (its own TODO) and relies on config keys that **`GI4327` hard-codes to match the property names** — a rename silently degrades reads to defaults with no failure. Flag for a test or a key-constant shared between SQL and C#.
- **N4 (minimal-diff hygiene):** incidental whitespace/`/// ` doc churn in `IHealthcodeService.cs` and an **entirely-whitespace** `SpecialProfiles.cs` diff (the "revert perm scope" commit) bloat the review surface — split into a cosmetics MR.

### Claims rig cannot adjudicate (need runtime/DB)
Webhook/ePrescribe/audit firing from `entity.Save()` (Echo/`[ClientAction]` boundary, V1); the `Process.tell`/`Async` chamber broadcasts (V5); whether the background claim builder runs under a rights-bearing profile (V2); the SQL migrations GI4327/GI4328 and the global-vs-per-owner shape of the COMPANY/SITE rows (V1 clobber); reflection-based enum consumers of `Rights.Site.*` (V4).

---

# rig bug report — where it helped, where it was noise

## Where rig genuinely helped (signal)
1. **Effect typing at d0 on the setters (V1).** `rig reaches` instantly classified each `Set*` as an `llblgen write` on the *named* shared entity. Strong, correct, ground-truthable.
2. **Reverse reachability found the real entry points (V1/V7).** `callers --roots` on the **interface** method surfaced `Company.Edit.Save` / `Doctor.Personal.EditLive` / `Admin.Sites.EditSite` — the single highest-value finding, and one I would *not* have guessed from the brief. Interface→impl dispatch worked.
3. **Concrete path across 6 hops (V2).** `rig path "Master.SubmitToHealthcode" "IfCanView"` produced the exact `SubmitToHealthcode→BuildClaim→GetClaimSource→GetCompany→CompanyCache.New→IfCanView` chain with file:line per hop. Excellent.
4. **The asymmetry (V2).** Company/Mp `New` reach `IfCanView`; Site doesn't — visible directly in `reaches`, before I opened a file.
5. **Negative results that held up (V4).** `rig refs` = 0 + `derive` proving the object_store detector fires elsewhere (16×) let me trust some absences.
6. **`ChamberChanged.#ctor` edge (V5).** Even though the `.tell` was uncrossable, the ctor edge in the tree pointed me straight to Settings.cs:860-861.

## Where rig was noise or misleading
1. **Over-approximated `Save()` cascade (V1) — the biggest noise.** `rig reaches "CompanyEntity.Save"` = 642 methods / 310 effects / **172 llblgen writes** spanning AbsenceReason, BankHoliday, AppointmentType… This is virtual-dispatch fan-out to *every* `*Entity.Save` override, not real behavior. A reviewer trusting the raw count would massively overstate the blast radius. **Repro:**
   ```
   cd C:\Git\meddbase-analysis
   rig reaches "CompanyEntity.Save"        # 310 effects, ~172 writes — almost all spurious
   rig reaches "SiteEntity.Save"           # identical 310/172 — tell-tale that it's dispatch fan-out, not real
   ```
   Fix idea: cap/flag base-virtual `Save()` dispatch fan-out, or mark methods whose override set exceeds N as "dispatch-saturated" in the output.
2. **Missed the `AssertRight` continuation-overload edge (V2) — false negative.** `rig path "CompanyCache.New(System.Int32)" "CertificateEntity.AssertRight"` → **"No path"**, despite CompanyCache.cs:56 calling it directly. The call passes a `Func<R>` continuation and `AccountCache.New(…).FkCertificate` (an `Option<Guid>`); one of those args likely bound to an error type so the overload didn't resolve. **Repro:**
   ```
   rig path "CompanyCache.New(System.Int32)" "CertificateEntity.AssertRight"   # No path (WRONG)
   rig path "CompanyCache.New(System.Int32)" "IfCanView"                       # resolves fine
   ```
3. **Missed ctor-fetch + `Save()` in `SaveClinician` (V3) — false negative, and inconsistent.** `rig reaches "SaveClinicians.SaveClinician"` showed only 2 throws — no `MpEntity` fetch, no write — yet the *same* `new XxxEntity(pk,txn)` + `.Save()` pattern was detected fine inside the Master setters. **Repro:**
   ```
   rig reaches "SaveClinicians.SaveClinician"        # 8 methods, 2 throws — misses templateMp fetch & mp.Save()
   rig reaches "Master.SetMedicalPersonSettings"     # same pattern, fetch+write captured correctly
   ```
   The divergence (works in one method, not the other) is the actionable bug.
4. **No object_store *read* detector (V6) — silent blind spot that reads as a clean result.** The ruleset only has object_store write/delete, so "no object_store effect on Get*" looks like confirmation of "no read fallback" but is vacuous. A user could draw a false conclusion. **Repro:**
   ```
   rig derive --format tsv | grep object_store | grep -c read   # 0 — no read rule exists at all
   ```
5. **Weak built-in sanity edge.** `rig callers "Master.GetInvoiceSettings"` (the brief's suggested health check) returned just the symbol itself with no callers — uninformative as a "transitive-fix working" probe. A better canned sanity edge would be one with a known cross-project caller.

Net: rig was a **fast, correct lead-generator for forward effects, reverse entry points, and concrete paths**, but its **counts under virtual `Save()` are not trustworthy**, it has **recall gaps on overload-heavy / continuation-style calls and on some ctor-fetch+Save sites**, and **absence of an effect ≠ absence in code** when the relevant detector (object_store read) doesn't exist. Every rig claim in this audit was cross-checked against source, which is exactly the methodology the skill prescribes.

---

# Suggested new detectors (from the code seen)

Ordered by value for migrations like this one:

1. **`entity_save_hooks` — model the `*Entity.Save()` override as a typed effect, not raw dispatch fan-out.** Replace the 172-write over-approximation with a curated effect that, for a given entity, emits the *real* lifecycle consequences (e.g. `webhook`, `audit:PersonEvent`, `account_resave`, `occ_bump`). This is the single biggest accuracy win and directly serves migration blast-radius audits. Without it, `reaches` on any `Save()` is unusable.

2. **`webhook` / `notify` provider.** There is clearly a webhook/ePrescribe firing pathway off entity saves that's currently invisible. A detector keyed on the actual emit API (`OnCompanyChanged`, `OnSiteModified`, `OnSystemAccountChanged`, ePrescribe publishers) would let `reaches` answer "does this write notify externally?" — the core R1 question.

3. **`object_store read` operation.** Close the V6 blind spot: add a read op for `IObjectStore.GetQuery*/GetObject*/GetQueryWithDTO`. Then "no object-store read on Get*" becomes a *real* assertion, and queue-vs-settings reads are distinguishable.

4. **`permission_assert` / `rights_gate` provider.** Detect `CertificateEntity.AssertRight/AssertAnyRight/AssertAccountRight/HasRight` and `*Cache.IfCanView` as a first-class effect carrying the `Rights.*` flag. This would have made V2 a one-command answer ("which read paths assert which right") and would catch the Company/Mp-vs-Site asymmetry automatically. Pair it with the `throw` it raises (`CertificateAccessException`).

5. **`echo_publish` provider (message-passing seam marker).** Detect `Process.tell/ask(ProcessDns.*, new XxxMsg(...))` and `*.Async.On*(...)`. rig can't *cross* the actor boundary, but tagging the **publish site** (with the message type) turns an invisible edge into a visible "fire-and-forget broadcast here" effect — exactly what V5 needed.

6. **`config_setting` read/write provider.** Detect the `Settings.Get<T>/Set<T>` compiler-magic accessors (`[CallerMemberName]` keyed). Useful both to trace config dependencies and — combined with the GI4327 hard-coded keys — to flag the property-name↔config-key coupling risk (N3).

7. **`ctor_fetch` recall fix (rule tightening, not new family).** `new MpEntity(id, txn)` was detected via `MpCache.NewEntity` but **not** at the direct site in `SaveClinician`. Broaden the `matchConstructor`/`minArguments` rule (or fix the binding) so direct `new *Entity(pk[,txn])` fetches are caught everywhere, closing the V3 gap.

====


distinct from the detector ideas (effect coverage) and the bug fixes (recall/accuracy), these are tool capability/UX requests, each tied to friction I actually hit this session:                                           
                                                                                                                                                                                                                                    
  1. rig diff <ref> / branch-aware indexing. The single biggest workflow gap: I had to manually reconcile the index timestamp against the HEAD commit's timezone to trust the index reflected the MR branch. A rig diff origin/main 
  that (a) maps changed methods to runs and (b) warns "index SHA ≠ working SHA, re-mine" would remove a whole class of stale-index errors. Bonus: rig reaches --changed to enumerate effects for only the methods the diff touches —
  that's exactly the V7 task.                                                                                                                                                                                                       
  2. A "blast radius" command that fuses forward+reverse. For every audit V-item I ran reaches then callers --roots then path separately and stitched them mentally. A rig impact "Master.SetCompanySettings" returning             
  {entry-points reaching it, effects it triggers, shared resources written} in one shot is the actual question reviewers ask.                                                                                                       
  3. Confidence/provenance flags on edges. rig silently gave a false "No path" (V2 AssertRight) and silently over-counted (V1 Save fan-out). If output annotated edges as resolved | dispatch-fanout(N overrides) |                 
  error-type-recovered | unresolved-overload, I'd know which results to trust without opening source every time. The !: error-type recovery already exists internally — surface it.                                                 
  4. Boundary markers in tree/reaches output. When a trace dead-ends at Process.tell, [ClientAction], Activator.CreateInstance, or an interface with no in-scope impl, print a ⊘ boundary: echo .tell (effects beyond invisible)    
  leaf instead of just stopping. Half my "rig cannot adjudicate" list is exactly these seams — the tool knows where they are; say so explicitly.                                                                                    
  5. --format json everywhere + stable DocIDs. I leaned on --format tsv + grep. Machine-readable JSON for reaches/callers/path would let an agent (or a CI gate) consume results without text-scraping, and make "fail the build if 
  a new entry point reaches llblgen write CompanyEntity" trivial.                                                                                                                                                                   
  6. rig assert / policy gate. Codify a claim as a checkable rule, e.g. rig assert no-path "PageService.DoRequest" "object_store write". Turns a one-off audit into a regression guard — the natural home for "the owner-chamber    
  guard, once added, must sit on every Set* path."                                                                                                                                                                                  
  7. Effect grouping / dedup in reaches. The 172-writes wall of AbsenceReasonEntity.Save, AppointmentTypeEntity.Save… should collapse to llblgen write ×172 via EntityBase.Save dispatch [expand]. Rollup-by-cause would have made  
  V1 readable at a glance instead of looking like a real 172-table write.                                                                                                                                                           
  8. Quote-the-source mode (--source). path/reaches give file:line; an opt-in that inlines the 1-2 relevant source lines per hop would cut the constant tool→Read round-trips (I opened ~8 files purely to confirm edges rig had    
  already located).                                                                                                                                                                                                                 
  9. Index health as an exit code. rig runs --check returning non-zero when any in-scope run shows the base-type-chain flake (EP≈0/effects≈0 with healthy symbols) would let a pre-audit script catch a bad mine before I trust it. 
                                                                                                                                                                                                                                    
  If I had to pick two to build first: #1 (diff/branch awareness) because it prevents silently auditing the wrong code, and #3 (edge confidence flags) because it directly addresses the false-negative/over-count problems that    
  forced me to ground-truth literally every rig claim. Want me to open these as issues in the coderig repo, or draft the rig impact and rig assert command specs?
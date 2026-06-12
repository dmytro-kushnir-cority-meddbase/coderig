# rig — issue backlog from the MR-!10645 audit review

Distilled from `docs/todo.md` (a fresh-Opus audit of MedDBase MR !10645 that drove rig end-to-end and
ground-truthed every claim against source). Each item has a repro and a root-cause note. Priorities:
**P1** = correctness/trust (act first), **P2** = cheap coverage win, **P3** = feature.

Convert to GitHub issues with `gh issue create` once `gh` is installed/authed (remote: `dv00d00/coderig`).

---

## A. Correctness bugs (P1 — every one forced manual source verification)

### A1 — `Save()` (base-virtual) dispatch fan-out over-approximates blast radius
- **Symptom:** `rig reaches "CompanyEntity.Save"` = 642 methods / 310 effects / ~172 `llblgen write` — and `rig reaches "SiteEntity.Save"` returns the **identical** 310/172. The count is base→all-overrides dispatch fan-out (every `*Entity.Save`), not real per-entity behavior. A reviewer trusting the raw count massively overstates impact.
- **Repro:**
  ```
  cd C:\Git\meddbase-analysis
  rig reaches "CompanyEntity.Save"     # ~310 effects, ~172 writes
  rig reaches "SiteEntity.Save"        # identical → dispatch fan-out, not real
  ```
- **Root cause:** `FactPathFinder` base-virtual/abstract→override dispatch (G6/G3) fires for *any* call whose target resolves to a base method that has overrides — including `base.Save()` / base-typed `Save()` — so it traverses into all ~172 `EntityBase.Save` overrides. The explosion path is `CompanyEntity.Save → base.Save (EntityBase.Save) → [override-dispatch] → all *Entity.Save`.
- **Fix options:** (a) cheap — roll up siblings in `reaches`/`tree` output to `llblgen write ×172 via EntityBase.Save dispatch [expand]`, and tag the edge `dispatch-fanout(N)` (see D3/D7); (b) principled — suppress override-dispatch for non-virtual `base.` calls (needs an `is-base-call` reference fact → re-extraction) and/or narrow dispatch by the call site's `ReceiverType` (already stored) so `companyEntity.Save()` reaches only `CompanyEntity`-assignable overrides.

### A2 — ctor-fetch + `Save()` recall is inconsistent across methods
- **Symptom:** `rig reaches "SaveClinicians.SaveClinician"` shows only 2 throws — it **misses** the `new MpEntity(templateMpId, txn)` fetch (SaveClinicians.cs:266) and the `mp.Save()` write (:289), even though the **same** `new XxxEntity(pk,txn)`+`.Save()` pattern is captured correctly inside `Master.SetMedicalPersonSettings`.
- **Repro:**
  ```
  rig reaches "SaveClinicians.SaveClinician"      # 8 methods, 2 throws — fetch+write missing
  rig reaches "Master.SetMedicalPersonSettings"   # same pattern — fetch+write captured
  ```
- **Root cause (hypothesis):** the divergence (works in one method, not another) points to a per-file/per-project binding or enclosing-symbol gap in `SaveClinicians` (MedDBase.ServiceTier) — the ctor/invocation refs either bound to error types or were attributed to the wrong enclosing symbol. **Investigate with a fixture that reproduces a captured-vs-missing pair.**
- **Priority:** P1 — recall gaps on writes silently understate migration blast radius.

### A3 — overload/continuation call edge missed (`AssertRight`)
- **Symptom:** `rig path "CompanyCache.New(System.Int32)" "CertificateEntity.AssertRight"` → **"No path"**, despite CompanyCache.cs:56 calling it directly; `rig path …New → IfCanView` resolves fine.
- **Root cause:** the call passes a `Func<R>` continuation + `AccountCache.New(...).FkCertificate` (`Option<Guid>`); one arg bound to an error type so `GetSymbolInfo().Symbol` was null and no invocation ref was recorded. General recall class (error-type binding); partial mitigation at best.
- **Priority:** P1 (low confidence of full fix) — at minimum, surface it via the `error-type-recovered`/`unresolved` edge tag (D3) so absence is visible.

---

## B. Rule-data gaps (P2 — cheap, high value)

### B1 — no `object_store` **read** detector
- **Symptom:** the ruleset has `object_store` write/delete only. So "no object_store effect on the `Get*` paths" (V6) reads as confirmation of "no read fallback" but is **vacuous** — there's no read rule to fire.
- **Repro:** `rig derive --format tsv | grep object_store | grep -c read` → `0`.
- **Fix:** add an `object_store` `read` rule to `meddbase-analysis/rig.rules.json` keyed on `IObjectStore.GetQuery*/GetObject*/GetQueryWithDTO`. Then queue-vs-settings reads are distinguishable and the V6 assertion becomes real.

---

## C. Detector families (P3 — ordered by audit value)

1. **`entity_save_hooks`** — model `*Entity.Save()` as a typed effect (real lifecycle consequences: `webhook`, `audit:PersonEvent`, `account_resave`, `occ_bump`) instead of raw dispatch fan-out. Biggest accuracy win; pairs with A1.
2. **`webhook` / `notify` provider** — keyed on the real emit API (`OnCompanyChanged`, `OnSiteModified`, `OnSystemAccountChanged`, ePrescribe publishers). Answers the core R1 question "does this write notify externally?".
3. **`object_store read` op** — see B1.
4. **`permission_assert` / `rights_gate` provider** — detect `CertificateEntity.AssertRight/AssertAnyRight/AssertAccountRight/HasRight` and `*Cache.IfCanView`, carrying the `Rights.*` flag + the `CertificateAccessException` it raises. Would make V2 a one-command answer.
5. **`echo_publish` provider (seam marker)** — detect `Process.tell/ask(ProcessDns.*, new XxxMsg(...))` and `*.Async.On*(...)`. Can't cross the actor boundary but tags the publish site with the message type.
6. **`config_setting` read/write** — detect `Settings.Get<T>/Set<T>` (`[CallerMemberName]`-keyed). Traces config deps + flags the GI4327 hard-coded-key↔property-name coupling (N3).
7. **`ctor_fetch` rule tightening** — broaden `matchConstructor`/`minArguments` (or fix binding) so direct `new *Entity(pk[,txn])` fetches are caught everywhere (closes A2 at the rule level).

---

## D. Tool capability / UX (P3 — friction hit this session; reviewer's top-2 = D1, D3)

1. **`rig diff <ref>` / branch-aware indexing** — biggest workflow gap: had to reconcile index timestamp vs HEAD commit timezone to trust the index matched the MR branch. Want: map changed methods→runs, warn "index SHA ≠ working SHA, re-mine", and `rig reaches --changed` (effects for only diff-touched methods = the V7 task).
2. **`rig impact <method>`** — fuse forward+reverse: return `{entry points reaching it, effects it triggers, shared resources written}` in one shot (the actual reviewer question; today = `reaches` + `callers --roots` + `path` stitched mentally).
3. **Edge confidence/provenance flags** — annotate edges `resolved | dispatch-fanout(N) | error-type-recovered | unresolved-overload`. Directly defuses A1 (over-count) and A2/A3 (silent false-negatives). The `!:` recovery already exists internally — surface it.
4. **Boundary markers in `tree`/`reaches`** — when a trace dead-ends at `Process.tell` / `[ClientAction]` / `Activator.CreateInstance` / an interface with no in-scope impl, print `⊘ boundary: echo .tell (effects beyond invisible)` instead of silently stopping. Half the "rig cannot adjudicate" list is exactly these seams.
5. **`--format json` everywhere + stable DocIDs** — for `reaches`/`callers`/`path`, so an agent/CI gate consumes results without text-scraping.
6. **`rig assert` / policy gate** — codify a claim as a check, e.g. `rig assert no-path "PageService.DoRequest" "object_store write"`. Turns a one-off audit into a regression guard (natural home for "the owner-chamber guard must sit on every `Set*` path").
7. **Effect grouping / dedup in `reaches`** — collapse the 172-write wall to `llblgen write ×172 via EntityBase.Save dispatch [expand]` (rollup-by-cause; pairs with A1/D3).
8. **Quote-the-source mode (`--source`)** — inline the 1–2 relevant source lines per hop in `path`/`reaches` (cut ~8 tool→Read round-trips this session).
9. **Index health as an exit code** — `rig runs --check` returns non-zero when any in-scope run shows the base-type-chain flake (EP≈0/effects≈0 with healthy symbols), so a pre-audit script catches a bad mine.

---

## Suggested first slice
- **A1 + D3 + D7 together** (dispatch-fanout tagging + rollup) — one coherent change that turns the single biggest noise source into a readable, trustworthy result.
- **B1** — trivial, closes the V6 blind spot.
- **A2** — investigate the recall divergence (fixture-first); likely also informs A3.

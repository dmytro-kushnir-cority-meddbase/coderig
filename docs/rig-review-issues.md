# rig — issue backlog from the MR-!10645 audit review

Distilled from a fresh-Opus audit of MedDBase MR !10645 that drove rig end-to-end and ground-truthed
every claim against source (the source `docs/todo.md` was removed once its findings were actioned).
Priorities: **P2** = cheap coverage win, **P3** = feature.

Convert to GitHub issues with `gh issue create` once `gh` is installed/authed (remote: `dv00d00/coderig`).

> **All P1 correctness bugs from the original audit (A1 `Save()` dispatch fan-out, A2 ctor-fetch+`Save()`
> recall, A3 overload/continuation edge) and the P2 `object_store` read gap (B1) were verified
> NON-REPRODUCIBLE on 2026-06-14 against the current build + store and removed.** Repro evidence:
> `CompanyEntity.Save` vs `SiteEntity.Save` now diverge (1454/73/7 vs 1196/27/5 — fan-out gone);
> `SaveClinicians.SaveClinician` now surfaces the fetch+write (6 write / 4 fetch); the `AssertRight`
> path resolves; `object_store read` fires 147×. The `ctor_fetch` rule-tightening idea (closed A2) is
> likewise moot. Only P3 feature work remains below.

---

## C. Detector families (P3 — ordered by audit value)

1. **`entity_save_hooks`** — model `*Entity.Save()` as a typed effect (real lifecycle consequences: `webhook`, `audit:PersonEvent`, `account_resave`, `occ_bump`) instead of relying on dispatch reach alone. Accuracy win for migration blast-radius audits.
2. **`webhook` / `notify` provider** — keyed on the real emit API (`OnCompanyChanged`, `OnSiteModified`, `OnSystemAccountChanged`, ePrescribe publishers). Answers the core R1 question "does this write notify externally?".
3. **`permission_assert` / `rights_gate` provider** — detect `CertificateEntity.AssertRight/AssertAnyRight/AssertAccountRight/HasRight` and `*Cache.IfCanView`, carrying the `Rights.*` flag + the `CertificateAccessException` it raises. Would make V2 a one-command answer.
4. **`echo_publish` provider (seam marker)** — detect `Process.tell/ask(ProcessDns.*, new XxxMsg(...))` and `*.Async.On*(...)`. Can't cross the actor boundary but tags the publish site with the message type.
5. **`config_setting` read/write** — detect `Settings.Get<T>/Set<T>` (`[CallerMemberName]`-keyed). Traces config deps + flags the GI4327 hard-coded-key↔property-name coupling (N3).

---

## D. Tool capability / UX (P3 — friction hit this session; reviewer's top-2 = D1, D2)

1. **`rig diff <ref>` / branch-aware indexing** — biggest workflow gap: had to reconcile index timestamp vs HEAD commit timezone to trust the index matched the MR branch. Want: map changed methods→runs, warn "index SHA ≠ working SHA, re-mine", and `rig reaches --changed` (effects for only diff-touched methods = the V7 task).
2. **`rig impact <method>`** — fuse forward+reverse: return `{entry points reaching it, effects it triggers, shared resources written}` in one shot (the actual reviewer question; today = `reaches` + `callers --roots` + `path` stitched mentally).
3. **Edge confidence/provenance flags** — annotate edges `resolved | dispatch-fanout(N) | error-type-recovered | unresolved-overload`. (The `Save()` over-count that motivated this is fixed, but explicit tagging still guards against silent mis-trust.) The `!:` recovery already exists internally — surface it.
4. **Boundary markers in `tree`/`reaches`** — when a trace dead-ends at `Process.tell` / `[ClientAction]` / `Activator.CreateInstance` / an interface with no in-scope impl, print `⊘ boundary: echo .tell (effects beyond invisible)` instead of silently stopping. Half the "rig cannot adjudicate" list is exactly these seams.
5. **`--format json` everywhere + stable DocIDs** — for `reaches`/`callers`/`path`, so an agent/CI gate consumes results without text-scraping.
6. **`rig assert` / policy gate** — codify a claim as a check, e.g. `rig assert no-path "PageService.DoRequest" "object_store write"`. Turns a one-off audit into a regression guard (natural home for "the owner-chamber guard must sit on every `Set*` path").
7. **Effect grouping / dedup in `reaches`** — collapse sibling-override walls (`AbsenceReasonEntity.Save`, `AppointmentTypeEntity.Save`, …) to `llblgen write ×N via EntityBase.Save dispatch [expand]` (rollup-by-cause).
8. **Quote-the-source mode (`--source`)** — inline the 1–2 relevant source lines per hop in `path`/`reaches` (cut ~8 tool→Read round-trips this session).
9. **Index health as an exit code** — `rig runs --check` returns non-zero when any in-scope run shows the base-type-chain flake (EP≈0/effects≈0 with healthy symbols), so a pre-audit script catches a bad mine.

---

## Suggested first slice
- **D1** (diff/branch awareness) — prevents silently auditing the wrong code.
- Then the P3 detector families by audit value (C1 `entity_save_hooks`, C3 `permission_assert`).

# Recall audit — `DetailsLive.MedicareVerification`

## Entry point
`M:MedDBase.Pages.Patient.DetailsLive.MedicareVerification` — `MedDBase.Pages/Patient/DetailsLive.cs:3204`. A launcher that opens `MedicareVerificationDialog`; real work (Flurl verification calls, DB writes) is in the dialog's `Verify`/`Save` actions.

## rig reported
From the entry point: 5 direct effects (3 throw, 1 clientpage_proxy:show, 1 clientpage_nav:load_child) + 102 dispatch fan-out. `--async`: identical (does not cross the proxy). From dialog actions queried directly: `Verify` = 3 throw, **0 HTTP despite 3 Flurl calls**; `Save` = llblgen:write PersonEntity + fetch + tx_commit/rollback + 3 throw (clean).

## Confirmed misses
**1+2. Flurl REST calls invisible — missing effect rule (codebase-wide).**
- `MedicareVerification.cs:42-78`: 3 `PostJsonAsync().ReceiveJson<T>()` to Medicare DHS (Patient/Concession/Veteran); `AccessRequests.cs:20-27`: `PostStringAsync()` PRODA OAuth token. All on `Flurl.Http.GeneratedExtensions`.
- Repro: `rig reaches "MedicareVerification.VerifyPatient"` → 2 effects (throw only); rig traverses TO the Flurl call site but emits nothing.
- Why: no Flurl rule in builtin/ruleset. Sync-over-async (`.GetAwaiter().GetResult()`), so NOT an async-state-machine issue — pure missing rule. Codebase-wide blind spot for all Medicare AU REST (and Flurl-backed SignatureRx/Opayo/Xero/HAProxy).
- FIXED: Flurl GET/POST/mutate rules added to builtin-rules.json with `resource: "declaring_type"` (the fluent URL/receiver isn't statically minable, so `http_argument`/`receiver_type` dropped the effect). Verified `VerifyPatient` now reports 2 `http POST`.

**3. `clientpage_proxy:show` boundary (expected, not a bug).** The registered EP is just a launcher; substantive effects live in `MedicareVerificationDialog.Verify`/`Save` (a separate `ClientPage` dispatched independently by the web framework). Auditing the launcher in isolation undercounts.

## Boundaries (expected)
Flurl over-the-wire; `clientpage_proxy:show` → dialog actions (static analysis can't traverse the proxy→dialog mapping); EventGrid audit via `IAuditLog.Log` (correctly in fan-out); PRODA JWT signing (in-process crypto).

## Verdict
Was: Flurl HTTP a codebase-wide blind spot (now fixed by rule). Structural: the launcher EP undercounts — treat `MedicareVerificationDialog.Verify`/`Save` as the true effect surface.

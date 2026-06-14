# Reverse EP-discovery audit — `WorkflowControllerBase.Save`

## Hot method
`M:MedDBase.Application.Core.Workflow.WorkflowControllerBase.Save` — `main/MedDBase.Application.Core.Workflow/WorkflowControllerBase.cs:465`. Base persistence for ALL workflow controllers; reached via base→override virtual dispatch.

## rig reported
| Query | Count |
|---|---|
| `callers` | 798 methods |
| `callers --roots` | 156 |
| `callers --entrypoints` | 68 (action 63, background 4, echoactor 1) |
| `callers --async` | 798 (identical — `Save()` is sync) |

## Real entry points reaching it
`--entrypoints` correct: 63 page actions across workflow UIs, 4 `Master.RegisterEvents` (background), 1 `ImportJobs.Inbox` (echoactor) — all verified.

Missed by `--entrypoints` (false negatives, verified):
- `InvoiceDebtChase.Master.CheckForZeroDebt`, `Master.ProcessHealthcodeQueue`, `PatientContact.Master.DoDueActions`, `ReferralSLAService.Worker` — actual scheduled `BackgroundProcessSchedule`/`RepeatingBackgroundProcessSchedule` delegate TARGETS.

## EP-detection gaps
1. **Background delegate-target gap (significant)**: the `background` rule tags the *wiring* method (`Master.RegisterEvents`, where the `new BackgroundProcessSchedule(..., Callback, ...)` is constructed) rather than the *delegate target* that runs on the background thread. The 4 callbacks above reach `Save()` and are real recurring origins but are untagged. `--roots` surfaces them (they have no static caller).
2. **Interface-method stubs as spurious roots** (`IWorkflowController.*`, `IReferral*Controller.*`, 19) — reverse dispatch hits the interface declaration; correctly skipped by `--entrypoints`, noise in `--roots`.
3. **3 `P:` property-getter false-positive roots** — `ReferralControllerBase.Collaboration/Outcome/Referral` call `RefreshReferralFromService(save: false)`; the `save:false` constant guard isn't propagated, so rig conservatively adds the getter (never reaches `Save()` at runtime).
4. **`ArgsRequestHandler.HandleRequest`** infra root — no EP rule for the MMS request pipeline; the real EPs (page actions) are tagged deeper.

## Verdict
**Base-virtual reverse dispatch is PRECISE** — no fan-out leak into unrelated controllers (the reverse mirror of the fixed forward `Save()` fan-out bug is clean). Top gap: the **background detector tags wiring methods, not delegate targets** — an `--entrypoints`-only blast-radius audit silently omits the 4 scheduled callbacks. Use `--roots` + manual `BackgroundProcessSchedule` delegate-target inspection to bridge.

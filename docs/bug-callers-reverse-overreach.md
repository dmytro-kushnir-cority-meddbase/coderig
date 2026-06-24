# BUG: `callers` (reverse) reports entry points with NO forward path — reverse/forward asymmetry

> **STATUS (2026-06-24): MITIGATED via fix #2 (forward-verification partition).** `callers --entrypoints`
> now forward-verifies each emitted EP against the same loaded graph (`FactPathFinder.SeedsReachTarget`,
> reusing the `rig impact` per-seed forward-reach engine): EPs whose handler forward-reaches a matched target
> node are listed as the precise answer (headline count), the rest under a `Reverse-only (no forward path
> found — confirm with rig path)` caveat (NOT dropped — recall-safe against the documented forward-misses-
> interface/lambda asymmetry). TSV gains an additive `forwardConfirmed` column. Validated on store `caa9373f`:
> the two documented sync FPs (`Work/Edit.HandleUpdateWarningActioned`, `Work/ManageRecall.HandleRecall…`)
> move to Reverse-only; `rig path` confirms no forward path, while confirmed EPs do have one. Test:
> `tests/Rig.Tests/Domain/CallersForwardVerificationTests.cs`.
> **The ROOT CAUSE (fix #1 — symmetric path-precise reverse narrowing) is STILL OPEN** — it's the
> set-based-reverse-BFS limitation REFERENCE already discloses; left as the deeper follow-up.

**Status:** mitigated (fix #2 shipped; fix #1 root-cause open) · **Severity:** medium (false positives in `callers --entrypoints`; safe-direction over-approximation but violates the callers↔path/tree invariant and inflates "who reaches X" / blast-radius answers) · **Found:** 2026-06-23 (MedDBase healthcode MR auth-reachability audit) · **Distinct from** `bug-callers-path-overreach.md` (that is the OPPOSITE direction — `path`/`callers` over-reaching vs `reaches`/`tree` via `nameof`/event edges; this is `callers` over-reaching vs forward `path` AND `tree`).

## Symptom
`rig callers <target> --entrypoints` lists entry points that forward `rig path`/`rig tree` from those same EPs cannot reach — in **either** traversal mode (it is NOT async/handoff-specific). The reverse closure contains predecessor relationships with no forward counterpart, so the documented invariant "`callers` walks the SAME shaped graph as `path`/`reaches`/`tree`" is violated.

### Concrete repro (store `caa9373f`, MedDBase; binary `0.1.1-ci…+dea678da`)
Target: `MedDBase.Application.Workflows.InvoiceDebtChase.Master.GetCompany`.

```
rig callers "…Master.GetCompany" --entrypoints
  → lists  Work/Edit.HandleUpdateWarningActioned         (Work/Edit.cs:265)
           Work/ManageRecall.HandleRecallCompletedMessage (Work/ManageRecall.cs:101)
  # SYNC — no --async. So this is not a handoff/delivery artifact.

rig path "MedDBase.Pages.Work.Edit.HandleUpdateWarningActioned" "…Master.GetCompany" --async
  → No path.
rig tree "MedDBase.Pages.Work.Edit.HandleUpdateWarningActioned" --async --view full --maxdepth 40 | grep -ci 'Healthcode|InvoiceDebtChase|IHealthcodeService'
  → 0      # of 2659 forward-reachable methods, NONE touch the healthcode service at all
```

So `callers` claims `Work/Edit.HandleUpdateWarningActioned` reaches `GetCompany`, but its entire forward closure (2659 methods) never touches Healthcode/InvoiceDebtChase. The reverse reach is wholly spurious. A control EP that genuinely reaches it (`Workflows/Configure.NewMaster`) resolves correctly forward, so the harness is fine — the defect is specific to the reverse-only edge(s).

A second, related manifestation appears only under `--async`: `Company/DetailsLive.MakeChamber`, `Diagnostics/Chambers.Load`, `TestBed.MigrateChamberSettingsFromConfigurationToCompanyRow` are admitted via the reverse closure of `InvoiceDebtChase.Master.RegisterEvents` (`rig callers "…Master.RegisterEvents" --roots --async` lists them) — i.e. reverse traversal up through the `WorkflowMasterBase.RegisterEvents` virtual node and the `IWorkflows.Register` interface call, to callers that forward-dispatch `RegisterEvents` to a DIFFERENT workflow master (not the InvoiceDebtChase one).

## Root cause (hypothesis — needs maintainer confirmation)
The reverse maps (`FactPathFinder.BuildReverseMaps` / `ReverseDispatch` / `ReceiverProfileByCallee`, `FactPathFinder.GraphIndex.cs`) admit dispatch predecessors that the forward walk narrows away:

- Forward, a virtual/interface call site is **receiver-narrowed** (`ReceiverType` mined onto the edge → dispatch resolves to the concrete override only). E.g. a caller doing `IWorkflows.Register(someSpecificMaster)` forward-dispatches `RegisterEvents` to that master's override, never `InvoiceDebtChase.Master.RegisterEvents`.
- Reverse, from `InvoiceDebtChase.Master.GetCompany` the walk climbs to the override `…Master.RegisterEvents`, then to the **shared base/interface node** (`WorkflowMasterBase.RegisterEvents` / `IManagedWorkflowMaster.RegisterEvents`), then to **all** callers of that virtual/interface method — including those that forward would dispatch to a sibling override. When the call site's receiver is base/interface-typed or unresolved, `ReceiverProfileByCallee.AnyUnreliable` forces the CHA fallback, so the reverse hop is not narrowed and admits sibling-override callers.

The `Work/Edit` / `ManageRecall` SYNC case shows the same shape without any handoff in the loop, so it is a pure reverse-dispatch (virtual/interface) over-approximation, independent of the handoff machinery.

Net: **reverse traversal does not apply the receiver-type narrowing symmetrically with the forward traversal**, so `callers` is a superset of the forward-consistent answer.

## Why it matters
`callers --entrypoints` and `path` are the two precision commands ("which of my real entry points touch this code" / "show the path"). When `callers` lists an EP that has no forward path, an auditor either (a) trusts it and over-scopes the blast radius / auth-reachability set, or (b) tries to `path` it, gets "No path", and loses confidence in the tool. In the healthcode MR audit this inflated the `GetCompany` EP set by ~5 of 34 and `GetMedicalPerson` by 3 of 9 — every one a false positive with a zero-overlap forward closure. Over-approximation is the *safe* direction for a reachability question (better than missing a real reach), but it is still wrong and contradicts `path`/`tree`.

## Suggested fix
1. **Apply forward receiver-narrowing in reverse** — when expanding reverse dispatch through a virtual/interface method, only admit a caller whose forward dispatch (given its mined `ReceiverType`) actually resolves to the override on the path. This is the symmetric counterpart of the forward `DispatchTargets` narrowing; the data (`ReceiverType`, `ReceiverProfileByCallee`) is already present.
2. **Cheaper interim guard** — when `callers --entrypoints` emits an EP, verify a forward path exists (the bounded forward closure is already loadable) and drop / mark-low-confidence EPs that fail. This makes `callers` ⊆ forward-reachable by construction, restoring the callers↔path invariant even before the reverse narrowing is fixed.
3. **Disclose** — until fixed, note in `callers --entrypoints` output (or the skill) that reverse reach is a CHA superset over virtual/interface dispatch and may list EPs with no forward path; `rig path <ep> <target>` is the authoritative confirmation.

## Repro files
`C:\Git\meddbase-analysis\paths-now-GetCompany.txt`, `paths-now-GetMedicalPerson.txt` — the per-EP `rig path` runs; the "No path" entries are exactly the reverse-only false positives (5 of 34 / 3 of 9). Store `caa9373f` in `C:\Git\meddbase-analysis\.rig`.

Files to investigate: `Rig.Domain/Functions/FactPathFinder.GraphIndex.cs` (`BuildReverseMaps`, `ReverseDispatch`, `ReceiverProfileByCallee`, `ReverseDispatchReaches`), `Rig.Cli/Commands/CallersCommand.cs`.

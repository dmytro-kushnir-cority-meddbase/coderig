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

---

## Root-cause refinement (2026-06-24): non-virtual `base.M()` calls dominate the fan — a cheaper, provably-correct sub-fix for #1

Investigating the FR-7 `cache_coherence` finding (GitLab work-item 4460, target
`ContactEntity.RemovePersonContactLinks`) pinned the **dominant** false-positive population of the open
root cause (fix #1) to a specific, distinct sub-case that the section above describes only generically
("when the call site's receiver is base/interface-typed or unresolved, CHA fallback"). It is sharper than
that: most of the spurious reverse callers are **non-virtual `base.M(...)` calls from inside *sibling*
override bodies** — which can be excluded with zero receiver-type analysis, because a `base.M()` call can
*never* dispatch to a sibling override (C# spec).

### Measured on store `caa9373f` (MedDBase, net48 + LLBLGen)

`rig callers "ContactEntity.RemovePersonContactLinks" --entrypoints` → 5 forward-confirmed / **3325
reverse-only**; reverse closure = **15,828** methods. The blow-up is one hop above the only true caller
(`ContactEntity.Delete`, d1):

```
target ← ContactEntity.Delete ←[override-dispatch, climb up]← EntityBase.Delete ←[invocation]← {340 callers}
```

- `EntityBase.Delete(IPredicate)` has **340** real `call_edges` into it; `CommonEntityBase.Delete` 42.
- **48 of those callers are sibling override bodies calling `base.Delete(pred)`** — `AppointmentEntity.Delete`,
  `CaseEntity.Delete`, `DocumentEntity.Delete`, … Tell: `ReceiverType` is the LLBLGen intermediate base
  `XxxEntityBase` (the static type of `base`), and `Kind = invocation` (NOT marked non-virtual).
- The `override-dispatch` climb edge `EntityBase.Delete → ContactEntity.Delete` has **`Basis = roslyn`**
  (exact) — so this is a *modeling gap*, not a `~heuristic` name/arity artifact.
- Forward is correctly cut: `rig path "AppointmentEntity.Delete" "ContactEntity.Delete"` → No path;
  `rig path "AppointmentEntity.Delete" "ContactEntity.RemovePersonContactLinks"` → No path.

### Why these are *provably* impossible (not just CHA over-approximation)

A `base.M()` call is **non-virtual by C# spec** — it binds to the base implementation and can never reach a
sibling override, *regardless of receiver type*. rig currently records it as an ordinary `invocation`
`call_edge` into the shared base node, and that node also carries the `override-dispatch` fan to every
overrider. Reverse traversal climbs override→base, lands on the hub, then walks back down every hub caller —
fanning the non-virtual base-call out to all 48 siblings. The RTA narrowing proposed in fix #1 *would* also
reject these (`ReceiverType = XxxEntityBase` doesn't forward-resolve to `ContactEntity.Delete`), but the
non-virtual observation gives a cheaper, judgement-free cut.

### Proposed sub-fix (complements fix #1, smaller blast radius)

Tag `base.M(...)` call-edges as non-virtual at extraction (`Kind = base_invocation`, or a `NonVirtual` flag
on `call_edges`) and **exclude non-virtual edges from the reverse `override-dispatch` fan**. A non-virtual
base call must not reverse-fan to sibling overrides — provably correct, no receiver-type machinery required.
This alone removes the 48-entity LLBLGen population that dominates the MedDBase reverse-only set; the broader
RTA narrowing (fix #1) then mops up the genuinely-virtual remainder (generic deleters: `ObjectStore.Delete`,
`TEntity` repository code holding a base/abstract reference).

Extraction site to mark base-calls: Roslyn `IInvocationOperation` where the instance receiver is a
`BaseReferenceExpression` (`base.M()`), or `IMemberReferenceOperation` on `BaseReference`. Files as above,
plus the extraction classifier (`Rig.Analysis/Extraction/FactExtractor.cs`).

### Repro queries (run from `C:\git\meddbase-analysis`, `DB=.rig/caa9373ffbf6/rig.db`)

```sql
SELECT FromSym, ToSym, Kind, Basis FROM dispatch_edges WHERE ToSym LIKE '%ContactEntity.Delete(%';
SELECT COUNT(*) FROM call_edges
 WHERE FromSym LIKE '%Entity.Delete(SD.LLBLGen%IPredicate)' AND ToSym LIKE '%EntityBase.Delete(%';  -- 48
SELECT ToSym, COUNT(*) FROM call_edges
 WHERE ToSym LIKE '%EntityBase.Delete(%' OR ToSym LIKE '%CommonEntityBase.Delete(%' GROUP BY ToSym;  -- 340 / 42
```

### Validation (2026-06-24): FORWARD fixed; REVERSE gate partial (first-reach-wins) — follow-up

Shipped the non-virtual `base.M()` flag end-to-end (extraction `ReferenceFact.NonVirtual` + `CallEdge`
round-trip + forward `DispatchTargets` and reverse `BuildReverseMaps` gates) and re-indexed (store
`caa9373f-dirty`, **1,239 `NonVirtual=1` edges**).

- **FORWARD — fixed.** Base-call siblings no longer fan to the target: `rig path DocumentEntity.Delete →
  ContactEntity.RemovePersonContactLinks` and `… AppointmentEntity.Delete …` both return **No path** (were
  reachable via the ×49 forward fan). So `path`/`reaches`/`tree` precision is genuinely improved.
- **REVERSE — only partial.** The reverse closure barely shrank (15,828 → 15,521 methods;
  `callers --entrypoints` reverse-only 3325 → 3309). Cause = the reverse gate's `viaReverseDispatch` is
  **first-reach-wins**: the base node (`CommonEntityBase.Delete`/`EntityBase.Delete`) is reached BOTH via the
  override→base dispatch climb AND via a *direct* `base.Delete()` from the target's OWN override
  (`ContactEntity.Delete` base-calls its base). When the direct reach wins, `currentViaReverseDispatch=false`,
  so the sibling base-callers are never excluded.
- **No regression / still correct:** `cache_coherence` 4-high stable, EP kinds healthy, baseline store
  preserved (new store is `-dirty`). `callers --entrypoints` stays correct (5 confirmed; the 3309 are
  correctly caveated via the forward-verification partition). Only the raw reverse closure
  (`--roots`/`--include-reverse-only`, the disclosed-heuristic surface) is still inflated.

**FOLLOW-UP (the remaining reverse half of fix #1):** make the reverse exclusion independent of first-reach —
either treat a base node reached *via-dispatch* vs *via-direct* as distinct BFS states (2-colour / re-visit),
or have reverse override-dispatch yield the base's **virtual** callers DIRECTLY (excluding non-virtual
`base.M()` callers), bypassing the shared base node. This is essentially the documented hard part of fix #1
(symmetric path-precise reverse narrowing).

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
> **The ROOT CAUSE (fix #1) is RETIRED (2026-06-25)** — not by making the reverse closure independently
> precise, but by recognizing there is nothing to make precise: forward-verification already makes the
> emitted answer forward≡reverse, and the residual raw reverse-only set is an *eliminable asymmetry artifact*
> (reverse less-narrowed than forward), not the irreducible CHA residual (that lives in forward's `×N`
> fan-out, disclosed symmetrically). See **Resolution of fix #1** at the foot of this doc.

**Status:** resolved (fix #2 shipped; fix #1 retired — see Resolution note) · **Severity:** medium (false positives in `callers --entrypoints`; safe-direction over-approximation but violates the callers↔path/tree invariant and inflates "who reaches X" / blast-radius answers) · **Found:** 2026-06-23 (MedDBase healthcode MR auth-reachability audit) · **Distinct from** `bug-callers-path-overreach.md` (that is the OPPOSITE direction — `path`/`callers` over-reaching vs `reaches`/`tree` via `nameof`/event edges; this is `callers` over-reaching vs forward `path` AND `tree`).

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

> **~~FOLLOW-UP retired~~ (2026-06-24): the reverse closure is now FORWARD-VERIFIED on ALL THREE caller
> paths.** Forward-verification (fix #2) was extended from `--entrypoints` to the DEFAULT `callers` path and
> `--roots` (same `FactPathFinder.SeedsReachTarget` engine): each reverse-reachable caller/root is forward-
> reached toward the matched (depth-0) target nodes; confirmed = headline count, reverse-only = caveat footer
> (listed only under `--include-reverse-only`). TSV gains an additive `forwardConfirmed` column on both paths.
> **Reverse is now just a cheap, sound candidate generator; forward is the arbiter — they match by
> construction**, so the callers↔path/tree invariant holds on the shaped path regardless of the reverse-gate
> imprecision. Measured (god-seam `ContactEntity.RemovePersonContactLinks`, store `caa9373f`): default
> `callers` **15,503 → 30 forward-confirmed** (+15,473 reverse-only); `--roots` 4,078 → 1 confirmed (the real
> guarded `Contact/Edit.Delete`); forward-verifying the entire closure ~10 s (batched, parallel). **This
> RETIRES the "symmetric path-precise reverse narrowing" / reverse-gate first-reach follow-up** — we no longer
> need to make the *reverse* closure independently precise, because we forward-verify it. The raw reverse
> superset is still available (`--include-reverse-only` to list, or `--raw` for the unshaped closure) — the
> right tool for blast-radius / dead-code, where the sound superset is what you want. Test:
> `tests/Rig.Tests/Domain/CallersForwardVerifiedClosureTests.cs`.

### Mechanism #2 (method-group receiver) — shipped 2026-06-24

`FactExtractor.ReceiverTypeOf` now runs for `RefKinds.MethodGroup` too, so a `Retry(cert.Delete)`-style
method group carries its receiver (`CertificateEntity`) and dispatch narrows it instead of CHA-fanning ×49.
(Static-class qualifiers capture their type harmlessly — no overrides to fan; a bare implicit-`this` method
group still gets null, a minor accepted gap.) Result on the store: `callers
ContactEntity.RemovePersonContactLinks --entrypoints` went **5 → 4 confirmed** — `CertificateTransactions.Inbox`
(a method-group phantom) is gone (`rig path … = No path`); `cache_coherence` 4-high stable, no regression.

The 4 remaining confirmed EPs: `Contact/Edit.Delete` (the real, guarded contact-delete) + `Import.Inbox` /
`ImportInstances.Inbox` / `AppointmentSync.Inbox` — the latter three are **mechanism #3**: their delete
receivers (`LoginEntity` in ChambersDb, `PathwayTaskStateAppointmentEntity` in Pathways) are first-party but
their `Delete` overrides are **cross-assembly** and absent from the external `EntityBase.Delete` mined dispatch
target set (all 49 are `MedDBase.DataAccessTier`), so receiver-narrowing finds no in-scope target and falls
back to full CHA.

### Mechanism #3 (inherited-receiver no-fan) — shipped 2026-06-24 (query-side, one line)

The "cross-asm override not mined" framing was **wrong** (verified): `LoginEntity` / `PathwayTaskStateAppointmentEntity`
**genuinely have no first-party `Delete` override** — no symbol, no dispatch fact; they *inherit* the external
`EntityBase.Delete`. The real bug was the narrowing fallback in `FactPathFinder.NarrowByReceiver`: for a
**reliable** receiver (past the `narrowRoot is null` guard) whose candidate overrides are all on *sibling*
branches (empty `subtree` **and** empty `ancestors`), it returned the **full candidate set** (CHA) — fanning
`login.Delete()` to all ~49 entity `Delete` overrides incl. `ContactEntity.Delete`.

**Fix:** return **empty** there instead. `narrowRoot` resolving guarantees the receiver→declaring-type
base-edge chain is intact — the *same* closure the override scan (`Descendants(declaringType)`) walked — so an
empty in-scope set means the receiver has **no first-party override on its line**: it runs the inherited impl,
which is reached via the **direct call edge** (separate from the override fan). So the sibling fan is spurious;
dropping it loses no real reach (an inherited *first-party* base impl is kept via the direct edge; an external
base inherits to a boundary). Unreliable base/interface receivers (`narrowRoot is null`) early-return and are
untouched. **Query-side — no re-index.**

Result on the store: `callers ContactEntity.RemovePersonContactLinks --entrypoints` **4 → 1** (only the real,
guarded `Contact/Edit.Delete`; `Import`/`ImportInstances`/`AppointmentSync` phantoms dropped). No recall
regression: `cache_coherence` 4-high stable, EPs 9936 / effects 183707 unchanged, full suite 608 green. The raw
reverse closure is unaffected (this is a forward-narrowing fix; `--entrypoints` is forward-verified) — the
remaining reverse-only bloat is the reverse-gate first-reach follow-up + the `ReverseDispatchReaches`
`AnyUnreliable` lever. Tests: `InheritedReceiverNoFanTests`.

---

## Resolution of fix #1 (2026-06-25): the split is the intended end-state — and reverse-only is an eliminable artifact, not irreducible residual

The open question on fix #1 was framed as "make the *reverse* closure independently precise (symmetric
path-precise reverse narrowing)." After monomorphization landed (it dropped this target's reverse-only set
from **3309 → 50** on the re-indexed store `0f7f84f2`), the conceptual question sharpened to: *should reverse
narrow exactly like forward, and is there any reason to present the reverse-only superset at all?* The
resolution:

### 1. On a shared graph, forward and reverse are the SAME relation

Reachability over a fixed edge set is direction-agnostic: "A reaches B forward" ≡ "B reaches A on the
reversed edges." So if both directions walk the **same shaped, identically-narrowed graph**, their answers
are equal *by construction* — that equality IS the forward≡reverse target. A correctly-narrowed backward
pass cannot discover a *true* node a correctly-narrowed forward pass misses. There is no legitimate
"backward finds roots forward can't."

### 2. Reverse's ONLY legitimate role is the root/EP index — efficiency, not extra truth

"What does X touch" → forward from X. "Who reaches sink S / what are the EPs" → reverse from S (roots are a
no-predecessor, i.e. reverse-graph, concept). The reverse question is *answerable* forward — run forward from
every candidate EP and filter — which is **exactly what forward-verification (fix #2) does**. Reverse-from-
the-sink is just the cheap index (one traversal vs forward-probing all ~9,936 EPs). Reverse must therefore
**not** carry its own dispatch semantics; the dispatch-narrowing predicate is **direction-free** (a
virtual/interface edge caller↔override is real iff the caller's mined `ReceiverType` *forward-resolves* to
that override). Today's divergence — `BuildReverseMaps` / `ReverseDispatchReaches` with the `AnyUnreliable`
CHA fallback + first-reach-wins through the shared base node — is precisely the bug, not a feature.

### 3. Why generics were RESOLVED but CHA is only DISCLOSED (they differ in kind)

- **Generic instantiation has recoverable static ground truth.** The type argument at `M<C>` is statically
  determined and propagates deterministically (incl. into lambdas). Monomorphization *recovers* the exact
  instantiation graph — not an approximation, the ground truth we'd been discarding. The effort paid because
  a precise answer existed in the facts.
- **Virtual/interface devirtualization has no static ground truth.** The runtime override depends on the
  receiver's runtime type — undecidable in general (the compiler itself emits `callvirt` to one static
  symbol). RTA receiver-narrowing sharpens it, but degrades to CHA at reflection/factory/`List<IFoo>`
  boundaries. The residual fan is **irreducible** — there's no precise answer to recover, only a sound
  over-approximation to disclose.

### 4. The reverse pass is a CANDIDATE GENERATOR; forward is the arbiter — so the headline is `reverse ∩ forward`

`callers` (all three modes: default / `--roots` / `--entrypoints`) does NOT use reverse to find the path. It
uses reverse (`FactPathFinder.ReachedBy`) to cheaply enumerate *candidates*, then forward-verifies each
(`FactPathFinder.SeedsReachTarget` toward the depth-0 matched target nodes). **Confirmed (forward-reaches the
target) = the headline answer; the rest = reverse-only.** Reverse proposes; forward decides. The reverse
pass's only job is to be a cheap, sound *superset* so forward has every real candidate to confirm — it is the
root/EP index, not a source of truth.

### 5. reverse-only is THREE things, only one of which is irreducible — and none belongs in the headline

`reverse-only = (reverse candidates that forward rejected)`. That set decomposes:

1. **Reverse over-approximation (the bulk; eliminable artifact).** Reverse is currently *less narrowed* than
   forward — a shared base/interface seam (`EntityBase.Delete`) rejoins every override's callers. Forward
   (receiver-narrowed) correctly rejects them. On store `0f7f84f2` the 50 reverse-only EPs for
   `ContactEntity.RemovePersonContactLinks` are dominated by this (verified false positives; `rig path` = No
   path). If reverse applied the same direction-free predicate, this component collapses to ≈∅.
2. **Forward's own under-approximation that reverse caught (the recall hedge — genuine, NOT artifact).**
   Forward narrowing can legitimately *miss* an interface-dispatch/lambda-only path (documented forward gap).
   Such an EP is truly reachable, reverse caught it, forward can't confirm it → it lands in reverse-only. This
   is exactly why the partition **caveats rather than drops** (`SeedsReachTarget` comment: "a forward reach
   can legitimately miss an interface/lambda-only path"). This component is the legitimate reason the bucket
   exists at all.
3. **The irreducible CHA residual is NOT here** — it lives in *both* directions equally and forward already
   discloses it symmetrically as the `×N` fan-out bucket ("could be any of these N — NOT a real call"). It is
   never reverse-only, because forward keeps it too (so it forward-verifies).

So "reverse-only is an eliminable artifact" was an over-simplification: it is **(1) eliminable artifact ∪ (2)
a real forward-miss hedge**. Neither belongs in the precise headline; (1) is noise, (2) is a recall escape
hatch for the rare forward false-negative.

### The actual recall RISK is none of the above — it's reverse UNDER-approximation

Because the headline is `reverse ∩ forward`, a true EP that **reverse** fails to generate (the disclosed
interface/lambda reverse-miss) never becomes a candidate → it is absent from the headline **and** from
reverse-only — a *silent* false negative, invisible in either bucket. This, not reverse-only noise, is the
recall property worth guarding: the candidate generator must stay a sound superset. The CHA
over-approximation (component 1) is the very thing that buys that safety margin, which is why narrowing
reverse is polish, not a correctness fix.

### Decision

- **CHA narrowing should be the same in both directions** — one direction-free predicate, applied up in
  reverse exactly as down in forward. That is the principled end-state; reverse-only collapses toward ∅ under
  it.
- **Forward-verification (fix #2) already makes the EMITTED answer forward≡reverse today** (headline =
  forward-confirmed). Fix #1 (symmetric reverse) and fix #2 (forward-verify the reverse closure) are
  *operationally the same computation* — "narrow reverse symmetrically" literally means "evaluate the forward
  predicate at each reverse hop." Fix #2 is the shipped, pragmatic form; the only thing still divergent is the
  **raw** reverse superset (`--include-reverse-only` / `--roots` / `--raw`).
- **Reverse-only is now HIDDEN by default (2026-06-25) — diagnostic, under a hidden flag.** Since the
  forward-confirmed set IS the answer and reverse-only is (1) noise ∪ (2) a rare recall hedge, the default
  `callers` output prints ONLY the confirmed answer — no footer, no count. The `--include-reverse-only` flag
  still lists it (the component-2 escape hatch for chasing a suspected forward false-negative) but is marked
  `Hidden` in `CallersCommand` so it is off the `--help` surface and doesn't read as a normal lens. Not
  deleted (it's a real hedge); just de-advertised. TSV is unchanged (always emits all rows + the
  `forwardConfirmed` flag, so consumers can still partition). The raw superset remains via `--raw`.

This **retires fix #1 as an open root cause**: there is nothing to make "independently precise" about reverse
— the answer is forward-verified, and the residual raw superset is the deliberate, disclosed
over-approximation. Remaining work is the optional polish of narrowing the *raw* reverse closure for the
blast-radius surface, tracked in the backlog, not a correctness gap.

# Effect-capture validation — findings (Jun 2026)

Validation of the fact-layer entry-point + effect derivers against MedDBase, done by
**tracing real entry points and checking whether the effects they reach are captured**.

## Method (and the rule we held to)

Ground truth = (1) a synthetic fixture where the answer is known by construction
(`playgrounds/LegacyNet48Web` + `tests/Rig.Tests/Analysis/FactDerivationTests.cs`), and
(2) **reading the actual MedDBase source**. The mined/derived DB is NOT ground truth — it is the
analyzer's own fallible output. See memory `feedback_ground_truth_fixtures`.

Tracing is done with the **shipped C# engine** (`rig path`, `rig reaches`), never an ad-hoc
reimplementation. Two reimplementation attempts (a Python forward BFS, a Python backward BFS)
each diverged from the real engine and produced wrong numbers before being discarded — concrete
evidence that the engine, not a scratch script, must be the oracle.

`rig reaches <pattern> [--maxdepth N] [--format tsv]` (added this session) reports the effects
reachable from an entry point, reusing `FactPathFinder` (call graph + interface→impl DI dispatch).

## What works (positive signals)

- **Captured effects are real.** Every spot-check resolved to genuine source (e.g. the Healthcode
  transaction: `invoice.Save()` / `Commit()` / `Rollback()` at `Master_HealthcodeServiceImpl.cs:1279/1303/1299`).
- **Cross-project DI stitching works.** UI action `InvoiceMain.SubmitToHealthcode` → (dialog
  `SaveClicked` handler) → `IHealthcodeService`→`Master` impl-dispatch → background queue →
  `Master.SubmitToHealthcode` writes — a 7-node cross-project path, effects captured.
- **Transitive `ClientPage` page detection works.** The page deriver BFSs the full base-type
  closure incl. generic bases — 908 page EPs incl. 179 routes ≥4 segments deep (`Home2Base`,
  `StartPage2/Main`, …). (NOTE: an analysis agent reported a "single-hop, 466 missing" gap — that
  was an artifact of its own single-hop SQL query, not the deriver. The deriver is transitive.)
- **UI → Workflows capture is the dominant case:** 22/36 effect-bearing Workflows methods (40/56
  effects) are reachable from the Pages/UI side via the real engine.
- **28-entry-point sweep:** 12 correct/plausible, 7 genuinely UI-only, 9 recall gaps.

## Gap inventory (real gaps, by type)

### Entry-point coverage
- **G1 — Fact EP deriver implements only `pageModel`.** No `classInheritance` family
  (background / service / WCF / HTTP / actor), no scheduler/method-group registrations, no
  `WorkflowMasterBase`/`WorkflowControllerBase` virtual-dispatch overrides. Result: **0 entry points
  for backend projects** (entire `MedDBase.Application.Workflows` derives 0 EPs despite being all
  background/service/event entry points: `ProcessHealthcodeQueue`, `CheckForZeroDebt`, `DoDueActions`,
  `WorkStatusCache : ChamberedServiceBase`, `Master : IHealthcodeService`).
- **G2 — `PageBase` (non-`ClientPage`) reflection-loaded pages unmodeled.** `PageLoad.Create()`
  reflects + instantiates `PageBase` subclasses (`Initialise`/`OnAction`) — incl. the login path.
- **G3 — Abstract `[ClientAction]` not re-dispatched to overrides.** `Home2ActionsBase.SendFollowUpReferral`
  is `abstract [ClientAction]` → traces 0 effects; the concrete `Home2Base` override reaches 17 DB
  writes. (Really a *virtual-override dispatch* gap in the call graph — see G6.)

### Effect detection / reachability
- **G4 — Unmodeled external providers.** No effect rule for SOAP/generated web-service proxies
  (`HCWebServices.submitBill` — the Healthcode submission, the highest-stakes external effect),
  HTTP/PDF print services, background-queue dispatch, or OpenAI/LLM (`SmartLetter.GetSmartLetterResponse`).
- **G5 — llblgen reads via entity constructors.** `new XxxEntity(pk[, transaction])` is a fetch
  (read) but the method-name rule can't see it → Workflows reports 0 reads despite dozens of
  ctor-fetches (e.g. `new InvoiceEntity(controller.PkInvoice, transaction)` at :1277, right beside a
  captured `Save()`). Needs the receiver/entity-type fact (slice-2 wall) or a ctor-fetch rule.
- **G6 — Virtual/abstract override dispatch missing from the call graph.** `FactPathFinder` does
  interface→impl dispatch but not base-virtual/abstract→override. Adding it recovers G3 and other
  framework virtual-dispatch effects (`WorkflowControllerBase.OnSave`, etc.).
- **G7 — Service-interface dispatch dead-ends when the impl/body isn't in the indexed set.** Mostly
  a mine-scope issue (G8), not a logic bug — `FactPathFinder` dispatches fine when the impl is present.
- **G8 — Mine scope.** The run covers only `MedDBase.Pages` + `MedDBase.Application.Workflows`.
  Service-layer/entity projects (`MMSEntityClasses`, `ServiceLayer.*`, `Application.Core`) have no
  body facts, so e.g. `DocumentEntity.Save()` is invisible. Broadening the mine recovers many G4/G7 misses.
- **G9 — Event-delegate firing→handler linking (low priority).** A `field.SaveClicked()` invocation
  isn't edged to the `+= handler` methodGroup; some flows only connect via the captured methodGroup.

## G1 resolution — fact-based classInheritance entry points (Jun 2026)

The fact EP deriver now derives **classInheritance** entry points (background/service/WCF/HTTP/
actor/lifecycle), not just pageModel. Mechanics: BFS closure over base **and interface** edges
(strict descendants — a handler on the root base itself is excluded), gated by `handlerMethods`,
`requireOverride` (via the `IsOverride` fact), `handlerMethodAttributes` (reusing attribute ctor
refs), and `handlerParameterTypes` (matched by simple name against the fact signature — without
this the gRPC rule degrades to "every override"). Shipped with fixture tests covering every branch
(`FactDerivationTests.Class_inheritance_backend_entry_points_are_derived`). Storage now also reads
all method rows (+`IsOverride`) and interface edges; these were already extracted, so **no re-mine
is needed** for the capability.

End-to-end proof on the live DB (throwaway rules, read-only `rig derive`):
- `baseTypes:["*"] + "*"` → 12772 EPs (path fires).
- `+ requireOverride` → 1318 EPs (the `IsOverride` filter is correct and populated).
- `MedDBase.Application.Core.Workflow.WorkflowMasterBase + "*"` → 250 EPs (base/interface closure
  works cross-project; the edge is recorded from the subtype side even though the base's project
  isn't indexed). With `requireOverride` → 0 (those handlers implement interfaces, they are not C#
  `override`s — recovering them is G6/G3, the virtual/interface override-dispatch work).

**Why the committed `meddbase-analysis` classInheritance rules still derive 0 backend EPs:** their
base-type FQNs are stale — e.g. the rule says `MedDBase.Application.Core.Background.IBackgroundProcess`
but the indexed type is `MedDBase.Application.Core.IBackgroundProcess`; `ChamberedServiceBase` isn't
found at all; `WorkflowMasterBase` is in `...Core.Workflow`, not `...Workflows`. AND the actual
service/process implementers live in projects outside the current Pages+Workflows mine scope (G8).
So lighting up real backend EPs = correct the rule FQNs **and** broaden the mine (the re-extraction
task) — a rules-data + scope change, not a deriver change. The deriver itself is done and validated.

## Distinction that matters
Detector *logic* is largely sound; captured effects are real and cross-project stitching works.
The dominant misses are **EP coverage** (G1) and **mine scope** (G8), then **rule additions**
(G4, G5) and **one traversal addition** (G6). These are additive, fixture-testable changes — not
rewrites.

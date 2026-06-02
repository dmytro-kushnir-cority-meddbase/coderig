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

## G6/G3 resolution — base-virtual/abstract → override dispatch (Jun 2026)

`FactPathFinder` now adds **base-virtual/abstract → override dispatch** alongside the existing
interface→impl hop. When a call resolves to a base-type method, the traversal also reaches the
SAME-named **override** on every (transitive, generic-stripped) subtype, gated on the `IsOverride`
fact so it never dispatches to unrelated same-named (method-hiding) members. This is what makes an
abstract `[ClientAction]` (e.g. `Home2ActionsBase.SendFollowUpReferral`) or a framework virtual
(`WorkflowControllerBase.OnSave`) reach the effects declared in its concrete override.

Implementation: `FactGraphData` now carries base edges + per-method `IsOverride`; the index builds a
generic-stripped base-edge lookup and a memoised strict-descendant closure (`TypeClosure`). Fixture
test `FactDerivationTests.Call_graph_dispatches_base_virtual_to_override` proves a call resolved to
the GENERIC virtual `WorkflowPaneBase\`1.Save` (empty body, no call edges) reaches `ReferralPane.Save`
and its llblgen write purely via override dispatch. On the live DB, `rig reaches SendFollowUpReferral`
(an abstract method — empty body) now reaches 327 methods; that reachability can only come from
dispatch hops. (It captures 0 *effects* today because those overrides' effects are llblgen-entity
ctor/receiver reads (G5) or live in projects outside the current mine scope (G8) — both still open.)

## G4 (partial) — external-provider effect rules (Jun 2026)

External providers (SOAP/HTTP/queue/LLM) are pure rule DATA — `FactEffectDeriver` matches them with
no engine change. Proven in the fixture (`External_provider_effects_are_derived_from_rules`: a soap/
http_print/queue/llm effect each, via stub providers + four rules). The real **Healthcode SOAP
submission is now captured**: a `soap`/`submit` rule (methods submitBill/requestRegistration/etc.,
gated on the `MedDBase.Application.Workflows.HealthcodeWebServices` proxy namespace) added to
`meddbase-analysis/rig.rules.json` derives **2 submit effects** on the existing index — submitBill
was invisible before. A companion `soap`/`query` rule covers the read operations.

Still open in G4: HTTP/PDF print, background-queue dispatch, and OpenAI/LLM
(`SmartLetter.GetSmartLetterResponse` — not in the current mine scope, 0 symbols). Their real rules
need the actual client type names, so they wait on the broadened re-extraction (task #6).

## G5 resolution — llblgen entity-constructor fetch reads (Jun 2026)

`new XxxEntity(pk[, txn])` is an llblgen fetch (read), but it's a constructor call. Two gaps fixed:
1. **Extraction**: `GetSymbolInfo` on a type *name* resolves to the type (recorded as `typeUse`),
   never the constructor — so object creations carried no constructor/argument fact. `FactExtractor`
   now has an object-creation pass that resolves the invoked constructor and emits a `ctor` ref with
   the constructor DocID (carrying the argument types). **This is a stage-1 fact change → needs
   re-extraction to take effect on the real index** (the fixture analyzes fresh, so its test works now).
2. **Deriver/rule**: `FactEffectRule` gained `MatchConstructor` + `MinArguments`; `FactEffectDeriver`
   now matches `ctor` refs for such rules, gating the CONSTRUCTED type (parsed from the ctor DocID,
   brace-depth-aware arg count) by the usual type gates. The fixture rule `llblgen`/`fetch`
   (declaringTypeBaseTypes `EntityBase2`, `matchConstructor`, `minArguments:1`) derives exactly the
   two with-argument `new InvoiceEntity(pk)` / `(pk, txn)` fetches and excludes the empty
   `new InvoiceEntity { ... }` (`Llblgen_entity_constructor_fetches_are_derived`).

Real meddbase rule deferred to the re-extraction (task #6): the entity types live in MMSEntityClasses
(outside current mine scope, so the `EntityBase2` base edge isn't indexed) AND the current DB predates
the object-creation ctor extraction. After re-mining, add a `matchConstructor` llblgen fetch rule
gated on the entity namespace (or `EntityBase2`) and validate against the `new XxxEntity(pk, txn)`
sites (e.g. `new InvoiceEntity(controller.PkInvoice, transaction)` at Master_HealthcodeServiceImpl.cs:1277).

## G2 resolution — PageBase reflection pages (Jun 2026)

`PageLoad.Create()` reflects + instantiates `PageBase` subclasses and calls their `Initialise`/
`OnAction` hooks (the legacy + login path). These were unmodeled because they aren't `ClientPage`.
Captured purely as rule data (no engine change): a `pageModel` rule (`baseTypes:["MedDBase.Pages.PageBase"]`)
makes each `PageBase` subclass a navigable `page` entry point, and a `classInheritance` rule
(`handlerMethods:["Initialise","OnAction"]`) makes the reflection-invoked hooks `pagehandler` entry
points (so effects in `OnAction` become reachable — nothing calls it from the ctor). Fixture test
`PageBase_reflection_pages_are_entry_points` proves both.

Validated on the live DB (`meddbase-analysis` rules): page EPs 908 → **1000** (+92 PageBase pages,
incl. the login path) and **45 new `pagehandler`** entry points.

## Distinction that matters
Detector *logic* is largely sound; captured effects are real and cross-project stitching works.
The dominant misses are **EP coverage** (G1) and **mine scope** (G8), then **rule additions**
(G4, G5) and **one traversal addition** (G6). These are additive, fixture-testable changes — not
rewrites.

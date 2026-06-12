# Handoff: coderig gap-fix sprint (G1–G5) + supervised re-extraction (G8) + in-flight source-generator indexing

## TL;DR
Gap-fixes G1–G5 are DONE, committed, fixture-tested. A supervised re-mine (136 projects, 0 failed)
broadened scope; G1 FQN fixes + G5 entity-ctor fetch validated on it and committed. **Two follow-ups
are in flight, both requested "implement both":** (1) make indexing run the source generator so the
`clientpage_proxy → ProxyBase` rule flip becomes viable, and (2) widen the mine to the
ServiceTier/PatientPortal/Plugins host projects to recover backend EPs. Item (1)'s code change is
WRITTEN but not yet validated — the validation re-index hit a **depleted Pages bin** (the mine
re-depleted it), so the generator no-op'd. Next agent: rebuild Pages, re-index, verify generated
proxies, then flip the rule; then do the mine-widening.

## Read these first (don't re-derive)
- `C:\git\coderig\docs\effect-capture-validation.md` — gap inventory G1–G9 + the G1/G2/G4/G5/G6/G3/G8
  resolution sections (method, what works, what's open). This is the source of truth for the sprint.
- Memory `project_coderig_status` — full sprint + re-extraction state, the env gotchas, FQN findings.
- Memory `feedback_mine_parallelism`, `project_meddbase_devenv`, `feedback_ground_truth_fixtures`.
- Prior handoff `C:\Users\dkushnir\AppData\Local\Temp\handoff-coderig-validation.md` (pre-sprint plan).
- Task list (#1–#6); #1–#5 completed, #6 in_progress with a detailed remaining-items description.

## Commits this session (all local, NOT pushed — push is user-gated)
Branch `fact-layer-stage2` in BOTH repos.
- `C:\git\coderig`: `7d10313` G1, `ccc33b4` G6/G3, `773ad1f` G4 fixture, `7b6eeed` G5,
  `6519828` G2, `c54f63e` G8 docs. Plus **UNCOMMITTED** working-tree change: the source-generator
  indexing fix in `src/Rig.Analysis/Inventory/SolutionSourceLoader.cs` (see below).
- `C:\git\meddbase-analysis`: `3385521` SOAP rules, `ccd09cf` PageBase rules,
  `6e4c856` classInheritance FQN fixes + llblgen entity-ctor fetch rule.

## What each gap delivered (already done — see docs for detail)
- **G1** classInheritance EP deriver (base+interface closure, requireOverride, attribute & param-type
  gates, `TypeClosure.ComputeStrictDescendants`). Real backend EPs need correct rule FQNs (done) + mine
  coverage (item 2 below).
- **G6/G3** base-virtual/abstract→override dispatch in `FactPathFinder` (transitive, generic-stripped,
  IsOverride-gated). FactGraphData gained BaseEdge + MethodRef.IsOverride.
- **G4** external-provider effect rules are pure data; SOAP shipped+validated (2 submit effects).
  HTTP/print/queue/LLM real rules deferred (need client types indexed).
- **G5** llblgen entity-ctor fetch: new object-creation ctor EXTRACTION in `FactExtractor` (stage-1
  fact change — needs re-extraction) + `MatchConstructor`/`MinArguments` rule knobs. Validated: +1359
  `llblgen fetch` on the re-mined index (gated on `…EntityClasses.CommonEntityBase`).
- **G2** PageBase reflection pages (pageModel + classInheritance rules). Live: page 908→1000, +45
  pagehandler EPs.

## The supervised re-mine (G8) — DONE
- Command (run from `C:\git\meddbase-main-application`, which holds `.rig`):
  `rig mine "<repo>\MedDBase.slnx" --from "<repo>\src\main\MedDBase.Pages\MedDBase.Pages.csproj" --rules "C:/git/meddbase-analysis/rig.rules.json" --identity 1E8E07368F463951 --parallelism 1`
- Pre-build is MANDATORY and must be `MedDBase.Pages.csproj` (NOT the `.slnf` — it's stale: references
  `MedDBase.ESignatureService.Messages` not in `MedDBase.slnx` → MSB4025). Build cmd:
  `& "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe" "src\main\MedDBase.Pages\MedDBase.Pages.csproj" /t:Build /p:Configuration=Debug /m /nologo /verbosity:minimal`
  Pages bin is FLAT (`bin/*.dll`, ~322 when healthy), NOT `bin/Debug/net48`.
- Result: 136 projects, 0 failed. effects 2192→9947, EPs 6385→6525. DB now 138 runs.
- The `rig` CLI used is the Debug build: `dotnet C:/git/coderig/src/Rig.Cli/bin/Debug/net10.0/Rig.Cli.dll <args>`.

## IN FLIGHT — item (1): source-generator indexing → clientpage_proxy flip
**Problem:** `ProxyBase` is indexed but no generated `<Page>Proxy : ProxyBase` types are —
`AdhocWorkspace.GetSourceGeneratedDocumentsAsync` does NOT execute generators in this design-time-build
setup. The clientpage_proxy rule can't be flipped to a `declaringTypeBaseTypes:["MedDBase.Pages.ProxyBase"]`
gate until those generated edges exist (flipping now zeroes out the effects).

**Change written (UNCOMMITTED) in `SolutionSourceLoader.cs`:** replaced the `GetSourceGeneratedDocumentsAsync`
block in `LoadProjectSourcesAsync` with `RunSourceGeneratorsAsync(project, ct)` — a new method that pulls
`project.AnalyzerReferences.SelectMany(ar => ar.GetGenerators(LanguageNames.CSharp))`, runs a
`CSharpGeneratorDriver.RunGeneratorsAndUpdateCompilation`, and indexes the new trees with semantic models
from the generator-updated compilation. Wrapped in try/catch (best-effort). Builds clean; full suite green
(36 pass / 2 skip — no fixture exercises a real generator, so this path is only validated against real Pages).

**Validation BLOCKED by depletion (this is the cliff-hanger):** I re-indexed Pages
(`rig index <Pages.csproj> --rules <meddbase-analysis> --identity 1E8E07368F463951`) and got **0 proxy
types AND compilation errors** (`ClientActionAttribute`/`MMS.Text`/`Response`/`PrintService` not found).
Those core types resolved fine during the mine (action EPs were 5477), so the cause is **the mine's 136
design-time builds RE-DEPLETED the Pages bin** after my pre-build. A broken Pages compilation → the proxy
generator finds no ClientPage subclasses → emits nothing. (I was mid-check of `ls src/main/MedDBase.Pages/bin/*.dll`
when the session was interrupted.)

**Next steps for item (1):**
1. Confirm Pages bin is depleted: `ls C:\git\meddbase-main-application\src\main\MedDBase.Pages\bin\*.dll | measure` (expect << 322).
2. REBUILD `MedDBase.Pages.csproj` (cmd above) to repopulate the bin.
3. Re-index Pages (single-project index is non-destructive): the `rig index … --identity 1E8E07368F463951` cmd above.
4. Verify generated proxies now exist: `rig symbols Proxy` should show `type … XxxProxy` rows (currently 0).
   Also confirm no/fewer CS errors in the index log.
5. If proxies appear: flip the 3 fact_derived clientpage_proxy rules in `meddbase-analysis/rig.rules.json`
   (the ones gated on `declaringTypes:["MedDBase.Pages"]`, reasons `*_fact`) to
   `declaringTypeBaseTypes:["MedDBase.Pages.ProxyBase"]`, drop `ShowWindow`. Then `rig derive` and confirm
   clientpage_proxy effects are still substantial (currently 2528 show + 522 redirect via the imprecise
   namespace gate) and now precise. Commit meddbase-analysis + update docs/memory.
6. If proxies STILL don't appear on a clean Pages compilation: diagnose whether `project.AnalyzerReferences`
   is empty (Buildalyzer didn't report the generator) vs. the generator DLL path missing vs. the generator
   producing nothing. The generator is `RequestResponseProxyGenerator` in
   `src/mms/Tools/RequestResponseProxyProjectBuilder.Roslyn`. Consider logging `generators.Length` and any
   generator diagnostics (currently swallowed by the catch).

## NOT STARTED — item (2): widen the mine to recover backend EPs
**Goal:** ChamberedServiceBase subclasses (the backend service EPs) live in host projects NOT in Pages'
dependency closure, so the Pages mine didn't reach them. Only 2 had inheritance facts (background EPs = 3
total today). Host projects found (grep `: ChamberedServiceBase`):
`MedDBase.ServiceTier`, `MedDBase.PatientPortal`, `MedDBase.Plugins`, `MedDBase.Web.BillingRules`,
`MedDBase.Web.Workflows` (+ already-mined Application.Workflows / Application.Core.Background.StandardServices).
csproj paths found: `src/main/MedDBase.PatientPortal/MedDBase.PatientPortal.csproj`,
`src/main/MedDBase.Web.BillingRules/MedDBase.Web.BillingRules.csproj`,
`src/main/MedDBase.Web.Workflows/MedDBase.Web.Workflows.csproj` (still need ServiceTier + Plugins csproj —
note ServiceTier subclasses are under subfolders like `MedDBase.ServiceTier/Document`, so the csproj may be
`MedDBase.ServiceTier.csproj` at the root or per-subfolder; verify).

**Approach (sequential — NEVER concurrent builds/mines; concurrency depletes bins):**
- `rig derive` aggregates facts across ALL runs in the DB (no per-run filter), so you only need each host
  project's OWN facts (its Subclass→ChamberedServiceBase edges + Startup methods). A single-project
  `rig index <host.csproj> --identity 1E8E07368F463951` per host suffices (deps already in the DB from the
  Pages mine, stitched by identity). Single-project index is non-destructive.
- For EACH host: build it first (`MSBuild <host.csproj> /t:Build /p:Configuration=Debug /m`), then
  `rig index <host.csproj> --rules C:/git/meddbase-analysis/rig.rules.json --identity 1E8E07368F463951`.
  Do them one at a time.
- Then `rig derive --rules C:/git/meddbase-analysis/rig.rules.json` and confirm `background:` EP count rose
  (was 1→3; expect more once these hosts' inheritance facts land). Sample the new EP routes.
- Alternatively a `rig mine --from <host.csproj>` per host pulls each host's full closure (heavier, more
  depletion-prone; single-project index is preferred here since the closure is already mined).

## Env gotchas (will bite you — confirmed this session)
- **Mine/many design-time builds DEPLETE the Pages bin (flat `bin/*.dll`)** even at parallelism 1 over 136
  projects. ALWAYS rebuild the target project's bin immediately before indexing it for real facts. This is
  exactly what broke item (1)'s validation.
- **Never run two MSBuild/mine/index operations concurrently** — bin corruption. The harness runs builds in
  background; wait for completion before the next build.
- `.slnf` is stale (MSB4025); build `MedDBase.Pages.csproj` directly (its closure == mine scope).
- CWD for all `rig` commands must be `C:\git\meddbase-main-application` (holds `.rig`).
- `--rules` MERGES (adds) onto built-in+global+local; a temp rules file is the safe way to dry-run a rule
  change without editing the committed file (used heavily this session — `cat > %TEMP%\x.rules.json` then
  `rig derive --rules <that>`; distinct provider/kind names let you read the contribution).
- This codebase is LLBLGen **SelfServicing** (`CommonEntityBase : EntityBase`), NOT Adapter — `EntityBase2`
  has 0 refs. The 1st-party→3rd-party edge `CommonEntityBase→EntityBase` didn't bind, so base-gate on
  `EntityBase` = 0; `CommonEntityBase` is the working base gate (1359; namespace gate `…EntityClasses` = 1577).
- Real backend type FQNs (mined): `MedDBase.Application.Core.IBackgroundProcess`,
  `MedDBase.Application.Core.ChamberedServiceBase` (rules had stale `.Background.` — fixed).

## Remaining open items (also in task #6 description)
- Item (1) finish (proxy flip) and item (2) (mine widening) — above.
- Nucleus `…Interfaces.Services.ServiceBase` FQN unresolved (left in rules, dead/harmless).
- Real G4 HTTP/print/queue/LLM rules once client types confirmed indexed (`SmartLetter.GetSmartLetterResponse`
  — note SmartLetter.cs had CS errors in the depleted re-index, so re-check after a clean Pages build).
- The ~218 entity→base edge gaps below CommonEntityBase (per-project binding gaps) — coverage, low priority.

## Suggested skills for the next session
- **/diagnose** — for item (1) if generated proxies still don't appear after a clean rebuild (disciplined
  reproduce→instrument→fix loop; the swallowed generator diagnostics are the first thing to surface).
- **/verify** or a subagent — to confirm the clientpage_proxy flip's precision (no FP MessageBox.ShowDialog,
  genuine proxy nav captured) once the rule is flipped, mirroring how `A` was verified this session.
- **/code-review** — before pushing the branch, review the uncommitted `SolutionSourceLoader.cs` generator
  change and the full sprint diff.

## First actions for the next agent
1. `cd C:\git\coderig && git status` — see the uncommitted `SolutionSourceLoader.cs` change; review it.
2. Check + rebuild the Pages bin, re-index Pages, verify generated proxies (item 1 steps 1–4 above).
3. If proxies appear → flip clientpage_proxy rules, derive, commit. Else → diagnose (item 1 step 6).
4. Then do item (2): build+index the ChamberedServiceBase host projects one at a time, re-derive, confirm
   backend EP gain, commit.
5. Commit the `SolutionSourceLoader.cs` change once validated; update docs/memory.

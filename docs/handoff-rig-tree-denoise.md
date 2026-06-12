# Handoff — rig tree denoise + outstanding work

_Prepared 2026-06-12 for a fresh agent. Pairs with the auto-memory at
`C:\Users\dkushnir\.claude\projects\C--Git\memory\` (see the entries it references)._

## TL;DR

A long session shipped a stack of `rig tree` denoise/correctness features plus a critical
generic-factory load fix, all on branch **`drop-legacy-model`** (fork
`dmytro-kushnir-cority-meddbase/coderig`), tool republished. **One real bug (PROBLEM 1) and the
deferred caching work (#24) remain.** Everything below is verified against the live meddbase store.

## How to start (read this first)

- **Two repos, two roles:**
  - `C:\Git\coderig` — the rig tool source (this repo, branch `drop-legacy-model`).
  - `C:\Git\meddbase-analysis` — the **query cwd**: holds the `.rig` store (the indexed meddbase
    facts) **and** `rig.rules.json` (auto-loaded from cwd — `--rules` is redundant when run from here).
  - `C:\Git\meddbase-main-application\src` — the meddbase SOURCE, for ground-truth verification.
- **Build / test / republish the tool** (from `C:\Git\coderig`):
  - Tests: `dotnet test RuntimeIntelligenceGraph.slnx -c Release /p:UseSharedCompilation=false` (129 green).
  - Full republish (build `-warnaserror` + csharpier check + pack + reinstall global `rig`):
    `./scripts/mini-ci.ps1 -SkipTests`. **Format first** if you touched C#: `dotnet csharpier format .`
    (mini-ci's csharpier check aborts on unformatted code).
  - **SDK note:** default SDK is .NET 11 preview; the MSBuild-bundle issue is FIXED (don't re-add
    `Microsoft.Build.Framework`/`StringTools` `18.6.3` pins — see `reference_coderig_msbuild_bundle`
    memory). No `global.json` needed.
  - `Rig.Domain` targets **netstandard2.0** — no `IReadOnlySet<>` (use `ISet<>`), no modern-only BCL.
- **Validation entry point** (the tree everything was tuned against), run from `C:\Git\meddbase-analysis`:
  ```
  rig tree InvoiceMain.HandleHealthcodeSettingsSaveClickedEvent --exclude throw --files --signatures
  ```
- **Store caveat:** the current store has **no materialized graph views** (`rig graph` not run), so
  queries take the **EF-fallback** load path (`Reads.LoadFactGraphAsync`). Correctness no longer
  depends on `rig graph` (the TypeArguments fix below), but you MAY run `rig graph` purely for query
  speed (switches to the bounded SQL path).
- **Detectors are data**: generic infra in C#, all detectors/rules in `rig.rules.json`
  (`feedback_coderig_detectors`). Validate against synthetic fixtures + real source, never the mined DB
  (`feedback_ground_truth_fixtures`).

## Shipped this session (branch `drop-legacy-model`, all pushed, 129 tests green)

| Commit | What |
|---|---|
| `6ffda8b` | MSBuild bundle fix — drop inert `18.6.3` pins; works under SDK 11, no `global.json` |
| `481039e` / `4f82ec1` | `--depth` alias for `--maxdepth`; **unknown-flag error** (per-command allowlist) |
| `c91ee06` | index default parallelism → CPU count (`index` only; `mine` unchanged) |
| `336edc6` | sibling-dedup: identical sibling edges → one `×N calls` line |
| `8a990b0` | **context-dispatch narrowing** (state-family): `IWorkflowState` dispatch → only the enclosing controller's states (`WorkflowStateBase<C>` base-arg); rule `contextDispatch` in rig.rules.json |
| `10cc3ac` | **event-subscription (`+=`) handoffs**: deferred handlers sync-cut, shown under `--async`. Generic (co-located event `read` `E:` + method-group). `Reads.EventSubscriptionSitesAsync` + `FactPathFinder.MarkEventSubscriptionHandoffs` |
| `96d7026` | single-impl interface fold: `IFoo.M → Foo.M` (1 target) → `Foo.M «via IFoo»` |
| `d3534e6` | **fix: EF-fallback dropped `CallEdge.TypeArguments`** → generic-factory rewrite + generic-dispatch narrowing were silently disabled on un-`graph`'d stores (Construct.New CHA-fanned ×49). Now carried. |

Also done earlier: getter/setter accessor walk (closed the property-setter capture gap), meddbase
reindex (233k symbols, swapped into `meddbase-analysis/.rig`; old at `.rig.prev`), and the
`ProvideService<T>` typed service-locator cut (committed in meddbase-analysis `rig.rules.json`).

Net effect on the InvoiceMain tree: **503 → ~191–317 lines** depending on flags, and materially more
correct (deferred handlers under `--async`, state dispatch family-narrowed, generic factories collapsed).

## Outstanding tasks (prioritized)

### 1. PROBLEM 1 — background-schedule handoff missed on multi-line `new` (REAL, confirmed)
**Symptom:** `AgedState.RegisterTermEndProcess → EndOfTerm` and its ~130-line subtree are expanded as
a synchronous call, but `EndOfTerm` is a deferred timer callback:
`new BackgroundProcessSchedule(termEnd.Value, EndOfTerm, "...")`.
**Root cause:** `HandoffClassifier.Classify` matches a method-group to its consumer at the **exact same
`(caller, file, line)`**. The handoff rule `meddbase.oneshot.schedule` (`.BackgroundProcessSchedule.#ctor`)
exists and *should* fire, but the multi-line `new(...)` puts the ctor at **line 106** and the `EndOfTerm`
method-group at **line 108** → co-location fails → not classified → expanded.
**Fix options** (decide in fresh context):
  - *Pragmatic:* relax co-location to a tight line window (consumer line ≤ method-group line ≤ +~4).
    Risk: over-classifying drops a real sync edge (recall loss) — keep the window tight.
  - *Precise:* match the method-group against its **enclosing invocation** via `FactStructuralContext`
    (`reference_facts.EnclosingInvocations`) instead of by line — threads that column onto the
    method-group `CallEdge` across load paths (same shape as the `TypeArguments` fix `d3534e6`).
**Files:** `src/Rig.Domain/Functions/HandoffClassifier.cs` (matcher), the load paths in
`src/Rig.Storage/Queries/Reads.cs` + `SqlReachability.cs` if threading structural context.
**Validate:** after fix, `EndOfTerm` should drop from the sync tree (visible under `--async`).

### 2. #24 — query caching (Stage 1 lazy effects sidecar) — biggest deferred item
Full plan in memory `project_coderig_caching_plan`. Summary: `reaches`/`tree`/`derive` recompute
`FactEffectDeriver.Derive` per query (~3.8s fixed). Cache it as a sidecar FILE under `.rig/cache/`
(store is read-only at query time; dir is writable), keyed `sha256(storeKey + rulesHash + schemaVersion)`.
Prereqs: a `rulesHash` helper (hash the merged effective ruleset) + `storeKey` (run-id + db size/mtime).
Stage 2 (per-node memo) has a receiver-context-sensitivity caveat; Stage 3 (materialize at `rig graph`)
optional.

### 3. PROBLEM 2 — `Cache<T,U>.GetResult → Invoices.GetResult` over-approximation (UNVERIFIED)
Sonnet review claims a generic-dispatch over-fan across the `Cache`/`CacheFunc`/`CacheBase` hierarchy
(resolving to an unrelated-type-param target, `×4` unexplained). Plausible generic-erasure CHA issue;
**verify first** before fixing — could be a misread. Deep (generic dispatch narrowing).

### 4. PROBLEM 3 — `AuditLog.Create().WithData().Log()` absent (NOT a tree bug)
The three calls ARE captured. Absent because the chain runs through `ProvideService<IAuditLog>()` (the
typed service-locator **cut**) and/or the terminal EventGrid/GCP-PubSub/Echo publishes have **no effect
rule** → branch reaches no captured effect → pruned. To surface audit/messaging effects, add an **effect
rule** for those providers (data), not code. Low priority / product call.

### 5. State→controller binding (deferred, now low value)
Originally proposed to collapse `OnComplete ×3` / `UnRegisterEvents ×13` deep fan-outs. The event-edge
marking (`10cc3ac`) already removed most of that bulk from the sync view. Only pursue if those fan-outs
resurface as noise on other entries.

## Key code map (for the fixes above)

- `src/Rig.Domain/Functions/FactPathFinder.cs` — traversal (`BuildTree`/`ReachesWithFanout`/`Find`),
  dispatch resolution (`DispatchTargets`, `NarrowByTypeArguments`, `NarrowByContextFamily`), receiver/
  binding carry (`Successors`, `PropagateReceiver`, `ContextControllerCarry`), graph transforms
  (`RewriteGenericFactories`, `MarkEventSubscriptionHandoffs`), `GraphIndex`/`BuildIndex`/`BuildContextFamilies`.
- `src/Rig.Domain/Functions/HandoffClassifier.cs` — method-group → handoff classification (PROBLEM 1).
- `src/Rig.Storage/Queries/Reads.cs` — EF-fallback graph load (`LoadFactGraphAsync`),
  `EventSubscriptionSitesAsync`. `SqlReachability.cs` — bounded materialized-graph load.
- `src/Rig.Cli/CliApplication.cs` — commands, arg parsing (`MaxDepthOf`, `KnownFlagsByCommand`),
  tree render (`RenderTreeNode`, `FoldSingleImplHops`), `LoadEffectReachInputsAsync`.
- `src/Rig.Analysis/Rules/AnalysisRuleSet.cs` + `Fact*RuleProvider.cs` — rule channel
  (`contextDispatch`, `traversalCuts`, `genericFactories`, `handoffDispatchers`).
- Rule data: `C:\Git\meddbase-analysis\rig.rules.json` (meddbase-specific; its own git repo).

## Verification discipline (lessons this session)

- The Sonnet/Opus reviews **mixed real findings with stale/effect-pruned ones** — always reproduce on
  the CURRENT store + read the cited source before acting. Two prior "findings" (a `×49` generic fan-out,
  "capture gaps") were dismissed as phantoms, then one turned out REAL once the store lacked graph views
  (the `d3534e6` fix). Trust the facts query + source, not the review summary.
- `--exclude throw` (drops exceptions) and the default **effect-prune** (branches reaching no captured
  effect are hidden) are deliberate — don't flag their omissions as bugs.

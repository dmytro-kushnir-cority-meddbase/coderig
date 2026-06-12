# Handoff — Async handoff edges (Phases 1–3): classification → sync-cut/`--async` → origin EPs

Implements the high-ROI core of `docs/ASYNC-FLOW-PLAN.md` (Fable 5's plan). Read that +
`docs/BACKGROUND-SURFACE-AND-ASYNC-MODEL.md` first — they are the design; this is the build order +
the freedoms/constraints.

## Goal
rig models async handoffs (a delegate handed to a dispatcher to run later / on another thread) as ordinary
**synchronous call edges**, so a timer registration looks like it *executes* its callback. Fix:
1. **classify** dispatcher-consumed `methodGroup` edges as a distinct `handoff` kind,
2. **default-cut** them (sync-cut traversal) with an `--async` mode that includes them tagged,
3. **promote** their targets to first-class background/timer/actor/event entry points.
Headline acceptance: `ProcessHealthcodeQueue`'s SOAP/DB effects stop appearing synchronously reachable from
master startup; `--async` restores them, tagged; `callers --roots` surfaces it as a true background origin.

## FREEDOMS (this codebase is non-production, pre-public)
Do NOT spend effort on compatibility. Specifically:
- **Re-index / re-`rig graph` freely** — mined data is discardable (~10 min to rebuild). No need for the
  "no-re-index" gymnastics in the plan: the **extractor is fair game**. If an exact mined fact is cleaner
  than a derive-time heuristic, mine it and re-index.
- **Schema / output / rule-JSON / TSV / glyphs are all fictional and changeable.** Change `CallEdge` and the
  `call_edges` table, add columns, redesign the rule sections, change command output. **No** `AddColumnIfMissing`
  degradation, **no** old-store fallback, **no** `--legacy-handoffs` switch. Old stores can simply be re-built.
- **Rewrite test expectations freely** to the new contracts (the equivalence tests below are *correctness*
  assertions, not frozen output — re-express them for the new modes).

## HARD CONSTRAINTS (correctness — keep these)
1. **Recall safety:** the sync-cut must never make something dead that is reachable today. `dead` keeps
   **ALL** `methodGroup` targets as roots regardless of classification (classification only improves labels).
2. **SQL == in-memory oracle, per mode:** the SQL set-reachability path must equal the `FactPathFinder` oracle
   for the same traversal mode. (This is the receiver-narrowing landmine — see Phase 2.)
3. Don't regress shipped behavior: receiver-type dispatch narrowing, source-order `Successors` sort, FTS
   `symbols`/`refs`, read-only query connections, `--only`/`--exclude`, emoji/UTF-8, `tree --effects`.

## Because the extractor is free: recommended classification approach
The plan offered co-location (derive-time, no re-index) as Phase 1 and exact `DelegateConsumer` extraction as
Phase 4. **Given re-index is free, do the exact extraction now** — it's the correct model, removes the
line-co-location heuristic's edge cases, and also catches `event +=` and lambda consumers:
- Mine on every `methodGroup` (and delegate-creation / `event +=` / delegate-assignment) ref a nullable
  **`DelegateConsumer`** = the DocID of the consuming invocation/ctor + arg index (the member the delegate is
  handed to). This is a *structural* fact (not rule data), so it belongs in the facts.
- Classification = join `DelegateConsumer` against the `handoffDispatchers` rule set at graph/derive time.
- Co-location (methodGroup ⋈ ctor/invocation at same FilePath+Line+EnclosingSymbolId, target ∈ dispatcher set)
  remains the **fallback** for refs without a `DelegateConsumer` — fine to keep as a secondary path, but the
  exact fact should be primary.
If the extractor work for `DelegateConsumer` balloons, fall back to co-location-only for P1 and revisit — but
try the exact fact first.

## Phase 1 — handoff classification + rules
- **`handoffDispatchers` rule section** (design the schema cleanly — it's fictional): per dispatcher, match
  pattern(s) for the consuming ctor/method DocID, an EP `kind` (`background`|`timer`|`actor`|`event`), and
  flags (e.g. `repeating`). Add a Domain projection (`FactHandoffRule`) + a provider mirroring
  `FactEffectRuleProvider`; merge builtin + `~/.rig` + project `rig.rules.json`. The MedDBase dispatcher set
  (from the surface doc) lives in `C:\Git\meddbase-analysis\rig.rules.json`, not in code:
  `MedDBase.Application.Core.Background.BackgroundProcessSchedule`/`RepeatingBackgroundProcessSchedule` ctors,
  `Echo.Process.spawn*`, `Echo.Router.fromConfig`, `IAsyncEvent<T>.Add`, `MedDBase.Min.Background.WithGlobal.Schedule<T>`.
- **Classify in the shared graph-load path** so the in-memory oracle and the SQL materializer agree by
  construction (the dispatch precedent): `Reads.LoadFactGraphAsync` + `FactPathFinder.AllCallEdges`. A
  dispatcher-consumed delegate edge → `CallEdge.Kind = "handoff"` + `HandoffDispatcher = <dispatcher id>`.
- **Schema:** `call_edges.Kind` gains value `handoff`; add `HandoffDispatcher TEXT`. `GraphMaterializer`
  writes them. `dispatch_edges` untouched. Just rebuild — no migration.
- **`DeriveHandoffEntryPointsAsync`:** classified output — background/timer/actor/event handoffs first (with
  dispatcher + registration site); collapse the unclassified-`methodGroup` residual to a count (kill the
  4,503 firehose as a display).
- **Gate:** `rig derive` classifies the ~60 MedDBase registration sites; `ProcessHealthcodeQueue` shows as a
  `background` handoff via `RepeatingBackgroundProcessSchedule`.

## Phase 2 — sync-cut default + `--async` (THE CORE — land atomically)
- **`FactPathFinder`:** thread a traversal mode (default **sync-cut** vs **async-include**) through
  `Find`/`Reaches`/`ReachesWithFanout`/`BuildTree`/`ReachedBy`/`ReachableFromAll`/`EntryRootsReaching` and
  `Predecessors`. Sync-cut skips `Kind=="handoff"` edges. Async-include walks them carrying a `HandoffVia`
  provenance — clone the existing `DispatchVia` machinery exactly (inherited forward through the subtree,
  dropped when the node is also reachable synchronously). Preserve the source-order sort and receiver narrowing.
- **`SqlReachability`:** a kind-filter parameter on `ReachedWithDepthAsync`/`ReachableSetAsync`/
  `EntryRootsReachingAsync` (`AND Kind <> 'handoff'` on the `call_edges` leg for sync). The **bounded load
  stays the superset** (includes handoffs) — one bounded graph serves both modes; filtering happens in the
  in-memory walk over it. (Same invariant as CHA-vs-narrowed.)
- **CLI:** `--async` on `reaches`/`tree`/`path`/`callers`. Renderers: handoff hop glyph (e.g. `⤳ via
  RepeatingBackgroundProcessSchedule`); `reaches` splits output into **direct / dispatch fan-out / async
  (scheduled)**; `path` renders the handoff hop. `dead` explicitly keeps all `methodGroup` targets as roots.
- **Land all three sites in ONE change** (FactPathFinder + AllCallEdges/materializer + SQL filter) — the
  SQL==oracle equivalence breaks if they diverge (receiver-narrowing precedent).
- **Tests:** add a dispatcher **zoo** to the `playgrounds/LegacyNet48Web` fixture (a BPS-like
  ctor+methodGroup, a `Task.Run`, an `event +=`, a fire-and-forget Task, and a lambda registration). Rewrite
  `SqlReachabilityTests` to the mode contract: `CHA-oracle(sync) == SQL(sync-filter)` and
  `CHA-oracle(async) == SQL(unfiltered)`, both directions; plus `narrowed ⊆ SQL`, `sync ⊆ async`, and
  `bounded(mode) == full(mode)`.
- **Gate:** `rig reaches/tree ProcessHealthcodeQueue` (and from master startup) no longer shows the background
  effects synchronously; `--async` restores them tagged; `callers --roots ProcessHealthcodeQueue` lists it as
  a background origin.

## Phase 3 — execution-origin EPs + observations
- Promote dispatcher-classified handoff targets to `DerivedEntryPoint`s — kind from the matching dispatcher
  rule, route = target FQN, registration site as file/line. **Dedup** against L1-rule EPs (a `Process()`
  override can be both an L1 EP and a handoff target).
- `async_handoff` observation (derive-time, `FactObservationDeriver`, data-driven) on registration
  invocations; `cross_thread` provenance tag rendered in `reaches --async`/`tree --async` (flag-only, from
  `HandoffVia`). If you mined `NotAwaited` in Phase 1's extraction, add the `fire_and_forget` observation too;
  otherwise defer it.
- **Gate:** classified origins are valid `from` patterns; effects carry the tags.

## Explicitly OUT of scope (deferred / never — from the plan)
- Full synthetic lambda symbols (the heuristic + `DelegateConsumer`/`InLambda` cover the dominant shapes;
  full lambda-node modeling perturbs the whole graph + identity — defer until measured residual demands it).
  You MAY mine an `InLambda` flag + a single-method-group-call heuristic (`() => Foo()` ⇒ Foo is the callback)
  since the extractor is open — include it if cheap; it closes most of the lambda residual (M6-lambda, M9).
- Message-type→handler **traversable** edges (unsound routing) — Phase 5 report-only, not in this handoff.
- Interleaving / races / ordering / thread identity — out of static reach; tag, never order. Permanent no.
- A third traversal mode.

## Build / ship / validate (repo conventions)
- Iterate: `dotnet build src/Rig.Cli/Rig.Cli.csproj -c Release -v q`; query via the built dll from the store dir.
- Before mini-ci: `dotnet tool run csharpier format .` (mini-ci runs `csharpier check .` + `-warnaserror`).
- Ship + reinstall global tool: `pwsh -NoProfile -File scripts/mini-ci.ps1`; confirm `rig --version` bumps.
- After the change, **re-index AND re-`rig graph`** `C:\Git\meddbase-analysis` (it carries the L1 background
  rules + receiver narrowing; re-index because the extractor changed). `meddbase-main-application` OOMs on
  `rig graph` — use meddbase-analysis for validation.
- Skill docs live in `C:\Git\coderig\.claude\skills\rig\{SKILL,REFERENCE}.md` (also installed at
  `~/.claude/skills/rig/`). Update them for `--async` / the handoff edge kind / new `reaches` sections, and
  for the count-semantics already noted. (Reinstall copies to the user path.)

## Definition of done
- P1: `handoffDispatchers` rules + classification; `rig derive` classifies the registration sites;
  `ProcessHealthcodeQueue` = `background` handoff via `RepeatingBackgroundProcessSchedule`.
- P2: sync-cut default + `--async` across `reaches`/`tree`/`path`/`callers` + the SQL set queries;
  `ProcessHealthcodeQueue` gone from master-startup sync reach, restored under `--async`; the fixture zoo +
  rewritten mode-parameterized equivalence tests green; `dead`-recall invariant asserted.
- P3: classified handoff targets are `DerivedEntryPoint`s (deduped); `async_handoff`/`cross_thread` (+ optional
  `fire_and_forget`) observations.
- Full suite green, csharpier-clean, shipped via mini-ci (version bump), meddbase-analysis re-indexed+re-graphed.
- Report: files changed, the classification approach taken (exact `DelegateConsumer` vs co-location),
  before/after numbers (`ProcessHealthcodeQueue --effects` sync vs `--async`; `derive` classified-handoff
  count; firehose residual), test results, shipped version, and any residual (lambdas not covered, etc.).

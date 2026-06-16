# Background-processor surface + async/concurrency model (MedDBase)

Two intertwined topics: (1) the concrete "OOP zoo" of background mechanisms in the MedDBase monolith and
how to detect their entry points; (2) the deeper model problem — rig is a SYNCHRONOUS reachability tool and
models async handoffs as synchronous call edges. This doc is the living model + the phased roadmap.

> **STATUS 2026-06-16.** Phases 0–3 of the roadmap below (L1 entry-point rules, handoff edge-kind
> classification, sync-cut/`--async` traversal, origin EPs) have **SHIPPED** via the deferred-delegate work
> (#18–21: Rx-subscribe handoff, lambda identity, stored delegate-field seam). Phase 4 (extractor facts:
> `DelegateConsumer`/`InLambda`/`NotAwaited`) is **partly done** — `DelegateConsumer` + the delegate-field
> seam landed; `NotAwaited`/fire-and-forget tagging is not. Phase 5 (message report-only) is open. The
> original standalone phased-plan doc and the P1–3 build spec were merged into this file and removed
> (recover from git history if needed); their substance is folded into "Phased plan & ROI" below.

## Central runtime truth
All background work funnels through one boundary: `IBackground.AddProcess(IBackgroundProcess)`
(`MedDBase.Application.Core.Interfaces/IBackground.cs`). `BackgroundThreads.ProcessLoop`
(`MedDBase.Application.Core/Background.cs:34`) spawns a `Thread` per process and calls `OnStartup(app)`
then **`Process()`** — the actual thread entry method. Every other mechanism is sugar that ultimately
registers an `IBackgroundProcess` whose `Process()` invokes a delegate.

## The async-handoff model (the real fix, for the Fable plan)
rig's call graph is caller→callee, evaluated as if execution is sequential. Concurrency is a different
topology: a **forest of execution origins joined by handoff boundaries**, where a delegate is registered
now and invoked later / on another thread by a scheduler/dispatcher. rig today models those handoffs as
**ordinary synchronous call edges** — conflating "schedule X to run later" with "call X now" (why
`ProcessHealthcodeQueue`'s SOAP/DB effects look synchronously reachable from master startup).

Boundary taxonomy and correct model:
- **`await`ed Task** → normal call edge (sequential; already correct).
- **delegate → dispatcher** (`Task.Run`, `new Thread`, `ThreadPool`, `Timer`, the MedDBase schedules,
  `event +=`, Echo `spawn`/`.tell`) → **handoff**: callback is a ROOT (new execution origin); the
  registrar→callback link is a **distinct non-synchronous edge kind**, default-cut from sync reach.
- **fire-and-forget Task** (`_ = FooAsync()`, `Task.Factory.StartNew`) → body reachable, **tag concurrent /
  not-awaited**.
- **message / actor** (Echo `.tell`/`.ask`, queue, event bus) → **uncrossable statically**; handler
  (`Inbox`/`OnMessage`) is an independent EP; sender→handler only via heuristic msg-type→handler match.

Proposed capture: a distinct `handoff` edge KIND (dispatcher set = data); promote callbacks to roots; two
traversal modes (sync-cut default vs `--async` tagged); concurrency observations (`async_handoff`,
`fire_and_forget`, `cross_thread`) alongside the existing `looped_effect`/`parallel_fanout`.
Reliable now (facts present): method-group handoffs to a known dispatcher via ctor/call co-location.
Needs extractor work: lambdas (synthetic symbol) + stamping edge kind at extraction. Irreducible: message
handler resolution, true interleaving/races.

## The zoo — 9 mechanisms (Sonnet code-level map, 2026-06-11)
Two detection layers: L1 = the execution-origin TYPE (structural, rule-gated); L2 = the handoff DELEGATE
(dispatcher→callback, co-location or lambda).

| # | Mechanism | Anchor / dispatcher | Entry method | callback form | rig today |
|---|---|---|---|---|---|
| M1 | `new RepeatingBackgroundProcessSchedule(TimeSpan, δ, name)` (~20 sites) | ctor; `BackgroundProcessScheduleDelegate` | the δ (e.g. `ProcessHealthcodeQueue`); base `Process()` loops calling `ProcessDelegate()` | method-group | L2 co-location (not yet) |
| M2 | `new BackgroundProcessSchedule(DateTime, δ, name)` one-shot (~15 + AsyncEvent internals) | ctor | the δ | method-group | L2 co-location |
| M3 | RepeatingBPS subclass overriding `SetProcessDelegate()` (~6: TaskCheckerService, SendReceiveService, EmailAutoLinkService, WorkItemAssignmentCheckingService, AnyProcess, ReferralSLAService) | base type | `Worker()`/`Run()` via `ProcessDelegate=…` | override | ✅ existing baseType rule |
| M4 | `ServiceBase.Startup()` override (~10–12: Expired/Contract/Licensing/Membership/Notification/SMS/Cache… ) | `ServiceBase`/`ChamberedServiceBase` | `Startup()` | override | ✅ existing rule |
| M5 | `AsyncEvent<T>`/`CoalescingAsyncEvent<T>` `.Add(handler)` (~15–25) | `IAsyncEvent<T>.Add`; internal `InvokeQueue()` on a BPS | subscriber handler (`.Add` arg); `InvokeQueue` is the thread entry | mostly method-group | ❌ |
| M6 | Echo `Process.spawn<>`/`Router.fromConfig` (~45) | `Echo.Process.spawn` | inbox `Action<TMsg>` | ~half method-group, ~half lambda | ❌ (echoactor rule needs IActor; most spawns don't) |
| M7a | XML `BackgroundTasks\*.xml` → `Delegate.CreateDelegate` | `Background.cs:205` | static method named in XML | n/a (reflection) | ❌ config-only, static-invisible |
| M7b | XML task → `IBackgroundProcess` subclass | `Background.cs` | `Process()` | override | ✅ if type indexed |
| M7c | `TaskCheckerService.Run()` → `Tasks.xml` → `InvokeMember("Execute")` | reflection | `Execute()` convention | n/a | ❌ config-only |
| M8 | `WorkflowMasterBase.RegisterEvents()` → inline BPS (~8–10) | `WorkflowMasterBase` subclass | `RegisterEvents` + the BPS δ (`ProcessHealthcodeQueue`, `CheckForZeroDebt`, `DoDueActions`, `CleanOpenControllersRepeat`) | method-group | partial |
| M9 | `WithGlobal.Schedule<T>(action)` fluent (~5–8) | `Min/WithGlobal_Scheduling.cs` | the action | lambda | ❌ lambda residual |

## Coverage estimate
- Current rules (L1: M3, M4, M7b): **~30–35%**.
- + method-group co-location join (L2: M1/M2/M5/M6-mg/M8): **~65–70%**, classified `background` (vs the
  4,503-entry generic method-group handoff firehose).
- Lambda residual (M6-lambda, M9, some M5, M7a/M7c XML): **~20–25%** — needs extractor / config-file work.

## Dispatcher set (for the L2 co-location handoff rule + the async edge kind)
`MedDBase.Application.Core.Background.BackgroundProcessSchedule` (ctor),
`…RepeatingBackgroundProcessSchedule` (ctor), `Echo.Process.spawn*`, `Echo.Router.fromConfig/…`,
`IAsyncEvent<T>.Add`, `MedDBase.Min.Background.WithGlobal.Schedule<T>`.

## Near-term plan (custom rules now — "treat background services like .NET Core workers/library code")
Gate the L1 execution-origin types as framework entry points (data-only, in the project rig.rules.json;
no re-index — rules reload per query): `IBackgroundProcess`→`Process`, `ServiceBase`/`ChamberedServiceBase`
→`Startup`, RepeatingBPS/BPS subclasses, `WorkflowMasterBase`→`RegisterEvents`. The delegate-callback
linkage (L2) and async edge model are the larger task below.

## Phased plan & ROI (folded from the archived async-flow plan)
The model fix reuses the receiver-narrowing precedent: facts are immutable/rule-agnostic, derived tables
rebuild cheaply via `rig graph` (no Roslyn), so anything expressible as a join over existing facts needs
**no re-index**. Bound = sound CHA superset; precision = in-memory; equivalence test asserts
`CHA-oracle == SQL` per mode and `narrowed ⊆ SQL`. The handoff cut keeps handoff edges in the superset and
sync-cut is a filtered traversal. **Not every methodGroup is a handoff** (`list.ForEach(Foo)` is
synchronous) — the fix splits *dispatcher-consumed* methodGroups (→ handoff, cut) from the rest, never the
reverse, or recall collapses.

| Phase | Effort | Re-index | Value | Status |
|---|---|---|---|---|
| **0. L1 entry-point rules** | S | No | dead roots + EP labels (M3/M4/M7b + Echo Inbox + WorkflowMaster) | ✅ shipped |
| **1. Handoff classification** (co-location + `handoffDispatchers`) | M | No (re-graph) | splits the 4,503 methodGroup firehose; classifies timer/actor/event callbacks | ✅ shipped (#18–21) |
| **2. Sync-cut default + `--async`** | L | No | the core fix — kills false synchronous reach; `callers --roots` shows true origins | ✅ shipped |
| **3. Origin EPs + `async_handoff`/`cross_thread`** | M | No | classified origins as `from` patterns; effect tags | ✅ shipped |
| **4. Extractor facts** (`DelegateConsumer`/`InLambda`/`NotAwaited`) | L | **Yes** | exact classification; `event +=`; lambdas; `fire_and_forget` | ◑ partial (DelegateConsumer + field seam done; NotAwaited not) |
| **5. Message report** (no traversable edges) | S | No | orientation at uncrossable boundaries (M6 `.tell`/queues) | ☐ open |

**Design axes (per the original plan, retained for the open work):** lambdas via `InLambda` flag +
single-method-group-call heuristic before full synthetic symbols; message/actor sender→handler is
**report-only** (candidate handlers by `FirstArgumentType` ⋈ signature, NO traversable edges — routing is
unsound). **Permanent no:** interleaving/races/happens-before/thread identity (outside static reach — tag,
never order); message-type→handler traversable edges; a third traversal mode.

## Key files
- `…/MedDBase.Application.Core.Interfaces/IBackground.cs` — `IBackgroundProcess`/`IBackground`
- `…/MedDBase.Application.Core/Background.cs` — `BackgroundThreads` + `LoadBackgroundTasks` (XML)
- `…/MedDBase.Application.Core.Background/BackgroundProcessSchedule.cs` + `RepeatingBackgroundProcessSchedule.cs`
- `…/MedDBase.Application.Core.Background/AsyncEvent.cs`, `CoalescingAsyncEvent.cs`, `Min/WithGlobal_Scheduling.cs`
- `…/MedDBase.Nucleus.Interfaces/Services/IService.cs` (`ServiceBase`), `…Core.Interfaces/ChamberedServiceBase.cs`
- `…/MedDBase.Application.Core.Workflow/WorkflowMasterBase.cs:268–329`
- `…/MedDBase.Processes/AppStartupProcesses.cs` (RegisterProcesses registry), `…/echo-process/Echo.Process/IProcess.cs`
- `…/MedDBase.Messages/GuaranteedMessaging.cs` (`PersistedQueueProcess<,>`)
- Rules: `C:\Git\meddbase-analysis\rig.rules.json` (M3/M4 covered) + `C:\Git\coderig\src\Rig.Cli\builtin-rules.json`

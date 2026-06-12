# Background-processor surface + async/concurrency model (MedDBase)

Two intertwined topics: (1) the concrete "OOP zoo" of background mechanisms in the MedDBase monolith and
how to detect their entry points; (2) the deeper model problem — rig is a SYNCHRONOUS reachability tool and
models async handoffs as synchronous call edges. This doc is the brief for both the near-term custom rules
and the larger async-flow design.

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

## Larger task (Fable 5 — PLAN + ROI first, do not one-shot)
The `handoff` edge kind, execution-origin roots, sync-cut/`--async` traversal modes, concurrency
observations, lambda extractor support, message-boundary handling. Requires thoughtful phased execution.

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

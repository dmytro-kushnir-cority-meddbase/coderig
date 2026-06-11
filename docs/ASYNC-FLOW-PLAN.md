# Async/concurrency model for rig — phased plan + ROI (Fable 5, 2026-06-11)

For evaluation. Companion to `BACKGROUND-SURFACE-AND-ASYNC-MODEL.md`. No code yet.

## Root cause
A `methodGroup` ref is already a distinct fact, but traversal walks it identically to an `invocation`
(`Reads.LoadFactGraphAsync`). So `new RepeatingBackgroundProcessSchedule(ts, ProcessHealthcodeQueue, name)`
makes a `RegisterEvents → ProcessHealthcodeQueue` edge that BFS crosses as if registration *executed* the
callback → (1) false synchronous reach, (2) callback never a root, (3) concurrency inexpressible.
**Not every methodGroup is a handoff** (`list.ForEach(Foo)`/`Select(Parse)` are synchronous), so the fix
must split *dispatcher-consumed* methodGroups (→ handoff, cut) from the rest (→ stay sync edge) — never the
reverse, or recall collapses. The 4,503 "handoff" firehose is both over-inclusive as roots and unlabeled.

## Architecture it reuses (the receiver-narrowing precedent)
Facts are immutable/rule-agnostic; derived tables rebuild cheaply via `rig graph` (no Roslyn). So anything
expressible as a join over existing facts needs **no re-index**. And: bound = sound CHA superset; precision
= in-memory; equivalence test asserts `CHA-oracle == SQL` and `narrowed ⊆ SQL`. The handoff cut follows the
identical shape (superset bound keeps handoff edges; sync-cut is a filtered traversal).

## Design recommendations (per axis)
1. **handoff edge kind** — classify via **co-location join now** (methodGroup ref ⋈ co-located ctor/invocation
   targeting a dispatcher-set member ⇒ `call_edges.Kind='handoff'` + `HandoffDispatcher`), in
   `Reads.LoadFactGraphAsync`/`AllCallEdges` so oracle and materializer agree; **exact `DelegateConsumer`
   fact later** (degrades to co-location on old stores). Reject extraction-time stamping of rule data.
   `dispatch_edges` untouched; SQL set queries get a `WHERE Kind<>'handoff'` (mode param).
2. **Origins as roots** — promote dispatcher-classified handoff targets to `DerivedEntryPoint`s (kind from a
   new `handoffDispatchers` rule section: background/timer/actor/event; route = target FQN; registration site).
   `dead` keeps **all** methodGroup targets as roots (recall rail). `callers --roots` then surfaces true origins.
3. **Traversal modes** — default **sync-cut** (skip `Kind=='handoff'`); `--async` includes them tagged with
   `HandoffVia` provenance (clone of `DispatchVia`); `reaches` splits "direct / dispatch fan-out / async".
   SQL = kind-filter param; bounded load unchanged. Two modes only (keep the test matrix sane).
4. **Observations** — `async_handoff` (yes, derive-time), `fire_and_forget` (yes, needs extractor `NotAwaited`),
   `cross_thread` (flag-only via `HandoffVia` provenance). Interleaving/races/ordering: never attempt.
5. **Lambdas** — (a) `InLambda` flag + single-method-group-call heuristic (`() => Foo()` → Foo is the callback)
   first; defer (b) full synthetic lambda symbols (perturbs every query + identity churn vs incremental indexing).
6. **Message/actor** — handler-as-EP via L1 rules + handoff classification of `spawn(inbox)`. Sender→handler:
   **report-only** (candidate handlers by `FirstArgumentType` ⋈ signature); NO traversable edges (unsound routing).
7. **Compat** — P1–3 need only re-`rig graph` (no Roslyn); un-re-graphed stores behave exactly as today;
   P4 columns nullable, degrade to co-location. Invariants→tests: sync ⊆ async; dead-recall preserved;
   `CHA-oracle == SQL` per mode + `narrowed ⊆ SQL`; bound superset. Add a dispatcher zoo to LegacyNet48Web fixture.

## Phases + ROI
| Phase | Effort | Re-index | Where | Value | Surface | Risk |
|---|---|---|---|---|---|---|
| **0. L1 rules** (DONE) | S | No | rules JSON | dead roots + EP labels for M3/M4/M7b + Echo Inbox + WorkflowMaster | ~30–35% (now ~+echoactor 106) | none |
| **1. Handoff classification** (co-location + `handoffDispatchers` rules) | M | No (re-graph) | derive/graph | splits the 4,503 firehose; classifies timer/actor/event callbacks (`ProcessHealthcodeQueue`→background); foundation | →~65–70% | low |
| **2. Sync-cut default + `--async`** | L | No | Domain+SQL+CLI | **the core fix** — kills false synchronous reach; `callers --roots` shows true origins; per-EP effect inventories trustworthy | same ~65–70% | **HIGH** (default-output change; equivalence tests) |
| **3. Origin EPs + `async_handoff`/`cross_thread`** | M | No | derive/render | classified origins as `from` patterns; effect tags | same, first-class | low |
| **4. Extractor facts** (`DelegateConsumer`/`InLambda`/`NotAwaited`) | L | **Yes** | Roslyn | exact classification; `event +=`; lambdas (M9/M6-lambda/M5); `fire_and_forget` | +~15–20% (lambda residual) | medium |
| **5. Message report** (no edges) | S | No | render | orientation at uncrossable boundaries | M6 .tell/queues, visibility only | none |

## Recommended sequence
**0 → 1 → 2 → 3**, validate on MedDBase (does `ProcessHealthcodeQueue` leave master-startup sync reach? does
`derive` classify the ~60 registration sites?), **then decide 4** with real residual numbers, **then 5**
opportunistically. P1–3 = ~⅔ of the surface + the entire model fix with no Roslyn/re-index — the high-ROI core.
P4 is the only re-index; ride it on the next planned re-mine, don't force one.

## Defer / never
- Full synthetic lambda symbols (large + identity churn) — heuristic covers dominant shapes; revisit on measured residual.
- Message-type→handler traversable edges (unsound routing) — report-only.
- M7a/M7c XML/reflection tasks (static-invisible; a config-mining data path exists if ever needed).
- Interleaving / races / happens-before / thread identity — outside static reach; tag, never order. Permanent no.
- A third traversal mode — `HandoffVia` rendering detail, not a mode.

## Key risks
1. Misclassification → recall loss — mitigated: curated dispatcher set only; unclassified methodGroups keep sync semantics.
2. SQL-vs-oracle equivalence — land all three (FactPathFinder + AllCallEdges/materializer + SQL filters) in ONE phase (P2), mode-parameterized invariants. (Receiver-narrowing precedent.)
3. Default-output churn — `reaches` counts drop by design; note in skill docs; offer a one-release `--legacy-handoffs` off-switch.
4. `dead` regressions — guarded by "handoff targets always roots".
5. P4 columns vs incremental-indexing — coordinate to avoid two schema migrations.

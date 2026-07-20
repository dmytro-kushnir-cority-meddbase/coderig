## Deferred-delegate → promoted async entry point

**Status:** DONE — delegate consumers/handoff EPs, lambda identity, stored delegate bindings, and the Rx
rule all shipped in the commits recorded below. The only unrelated residual, custom `DelegatingHandler` boundary
disclosure, is now tracked by [CLI paths and boundaries](../todo/cli-ux-file-paths-and-boundaries.md).

The remainder of this file is the pre-implementation design and evidence record.
**Source:** extracted from `docs/rig-review-issues.md`, 2026-06-25 (F3, VS-G1, VS-C1, VS-G10, VS-G11, VS-G13, #18b, #18c from the audit register)

### The unified theme

A delegate handed to something that **defers** its execution (scheduler ctor, background-process scheduler,
Rx Subscribe, LanguageExt `Try`) must be treated as an async entry point, NOT inlined as synchronous. Right
now rig either:
- inlines the callback as synchronous → **false positives** (VS-C1 Rx phantom), or
- silently drops the stored delegate invocation → **false negatives** (VS-G10/G11 Func-field/LanguageExt).

The approved design (2026-06-15): treat a delegate as a degenerate single-method interface — reuse the
existing single-impl/DI seam resolver. Two coordinated primitives:

1. **Mine a "delegate binding" fact** — a method-group/lambda ASSIGNED to a delegate field/property/event
   (RHS of `=`, field initializer, `+=`) → `DispatchFact(SourceMember = field/property/event DocID,
   TargetMember = bound method/synthetic-lambda id, Kind = "delegate_bind")`.  Analogous to a DI registration.
2. **Re-key the delegate invocation to the slot** — `_handler()` today emits an edge to BCL `Func.Invoke` +
   a field READ; emit / redirect it to target the FIELD/property DocID so `DispatchTargets` can look up
   `MinedDispatchBySource[slot]` → bound targets, reusing impl/override closure + `NarrowByReceiver` verbatim.

The **deferred-vs-inline split stays in the rule layer**: existing `handoffDispatchers` data marks which APIs
defer their delegate arg → promote to async EP node; otherwise inline (synchronous combinators such as
`LanguageExt.Try` that run the delegate immediately would be inlined, not promoted).

**Note: (1) without (2) is a consumer-less no-op — ship them together. Both require a re-mine.**

### Historical worklist (all delegate slices shipped; do not recreate)

**#18a — Rx `Subscribe` handoff rule (rule data, no engine change)**
Add Rx Subscribe consumers to `handoffDispatchers`. ONLY fixes the method-group form (`obs.Subscribe(Handle)`);
the dominant lambda form (`obs.Subscribe(x => …)`) is blocked until #18b. The real first-party target is
`WithGlobal.DefaultSubscriber.Subscribe`, not BCL `IObservable.Subscribe` — pin via `rig refs` before adding
the rule.

**VS-C1 — Rx `Observable.Subscribe` phantom path**
`IPersistentState.GetItem` → `AccountConfiguration.InitialiseLogger` drags in spurious transitive effects
(`http:POST` audit, `gcp_pubsub:publish`) that never fire synchronously. `AccountConfiguration.cs:210-213`.
Fix: once `Subscribe` is in `handoffDispatchers` (or a Subscribe entry point is promoted), the phantom
disappears. Sev: High.

**F3 / VS-G1 — Background ctor-arg-delegate entry points (10 DARK background EPs)**
`new BackgroundProcessSchedule(due, MethodGroup, name)` — the method-group passed as the second ctor arg is
NOT promoted as an EP; only the `SetProcessDelegate` override form is. Evidenced EPs with their effect counts:
- `ProcessHealthcodeQueue` — 269 effects incl. `soap:submit` (`Master_HealthcodeServiceImpl.cs:239`)
- `DoDueActions` — 216 effects (`PatientContact/Master.cs:165`)
- `RaiseMembershipSchemeInvoices` — 257 effects
- `CheckForZeroDebt` — 92 effects
- +6 more background dark EPs

Detector rule: method-group passed as `BackgroundProcessScheduleDelegate` ctor arg → `background` EP
(flow-insensitive; rules-only for the method-group form; lambda form needs #18b). Sev: Critical.

**#18b — Lambda identity (CORE engine gap)**
Lambdas have no DocID — they're host-context-only — so a lambda handed to a dispatcher
(`Subscribe(x => ..)`, `new Job(() => Work())`) is neither promoted NOR cut; its inner calls attribute to
the enclosing method (the source of the lambda-form Rx phantom). Fix: synthesize a stable identity for a
lambda passed as a dispatcher arg + capture its body's calls as a promotable async-EP unit.
Prerequisite for full VS-C1 fix, full VS-G1/F3 coverage, and VS-G11 LanguageExt.
Effort: meaty extractor + domain change. TDD with a synthetic playground first.

**#18c — Stored delegate-field seam (VS-G10/G11)**
`Func<T> _h = X; _h()` → effects attribute to the lambda DEFINITION site, not the invocation site.
`rig reaches Xero2Client.GetResult` shows NO xero effects; `SubscriberManager.GetEndpoint` body dropped.
Evidence: `Xero2Client.cs:95` (`request(auth)`), `SubscriberManager.cs:27`.
Fix: the two coordinated primitives above (delegate-binding fact + re-keyed invocation edge).
Note: shipping (1) without (2) is a consumer-less no-op — do them together. Lower frequency than arg-lambdas
(#18b) so lower priority despite being the "delegate-as-interface" framing.

**VS-G13 — `DelegatingHandler.SendAsync` not traversed — MOVED**
This was not a delegate-binding defect. Its boundary-disclosure requirement now lives in
[CLI paths and boundaries](../todo/cli-ux-file-paths-and-boundaries.md).

### Shipped implementation (do NOT recreate)
- #18 infra (2026-06-15): `DelegateConsumer` fact, `HandoffClassifier`, `TraversalMode.AsyncInclude`,
  `handoffDispatchers` for schedulers/IAsyncEvent/Echo/WithGlobal.Schedule, `HandoffEntryPoint` derivation,
  `rig handoffs`/`derive`. Validated live (b82ce792: 267,843 symbols, 38,513 lambda symbols, 699 delegate_bind
  facts). 18b `c10815f`/`ae81461`, 18c `77dec49`, 18a meddbase `15585db`.
- VS-C3 (SemaphoreSlim → `async_lock:acquire/release`, 2026-06-15 quick-win batch).

### Original acceptance shape (met by the shipped implementation)
- `new BackgroundProcessSchedule(…, ProcessHealthcodeQueue, …)` → `ProcessHealthcodeQueue` appears in
  `rig derive --entrypoints` and `rig reaches ProcessHealthcodeQueue` reports `soap:submit`.
- `obs.Subscribe(handler)` does NOT pull `handler`'s effects into a synchronous `reaches` of the wiring method.
- Hard-TDD synthetic playground before touching MedDBase.

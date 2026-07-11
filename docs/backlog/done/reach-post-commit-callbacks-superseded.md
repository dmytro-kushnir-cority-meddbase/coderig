## Reach post-commit callbacks (`DoWhenCommitted`) — effects fire but aren't reachable from the EP

> **SUPERSEDED / MISDIAGNOSED (verified 2026-06-23).** This item's *cause* is WRONG. `DoWhenCommitted`
> lambdas are NOT sync-cut — a `methodGroup`→lambda edge is walked synchronously (proven: in-repo unit test
> `tests/Rig.Tests/Domain/DoWhenCommittedHandoffTests.cs`, and a recursive walk from the exact
> `AbsenceRecordEntity.Save` node reaches its `~λ0/~λ1` + `LogAbsenceRecordAdded`). The webhook on the
> SaveLetter path is a plain `invocation` from `DocumentEntity.Save`, not a deferred lambda. The real cause
> of `reaches SaveLetter --only webhook,audit = 0` is the **external-virtual-override orphan** below — see
> that section. A `handoffDispatchers` rule for `DoWhenCommitted` would do nothing for it and would *reduce*
> recall (reclassifying currently-walked lambda edges to sync-cut handoffs). Do NOT build the fix described
> here. (The deferred-vs-synchronous *precision* question — should commit callbacks be modeled as deferred
> at all — is a real but separate, lower-priority semantic question.)

Surfaced closing the UX-panel "missing effects" loop (the `webhook:emit` + `audit:write` rules added to
MedDBase `rig.rules.json`, 2026-06-23): both effects are now MODELED and fire at the right sites (e.g.
`DocumentEntity.TriggerDocumentWebhook`, the `auditLogEvent.Log()` sites), but `reaches SmartLetter.SaveLetter
--only webhook,audit` is **0 even with `--async`**. Cause: on the document-save path these run inside
`DoWhenCommitted(() => …)` *deferred transaction-commit callbacks* — the effect's enclosing method is the
commit-callback lambda (`…~λ0`), which today is NOT on a handoff class rig walks, so it's sync-cut and
`--async` doesn't reach it either. So the effect is greppable store-wide but invisible from the entry point
that triggers it.

Likely fix is a **rule, not engine work** (correcting my first take): `DoWhenCommitted(Action)` is the
classic "delegate handed to a dispatcher to run later" handoff shape, so a `handoffDispatchers` entry
(per-repo data) should let the classifier promote the commit-callback lambda to a walked handoff edge —
making its effects reachable under `--async`, tagged as scheduled, exactly like timer/actor/event callbacks.
TO VERIFY before building: (a) confirm `DoWhenCommitted`'s registration is co-located-methodGroup/lambda
shaped (what `handoffDispatchers` matches) vs. needing a delivery-rule or genuine engine support; (b) decide
the semantics tag — it's deferred-but-SAME-THREAD (runs at commit, not cross-thread), so it should walk under
`--async` but ideally not be mislabelled `cross_thread`. Scope: start with the `DoWhenCommitted` dispatcher
on the MedDBase store, calibrate (does `SaveLetter --async` then reach the audit/webhook?), then generalize.

# BUG: `path`/`callers` over-reach via `nameof` and event-subscription edges

**Status:** open · **Severity:** high (false positives in `path`/`callers` on menu-driven / event-wired codebases) · **Found:** 2026-06-16 (spicy-endpoint audit, `InvoiceMain.AddBillingItem`)

## Symptom
`rig path` and `rig callers --entrypoints` report reaches that `rig reaches`/`tree` correctly do **not**. Concretely, both claim `InvoiceMain.AddBillingItem` reaches the Xero accounting API (`callers "Xero2.BeforeDebtorInvoiceSent" --entrypoints` lists ~161 entry points, most spurious), but `reaches`/`tree` on the same store show no such reach. Adding a billing item does not post to Xero — only the invoice-*Send* action does.

## Root cause
`path`/`callers` traverse two edge kinds as if they were synchronous calls. The effect engine (`reaches`/`tree`) excludes both; the forward/reverse path engines don't.

### (a) `nameof(Method)` reference edges — pure noise, must go
`FactExtractor.ClassifyReference` (`Rig.Analysis/Extraction/FactExtractor.cs:1006-1019`) maps any non-invoked `IMethodSymbol` name to `RefKinds.MethodGroup`:
```csharp
IMethodSymbol => IsInvoked(name) ? RefKinds.Invocation : RefKinds.MethodGroup,
```
There is **no `nameof` detection**. So `nameof(MarkSentMakePayment)` inside a static menu map —
`ActionItem("Make Payment", nameof(MarkSentMakePayment))` (`InvoiceMain.cs:668-674`) — emits a `methodGroup` edge `F:PaymentsMenuMap → MarkSentMakePayment`. But `nameof` only extracts a **string**; the method is never converted to a delegate and never invoked through that reference. It is not a call, not a handoff, not a delegate binding — it should not be a call-graph edge at all.

**Fix:** in extraction, detect when the name is the argument of a `nameof(...)` expression and classify it as a benign reference (e.g. a non-traversable `nameRef`/`typeUse`-like kind), not `MethodGroup`. Roslyn: the name's enclosing `InvocationExpressionSyntax` has expression text `nameof` (a contextual keyword); or `model.GetConstantValue(invocation).HasValue`. Drop these from the call/dispatch graph. Regression test: a `nameof(M)` in a field/initializer must not make `M` reachable from the enclosing type.

### (b) Event-subscription method-group edges — align with `reaches`/`tree`
`proxy.Changed += new BillingItemListChanged(BillingItemsChangedEventHandler)` (`InvoiceMain.cs:148`) emits a `methodGroup` edge with a `DelegateConsumer` (the event). `ReachesCommand`/`TreeCommand` call `FactPathFinder.MarkEventSubscriptionHandoffs` (reclassifying these `+=` edges to `handoff`, which is sync-cut by default and surfaced under `--async`). `PathCommand`/`CallersCommand` do **not** — so they walk the handler as a synchronous call.

This one is **borderline legitimate**: the handler genuinely runs later, via the event — i.e. it's an async handoff, not noise. So the fix is not to delete it but to make `path`/`callers` treat it like `reaches`/`tree`: apply `MarkEventSubscriptionHandoffs` so event handlers are sync-cut by default and included under `--async`.

**Fix:** call `FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context))` in `PathCommand` and `CallersCommand` before the traversal, mirroring `ReachesCommand.cs` / `TreeCommand.cs`. Then a `+=` handler is reachable in `path`/`callers` only under `--async`, consistent with the effect engine.

## Why it matters
On a menu-driven Pages codebase (static `DomMap` action maps full of `nameof`, plus pervasive `+=` event wiring), these edges produce large false-positive reach sets — e.g. nearly every `InvoiceMain` action appears to reach Xero because they all touch `BuildToolbar`/`BuildPaymentsMenu` → the `nameof`-laden menu field → the real Send path. This silently inflates `callers --entrypoints` (blast-radius / "who reaches this") and `path` results, the two commands whose entire job is precision.

## Fix plan
1. **(a) `nameof`** — extraction: classify `nameof`-argument method/type references as non-call references; exclude from call/dispatch edges. (Clear bug; do first.)
2. **(b) events** — apply `MarkEventSubscriptionHandoffs` in `PathCommand`/`CallersCommand` so event handoffs are sync-cut by default, `--async`-only — matching `reaches`/`tree`.
3. Verify: `callers "Xero2.BeforeDebtorInvoiceSent" --entrypoints` no longer lists `AddBillingItem` (and the ~160 other non-Send `InvoiceMain` actions); `path "InvoiceMain.AddBillingItem" "Xero2.CreateInvoices"` returns no path (sync) / only under `--async` if an event chain genuinely connects them.

Files: `Rig.Analysis/Extraction/FactExtractor.cs` (ClassifyReference), `Rig.Cli/Commands/PathCommand.cs`, `Rig.Cli/Commands/CallersCommand.cs`.

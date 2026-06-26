## `callers`/`path` over-reach via `nameof` edges — extraction fix

**Status:** todo — open correctness bug (high severity)
**Source:** promoted from `docs/bug-callers-path-overreach.md` (open half — the `nameof` fix; the
event-subscription half was partially shipped). 2026-06-25.

### Symptom

`rig path` and `rig callers --entrypoints` report reaches that `reaches`/`tree` correctly do NOT.
Example: `callers "Xero2.BeforeDebtorInvoiceSent" --entrypoints` lists ~161 entry points (most spurious)
because `InvoiceMain` actions touch a `nameof`-laden menu map → the real Send path. `reaches`/`tree` show no
such reach. Adding a billing item does NOT post to Xero.

### Root cause: `nameof` reference edges

`FactExtractor.ClassifyReference` (`Rig.Analysis/Extraction/FactExtractor.cs:1006-1019`) maps any
non-invoked `IMethodSymbol` name to `RefKinds.MethodGroup`:
```csharp
IMethodSymbol => IsInvoked(name) ? RefKinds.Invocation : RefKinds.MethodGroup,
```
There is **no `nameof` detection**. `nameof(MarkSentMakePayment)` inside a static menu map
(`ActionItem("Make Payment", nameof(MarkSentMakePayment))`, `InvoiceMain.cs:668-674`) emits a `methodGroup`
edge `F:PaymentsMenuMap → MarkSentMakePayment`. `nameof` only extracts a **string**; it is not a call, not
a handoff, not a delegate binding — it must not be a call-graph edge.

### Fix

In `FactExtractor.ClassifyReference`: detect when the name is the argument of a `nameof(...)` expression
and classify it as a non-traversable reference kind (e.g. `nameRef`), not `MethodGroup`. Roslyn detection:
the name's enclosing `InvocationExpressionSyntax` has expression text `nameof` (a contextual keyword), or
`model.GetConstantValue(invocation).HasValue`.

Regression test: a `nameof(M)` in a field/initializer must NOT make `M` reachable from the enclosing type.

Files: `Rig.Analysis/Extraction/FactExtractor.cs` (`ClassifyReference`).

### Out of scope here: event-subscription alignment

The other half of the original bug — making `PathCommand`/`CallersCommand` apply
`MarkEventSubscriptionHandoffs` to sync-cut `+=` event handlers the same way `ReachesCommand`/`TreeCommand`
do — was noted in the original bug file. If not yet shipped, it belongs in a separate slice or here as a
follow-on; it is NOT the `nameof` extraction fix.

### Verification

`callers "Xero2.BeforeDebtorInvoiceSent" --entrypoints` no longer lists `InvoiceMain.AddBillingItem` (and
the ~160 other non-Send actions); `path "InvoiceMain.AddBillingItem" "Xero2.CreateInvoices"` returns no
sync path.

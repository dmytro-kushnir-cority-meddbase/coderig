# `callers`/`path` over-reach via `nameof` edges — extraction fix

**Status:** DONE — shipped `b5c1bd33` 2026-06-16 ("fix(query): stop path/callers over-reaching via
nameof + event edges"), **including the event-subscription half** this doc listed as out-of-scope:
`PathCommand`/`CallersCommand` apply `MarkEventSubscriptionHandoffs` in the same commit.
`ClassifyReference` checks `IsNameOfArgument` first and emits the non-traversable `RefKinds.NameOf`
(covers dotted `nameof(A.B.Method)` via the ancestor walk). Regression tests:
`FactExtractorCaptureTests.cs:801-862` (field-initializer `nameof(MarkSent)` emits no methodGroup edge;
dotted `nameof(Repo.Fetch)` likewise). **Verified on the re-indexed MedDBase store** (in the commit):
`callers "Xero2.BeforeDebtorInvoiceSent" --entrypoints` 161 → 89; `AddBillingItem` no longer falsely
reaches Xero; the real invoice-Send path still does.

**Staleness note:** this file was promoted to the backlog on 2026-06-25 from
`docs/bug-callers-path-overreach.md` (now `docs/archive/`), whose "open half" framing predated the
2026-06-16 fix — the promotion copied an already-resolved snapshot. Moved to done 2026-07-02.

---

Original writeup below (pre-fix snapshot):

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

### Verification

`callers "Xero2.BeforeDebtorInvoiceSent" --entrypoints` no longer lists `InvoiceMain.AddBillingItem` (and
the ~160 other non-Send actions); `path "InvoiceMain.AddBillingItem" "Xero2.CreateInvoices"` returns no
sync path.

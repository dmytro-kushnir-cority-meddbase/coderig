# Constructor-initializer edges missing from call graph (`: this(...)` / `: base(...)`)

**Status:** fix SHIPPED 2026-07-02 (`OnConstructorInitializer` in `FactExtractor`, `CtorInitializerEdgeTests`,
verified end-to-end on a scratch solution: `callers` climbs the chain to the `new` site and a `: base` derived
ctor). **Remaining:** MedDBase re-index + the InvoiceEntity verification below — extraction change, the
existing store predates it. Move to `done/` after that.
**Found:** 2026-07-02 (verified on MedDBase) · **Family:** extraction / call-graph recall
**Source:** promoted from the `C:\Git` memory backlog (§5b) — the repo backlog is the canonical home.

## Symptom

`: this(...)` / `: base(...)` ctor chaining is NOT a call edge. Repro (MedDBase): the `InvoiceEntity`
ctor chain (3-arg → 4-arg → 5-arg → 6-arg → `Initialise` → `GroupByAccount`,
InvoiceEntity.cs:102/169/189): `rig callers "InvoiceEntity.GroupByAccount"` returns only 8 methods —
misses `Payments.MakePaymentFee` (Payments.cs:298) and `WizardBase` charging (WizardBase.cs:1155),
both of which call chained overloads. **False-negative reverse reachability through any ctor overload
chain** — forward reach (`reaches`/`tree`) through a `new X(...)` similarly stops at the invoked
overload and never walks into the chained-to ctor's body.

## Root cause

`FactExtractor` records object creations (`OnCreation`, `RefKinds.Ctor` → a call edge per
`Reads.LoadFactGraphAsync` / `FactGraphProjection`), but a `ConstructorInitializerSyntax`
(`: this(...)` / `: base(...)`) carries no creation/invocation syntax the existing passes see —
the SimpleName pass never visits it (there is no name node for the target ctor), so no reference
fact is emitted and the declaring ctor → chained ctor edge is missing.

## Fix (extraction-side — forces a MedDBase re-index)

In the `root.DescendantNodes()` walk, handle `ConstructorInitializerSyntax`: resolve the target ctor
via `model.GetSymbolInfo(initializer)` and `AddReference` with `RefKinds.Ctor`, enclosing = the
declaring ctor (`EnclosingSymbolId`), mirroring `OnCreation`. Both `this` and `base` initializers are
exact, non-dispatching calls (CIL `call`), like a `base.M(...)` invocation.

Out of scope / residual (disclose, don't build): **implicit** base-ctor calls (a ctor with no
initializer still runs `base()`; a type with no declared ctor has no syntax at all) — those need a
symbol-level pass, not a syntax case, and the verified false negatives are all explicit chains.

## Verification

- Fixture: ctor overload chain `A(x) : this(x, 0)` → `A(x, y)` → body calls `M()`; assert the
  extractor emits a ctor ref `A.ctor(x)` → `A.ctor(x,y)`, and `callers`/reverse reach of `M` includes
  the method containing `new A(x)`.
- MedDBase (after re-index): `rig callers "InvoiceEntity.GroupByAccount"` includes
  `Payments.MakePaymentFee` and the `WizardBase` charging call site.

Until shipped+re-indexed: treat `callers` through ctors as under-approximate; grep source for
`new <Type>(` call sites.

Files: `src/Rig.Analysis/Extraction/FactExtractor.cs` (descendant walk + a new `OnConstructorInitializer`).

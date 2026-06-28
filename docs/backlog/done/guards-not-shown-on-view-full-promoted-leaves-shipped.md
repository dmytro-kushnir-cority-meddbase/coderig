# `--guards` not rendered on `--view full` promoted leaves (external library calls + effect leaves)

**Status:** SHIPPED 2026-06-28 (`fac662ca`) — guards thread through SymbolRef/FactInvocation/DerivedEffect to both leaf formatters; validated on the MedDBase store (switch-arm externals + guarded `Dictionary.Add`/`String.Substring`; must-run io/lock leaves stay bare). · **Priority: MEDIUM** · **Found:** 2026-06-28 (dogfooding `tree MMS.ClassFactory.GetAssembly --view full --guards`) · **Family:** rendering / disclosure-completeness

## Residual follow-ups (NOT shipped)
- **Render-quality nit:** a switch-derived guard's predicate text is the bare case constant (`"entry"`, `!"executing"`), not `name == "entry"` / `name != "executing"`. The switch lowers to per-case equality and `FactExtractor.FullConditionText` returns the constant unchanged. Reconstruct `<governing> == <constant>` for switch guards so the render reads as a predicate.
- **Ctor + field-access effect leaves stay guardless:** the invocation and throw arms carry guards now, but ctor effects (`OnCreation` is structurally blind — see the alloc-detector card) and static-field-access effects don't. Wiring those needs the ctor/field-access refs to carry `EnclosingGuards` (extraction change → re-index).
**Related:** [[branch-aware-effects-shipped]] (the feature) · [[guard-set-direct-vs-transitive-control-dependence]] (sibling guard-disclosure gap)

## Symptom
In `--view full`, the `·` library-call leaves and `↯`/`📁`/`🔒` effect leaves **never** show the `⎇ [guard]`
glyph, even when the call/effect is genuinely guarded. Only **resolved in-source call edges** carry it.
Observed on `ClassFactory.GetAssembly`: the `switch(name)` arms (`· Assembly.GetEntryAssembly :72` etc.,
each gated by a `case`) render bare, while the same method's in-source `IAssemblyProvider.Provide`
(under `if (GlobalSettings.AssemblyProvider != null)`) correctly shows `⎇ […]`.

NOT a flag artifact: `--raw` (drops opaque) and `--plain` (drops connectors) are irrelevant; this is the
`--view full` leaf-PROMOTION path.

## Proven NOT to be an extraction gap
Production `ComputeGuards` computes the switch-case guards correctly (spike, `CfgSpike`-style dump):
`case "entry" → "entry"=True`, `case "calling" → "calling"=True`, `default → "calling"=False`. So the guard
**exists on the underlying `ReferenceFact.EnclosingGuards`** — it's just not plumbed to the leaf node.

## Root cause (CONFIRMED against code)
`FactGraphProjection` (`Rig.Domain/Functions/FactGraphProjection.cs:28-34`) keeps only `TargetInSource`
call edges — **BCL/runtime/external targets are filtered out of the guard-carrying `CallEdge` list**
("leaves that add width, not reach"). `EnclosingGuards` rides on `CallEdge` (line 50), so once the external
edge is dropped, its guard is gone from the graph. `--view full` then re-introduces those targets as leaf
nodes from a DIFFERENT projection (effects + unresolved-library-call promotion) that never carried
`EnclosingGuards`. Hence no `⎇`.

## Scope of fix — two sub-cases, different effort
1. **External library-call leaves (cheaper).** The guard is on the originating `ReferenceFact`; it's dropped
   at the `FactGraphProjection.cs:34` filter. Thread it onto the promoted leaf: either keep a guard-bearing
   side-table of external refs at projection time, or carry `EnclosingGuards` onto the leaf `TraceNode` when
   `--view full` promotes an unresolved/library call. Data already exists; it's a plumbing reconnect.
2. **Effect leaves (`io:read`/`db:`/`lock:`… — more involved).** Effects are derived (`FactEffectDeriver`)
   keyed to the **enclosing symbol id**, not to a specific call-site reference, so there's no direct
   `EnclosingGuards` to read. Associating an effect with the guard of the call-site that produced it needs a
   reference↔effect back-link (the effect's originating `ReferenceFact`). Bigger; do after #1.

## Render-quality nit (fold in while here)
Even once plumbed, a switch guard's predicate text is the **bare case constant** (`"entry"`), not
`name == "entry"` — the switch lowers to per-case equality and `FactExtractor.FullConditionText` returns the
constant unchanged. Consider reconstructing `<governing> == <constant>` for switch-derived guards so the
render reads as a predicate, not a dangling literal.

## Why MEDIUM
The entire point of `--guards` is to distinguish conditional effects from the must-run spine. In `--view
full` (the per-effect inspection view) the effect/library leaves are exactly what users read — and they all
look must-run today. A guarded `io:read`/`db:write` shown as a bare leaf is an actively misleading
disclosure, not just an omission. #1 (external calls) is a small reconnect; #2 (effect leaves) is the part
that actually closes the misleading-effect case.

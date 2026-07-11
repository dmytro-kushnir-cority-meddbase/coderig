# Fix options: `event_raise` delivery-edge over-approximation

**Status:** ✅ IMPLEMENTED (Options A + D) on branch `fix/event-raise-overapproximation`.
- `CallEdge.DeliveryPrecision` (`exact`|`fanout`) stamped by `AddDeliveryEdges` (single-handler channel ⇒ exact, multi ⇒ fanout); persisted in `call_edges` + round-tripped by `SqlReachability`.
- New `TraversalMode.AsyncExact` is the `--async` default — gate `FactPathFinder.CutsHandoff` cuts only `fanout` delivery; `--include-delivery` ⇒ `AsyncInclude` (walks everything). `event_cycle` reads edges directly, unaffected.
- Tests: `tests/Rig.Tests/Domain/DeliveryFanoutPrecisionTests.cs`. Original options analysis retained below.

---
**Repro/evidence:** `C:\Git\meddbase-analysis\BUG-rig-event-raise-overapproximation.md` — on MedDBase, 22/22 sampled `--async` paths whose reach depended on an `event_raise` hop were FALSE; 5/5 `event` (registrant→handler) controls were REAL.

## Root cause (precise)

Event delivery edges are built by a symbol-only channel join:

- `Reads.LoadDeliverySitesAsync` (`src/Rig.Storage/Queries/Reads.cs:520-553`) emits one `DeliverySite` per event-read ref with **`IdentityToken = the event symbol DocID`** (`E:…`) and `Role = ByColocation`. Every `someEvent += H` and every `someEvent?.Invoke(...)` reads the same symbol.
- `FactPathFinder.AddDeliveryEdges` (`src/Rig.Domain/Functions/FactPathFinder.GraphShaping.cs:81-191`) groups handlers into `handlersByChannel[(Tag, IdentityToken)]` and then, for **every** producer (raise) on that channel, adds a handoff edge `producer → handler` for **every** handler in the channel.

Because the channel key is the event *symbol* only, all registrations of that symbol **across every instance and every call site** collapse into one channel. A single raise inside a generated proxy (`AccountSearchDialog_RequestProxy.g.cs`) therefore fans out to all **64** `AccountSelected += …` subscribers project-wide. The header comment calls this "EXACT (a raise of E reaches precisely E's subscribers)" — true per *symbol*, but blind to the *instance* a given caller wired its handler to. .NET instance events have a per-instance invocation list; rig models one global list per event declaration.

## The asymmetry that makes this fixable

There are two edges in play, and only one is wrong:

| Edge | Built by | Shape | Verdict |
|---|---|---|---|
| `event` | `MarkEventSubscriptionHandoffs` (`GraphShaping.cs:25-50`) | **Registrant → Handler** (the `+= H` methodgroup at the subscribing caller, reclassified to handoff) | EXACT (5/5 real) |
| `event_raise` | `AddDeliveryEdges` | **Producer(raise) → every Handler on the symbol** | OVER-APPROX (22/22 false) |

For the dominant pattern — a component subscribes to an instance event it *also triggers* (open dialog → it later raises) — reachability is **already captured exactly** by the `event` edge `Registrant → Handler`. The `event_raise` edge contributes reach *only* for a genuinely decoupled publisher (a raise reached on a path that does **not** pass through the registrant). That decoupled case is rare and is precisely the source of every observed false path.

Critically, **`event_raise` edges are consumed by two independent things**:
1. The reachability/attribution traversal (`reaches`/`callers`/`path`/`tree`) under `--async` — gate at `FactPathFinder.GraphIndex.cs:52-53` cuts all handoffs in `SyncCut`, walks all under `--async`, **without distinguishing `event` from `event_raise`**.
2. `FactCycleDeriver.DeriveEventCycles` (`src/Rig.Domain/Functions/FactCycleDeriver.cs:89-129`) — iterates `graph.CallEdges` **directly** (not via the traversal gate) to find `event_cycle` re-entrancy hazards. It genuinely needs producer→handler edges.

So the bug is really: **edges built for cycle detection leak into reachability**, where the sound `event` edge already covers the common case.

## Options

### Option A — Quarantine `event_raise` from reachability; keep it for cycle detection (minimal, recommended baseline)
Stop the reachability traversal from walking `event_raise` (and the similarly heuristic `actor_tell`) edges by default; keep them in `graph.CallEdges` so `FactCycleDeriver` is unaffected.

- Change: at the `--async` walk gate (`FactPathFinder.GraphIndex.cs:52`), skip handoff edges whose `HandoffDispatcher` is in a "delivery/low-confidence" set (`event_raise`, `actor_tell`) unless a new opt-in flag (`--include-delivery` / `--imprecise`) is passed. Continue walking `event` and the curated dispatcher handoffs (`repeating.schedule`, spawns, etc.).
- Effect: eliminates all 22/22 false paths. Dialog-pattern reach is retained via the `event` edge. Loses *default* reach for truly decoupled publishers under `--async` (was wrong anyway; recoverable with the flag).
- Cost: ~10 lines + a flag. No schema change. No effect on `event_cycle`.

### Option D — Single-subscriber exactness gate (refinement on A, recommended together)
When a channel has exactly **one** handler, `producer → handler` is unambiguous and exact; when it has **many**, the producer→handler set is the over-approximation. Stamp that precision on the edge at build time and let the traversal trust the exact ones.

- Change in `AddDeliveryEdges` (`GraphShaping.cs:172-186`): when emitting edges, record precision = `exact` if `channelHandlers.Count == 1`, else `fanout`. (Needs a small carrier — see "Schema" below.)
- Traversal walks `exact` delivery edges under `--async`; excludes `fanout` ones by default (Option-A treatment), opt-in via flag.
- Effect: keeps exact single-subscriber delivery reach (e.g. a 1-handler event) while quarantining only the genuinely ambiguous fan-out. `AccountSelected` (64), `PaymentMade` (13), `SaveClicked` (2) → all `fanout` → excluded by default.
- Cost: small. Most precise honest option.

### Option B — Scope the channel to the registrant (rejected as primary)
Make `IdentityToken = (event symbol, registrant scope)`. But the producer (raise) lives in generated proxy code with no static tie to a registrant, so it would produce **zero** edges for the proxy pattern — i.e. it degenerates to Option A for exactly the cases that matter, with more complexity and less predictability.

### Option C — Receiver-type narrowing (rejected as primary; possible secondary filter)
Require the raise's `ReceiverType` (already mined, `CallEdge.ReceiverType`) to equal the registration's. For `proxy.AccountSelected += H` and the proxy's internal `AccountSelected?.Invoke`, **both receivers are the same proxy type**, so this does not separate the 64 callers (the worst fan-out is same-type). It would trim only cross-*type* leaks; marginal. Could be layered as an extra filter but does not fix the dominant case.

## Recommendation

Ship **A + D**: add a precision marker to delivery edges (`exact` when the channel has a single handler, `fanout` otherwise); the reachability traversal walks `exact` delivery edges under `--async` and excludes `fanout` ones unless `--include-delivery` is passed; `FactCycleDeriver` keeps consuming all delivery edges unchanged. This removes every observed false path, preserves exact delivery reach where it exists, keeps the sound `event` (registrant→handler) reach, and leaves `event_cycle` detection intact.

### Schema note
`CallEdge` (`src/Rig.Domain/Data/Facts.cs:159`) has `HandoffDispatcher` but no precision field. Two ways to carry precision without a wide change:
- Add an optional `string? DeliveryPrecision = null` to `CallEdge` (cleanest; one column / codec field — check `TreeCacheCodec`, `GraphMaterializer`, storage round-trip).
- Or encode in the dispatcher tag (`event_raise` for fanout vs `event_raise_1` for single-subscriber) — no schema change but stringly-typed and leaks into `DefaultDeliveryDispatchers`/rendering; less clean.

## Related (separate ticket)
The compounding `IDetailDialogProxy.ShowDialog [impl-dispatch ×14]` fan-out (an interface call fanned to all 14 dialog implementations) is a distinct CHA over-approximation in the dispatch layer, not the delivery join. It should be narrowed by `ReceiverType` (the constructed proxy type) like other virtual/interface dispatch — track separately.

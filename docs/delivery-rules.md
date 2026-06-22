# Delivery rules — publish→consumer edges as composable data, not coded resolvers

**Status: design spec (not yet built).** Sequenced AFTER FR-10 (`event_cycle`) lands and is committed —
they share `FactPathFinder.GraphShaping.cs` and the `derive` graph-build snippet, so the refactor edits on
top rather than concurrently. See [hazards.md](hazards.md) for the delivery-edge conjecture this formalises.

## The problem this fixes

The graph carries publish→consumer **delivery edges** (a raise delivers to an event's subscribers; an actor
`tell` delivers to the handler spawned under that process name) — call edges no syntactic call records,
modeled as `handoff` edges (sync-cut by default, walked under `--async`, cycle-visible). Today they are
produced by **two bespoke coded resolvers** in `FactPathFinder.GraphShaping.cs`:

- `AddEventDeliveryEdges` — C# events (identity = the event symbol).
- `AddActorDeliveryEdges` — Echo actors (identity = a process-name string on a static registry).

The actor resolver hard-codes a **MedDBase-/Echo-specific convention** (the channel identity is a member path
on a parallel static class). That is niche third-party mechanics living in the core engine — the exact thing
rig otherwise pushes into **data** (effect detectors, handoff dispatchers, render rules, the actor *methods*
themselves are all rules). The two resolvers are also, on inspection, **the same algorithm**: a registration
site co-located with a method-group edge yields `(identity → handler)`; a producer site bearing the same
identity gets a handoff edge to that handler. The only per-mechanism differences are **what the identity token
is** and **what dispatcher tag to stamp**.

## The principle

Ship a **fixed library of general primitives**; let a user declare a mechanism by **composing** them in a rule.
MedDBase contributes an `echo-actor` rule entry and adds **zero engine code**. Events and MediatR become rule
entries too — the two coded resolvers delete. The engine keeps exactly **one** coded mechanism: resolve sites
to identity **tokens** via the named primitives, **join on token equality**, emit tagged handoff edges.

```
                 ┌───────────────── rules (DATA) ─────────────────┐
 producer sites ─┤ match → identity(source, resolver) → token      ├─┐
 registr. sites ─┤ match → identity(...) → token, handler-locator   │ │   one coded join:
                 └────────────────────────────────────────────────┘ ├─► group by token → for each
                                                                      │   producer, edge → each handler
                 emit: { tag, confidence } ───────────────────────────┘   (Kind="handoff", HandoffDispatcher=tag)
```

## The primitive library

A delivery rule is a composition along four axes plus an emit clause. Every primitive is **codebase-agnostic**
— none knows what a "process" or an "event" is.

### 1. `match` — which sites are producers / registrations
Reuses the **existing `FactEffectRule` match vocabulary** verbatim (consistency + zero new matcher code):
`methods`, `declaringTypes`, `receiverTypes`, `declaringTypeNameEndsWith`, `declaringTypeBaseTypes`,
`minArguments`. Plus two language-construct predicates for things that aren't ordinary calls:

| `match` | Matches |
|---|---|
| (the effect-rule clause) | call sites — `Process.tell(...)`, `mediator.Publish(...)`, `spawn(name, H)` |
| `event-raise` | a C# event invocation (`SomeEvent?.Invoke(...)`) |
| `event-subscribe` | a C# event subscription (`SomeEvent += H`) |

### 2. `identity.from` — where the channel identity is carried
`arg[N]` · `event-symbol` · `type-arg[N]` · `declaring-type` · `receiver-type`.

### 3. `identity.resolve` — how to fold the source into a comparable token
The **resolution ladder**, precision-ordered. Free = derivable from facts already captured; ✚ = needs an
extraction add (re-index):

| `resolve` | Token | Cost |
|---|---|---|
| `symbol` | the source's DocID symbol (exact) | free — events already mined |
| `type` | the static type (exact) | free — `argument_type` / `type_argument` captured |
| `literal` | inline literal at the call site | free — call-site templates captured |
| `nameof` | `nameof(X)` → the simple name | free (small extraction note; mostly captured) |
| `enum-member` | the enum member symbol | ✚ capture enum-member refs |
| `field-literal` | a referenced `const`/`static readonly` field's **initializer literal** | ✚ capture field-initializer literals |
| `path` | the raw member-path expression (today's crude actor behaviour) | free — fallback rung, low confidence |

`field-literal` is the rung that gets the actor resolver **off** the MedDBase static-registry convention: a
`spawn(Names.Worker, …)` resolves to the *value* `"worker"`, not to the path `Names.Worker`. It is a general
resolver that happens to be what MedDBase needs — not Echo support.

### 4. `handler` — how to find the consumer method at a registration
| `handler` | The consumer is… |
|---|---|
| `colocated-methodgroup` | the method-group target at the registration site (events `+= H`, actor `spawn(name, H)`) |
| `declaring-method` | the registration's own enclosing method (handler-as-method) |
| `interface-impl` | implementers of an interface (MediatR `INotificationHandler<T>`) |

### 5. `emit`
`tag` — the `HandoffDispatcher` label stamped on the edge (`event_raise`, `actor_tell`, `mediatr_publish`, …;
also the `--async` provenance shown as `⤳ via <tag>`). `confidence` — `exact` (symbol/type joins) or
`heuristic` (value joins; rendered `~heuristic`, and it sets the FR-10 cycle confidence tier).

## Rule schema

A new `deliveryRules` array in `rig.rules.json`, cascaded like the other rule families and surfaced on
`RuleSet.Delivery`. One entry = one mechanism:

```jsonc
{
  "id": "echo-actor",
  "tag": "actor_tell",
  "confidence": "heuristic",
  "producer": {
    "methods": ["tell", "tellSystem", "ask", "askAsync", "askIfAlive"],
    "declaringTypes": ["Echo.Process"],
    "identity": { "from": "arg", "index": 0, "resolve": "field-literal" }
  },
  "registration": {
    "methods": ["spawn", "spawnUnit", "spawnMany", "register"],
    "declaringTypes": ["Echo.Process"],
    "identity": { "from": "arg", "index": 0, "resolve": "field-literal" },
    "handler": "colocated-methodgroup"
  }
}
```

The two coded resolvers collapse into builtin rule entries on the same engine:

```jsonc
{ "id": "csharp-event", "tag": "event_raise", "confidence": "exact",
  "producer":     { "match": "event-raise",     "identity": { "from": "event-symbol", "resolve": "symbol" } },
  "registration": { "match": "event-subscribe", "identity": { "from": "event-symbol", "resolve": "symbol" },
                    "handler": "colocated-methodgroup" } },

{ "id": "mediatr", "tag": "mediatr_publish", "confidence": "exact",
  "producer":     { "methods": ["Publish", "Send"], "declaringTypes": ["MediatR.IMediator", "MediatR.IPublisher", "MediatR.ISender"],
                    "identity": { "from": "arg", "index": 0, "resolve": "type" } },
  "registration": { "implementsInterface": "MediatR.INotificationHandler`1",
                    "identity": { "from": "type-arg", "index": 0, "resolve": "type" }, "handler": "interface-impl" } }
```

> `http-route` (the documented but undeveloped cross-repo/RPC contract item) is a future entry: producer =
> the client call with `identity.from: arg[N], resolve: literal` (the URL/route string), registration = the
> route attribute / mapping with the same token. `confidence: heuristic` (interpolated client URL vs route
> template) — exactly the conjecture's http row.

## The engine join (the one coded mechanism)

Replaces `AddEventDeliveryEdges` + `AddActorDeliveryEdges` with a single
`AddDeliveryEdges(FactGraphData graph, IReadOnlyList<DeliverySite> sites)`. The Storage loader resolves each
matched site to a `DeliverySite(Caller, FilePath, Line, IdentityToken, Role, Tag, Confidence, HandlerLocator)`
using the named primitives; the Domain function is pure and framework-blind:

```
group registration sites by (rule, IdentityToken)        // identity → handler(s) via the handler-locator
for each producer site:
    handlers = registrations[(rule, producer.IdentityToken)]   // join on token equality
    for each handler:
        add CallEdge(producer.Caller → handler, Kind="handoff",
                     HandoffDispatcher=Tag, FilePath/Line = producer site)   // deduped per (caller,callee)
```

Identity tokens that don't resolve (`resolve` rung fails — interpolated/computed/runtime-bound) yield **no
edge** and are disclosed (the conjecture's "sometimes a value" residual), never a spurious bare-token join —
preserving the current precision gate (a non-resolving / bare-variable identity does not join).

## Where the niche actually goes

| Concern | Today | After |
|---|---|---|
| Echo's `tell`/`spawn` method names | data (`actor:*` effect rules) | data (`echo-actor` delivery rule) |
| "identity is a process-name on a static registry" | **coded** in `AddActorDeliveryEdges` | data (`resolve: field-literal`) |
| C# event raise/subscribe semantics | **coded** in `AddEventDeliveryEdges` | data (`csharp-event` builtin rule) + the `event-raise`/`event-subscribe` match primitives |
| the producer→handler join | duplicated in both functions | one coded `AddDeliveryEdges` |
| value/symbol/type resolution | implicit per resolver | the shared primitive ladder |

Engine code that remains: the join + the resolver primitives. Both codebase-agnostic. Onboarding a new
framework = a new rule entry.

## Interaction with FR-10 (`event_cycle`)

None at the seam: the cycle detector reads `handoff` edges by their `HandoffDispatcher` tag and is agnostic to
how they were produced. It keeps working unchanged through the refactor. The delivery rule's `confidence`
feeds the cycle's confidence tier (a cycle through any `heuristic` delivery edge is disclosed `low`).

## Build order & extraction

1. **(prereq)** FR-10 committed.
2. **Refactor (no re-index):** add `RuleSet.Delivery` + schema/loader; add the `DeliverySite` shape and the
   resolver primitives that are *free* (`symbol`, `type`, `literal`, `path`, `colocated-methodgroup`,
   `interface-impl`); add the general `AddDeliveryEdges`; re-express events + actors as builtin rules; delete
   the two coded resolvers; update the `GraphMaterializer` + `derive` call sites to the general join. Events
   keep exact recall; actors keep today's `path`-rung behaviour until step 3.
3. **Extraction add (re-index):** capture `field-literal` (and `enum-member`) initializer values; switch the
   `echo-actor` rule to `resolve: field-literal`. This is the rung that generalises actor identity off the
   member-path convention and is the prerequisite for real MedDBase actor coverage (the `ProcessNames.X =
   "..."` literals become joinable values). Calibrate FP rate on the MedDBase store before on-by-default.

## Deferred / open

- **MedDBase's `ProcessId`↔`ProcessName` split is its own local wrinkle** and only half-solved by
  `field-literal`: `spawn`/`register` carry the literal-bearing `ProcessNames.X`, but `tell` carries
  `ProcessDns.X` — a `ProcessId` with no literal (runtime-bound by `register`). A symmetric all-string Echo
  app needs no special handling; MedDBase needs the `register(name)→pid` binding modeled (a future
  `identity` source that follows an assignment), or a `path`-rung fallback. Disclose, don't over-fit.
- **Registry auto-discovery** (a stretch): infer the name registry — any static class whose string-const
  fields are used as a delivery `identity.from: arg` — instead of naming `declaringTypes`. Keeps even the
  registry out of the rule. Speculative; revisit after `field-literal` ships.
- **Delivery edges in the in-memory `LoadFactGraphAsync`+`ShapeGraph` path:** today they're baked only into
  persisted `call_edges` (and reconstructed ad hoc by `derive`/FR-10). The general join makes it cheap to
  apply them uniformly wherever the shaped graph is built — worth folding into `ShapeGraph` so impact's
  per-EP reach and the EF-fallback traversals see delivery edges too (a latent gap noted during FR-10).

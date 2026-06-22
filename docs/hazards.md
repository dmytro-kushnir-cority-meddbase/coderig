# Hazards — rig's operational-risk detection layer

Abstract design. The codebase-specific grounding (the real production-revert corpus, the MedDBase
validation, the prioritised roadmap) lives **outside this repo** in the analysis workspace
(`meddbase-analysis/docs/`): `rca-corpus-meddbase.md` (per-incident RCAs) and `backlog-bug-detection.md`
(the grounded roadmap). This file is the timeless part: what a Hazard *is* and the rules it plays by.

## What it is

rig has three layers:

1. **Effects** — point facts. "A write of X happens here." (Stage-2 derivation over the fact store.)
2. **Entry points** — reachability. Which effect-sites are reachable from which origins.
3. **Hazards** — patterns *over* the effect graph. A read-modify-write window, an N+1 read in a loop, a
   write to two systems with no atomicity. A Hazard relates effects (and/or reachability), it isn't a point
   fact.

Mental model: **static Complex Event Processing.** facts → effect graph → pattern detectors → ranked
findings → agent guidance. The primary consumer is an LLM reviewer/agent, not a human reading a report.

## The one rule that governs everything: suspicion maps, not proofs

A Hazard answers *"which paths resemble historically dangerous operational patterns?"* — never *"is this
correct?"* Every finding is a **candidate**, disclosed with confidence and residual, **never a verdict**.

The binding constraint is **don't cry wolf**: the consumer is an LLM, and a detector that fires on benign
code gets muted — and a muted detector is worth zero. Therefore:

- **Precision over recall on the default surface.** Over-approximation lives in an opt-in tier.
- **Annotate, never suppress.** A "protected" or "atomic" or "guarded" finding is *down-ranked and
  disclosed*, never silently filtered — because the protection is often an unverified assumption
  (in-process lock ≠ cross-instance safe; "in a transaction" ≠ serializable; an actor inbox ≠ durable
  exactly-once). Hiding it is how the bug escapes.
- **Calibrate FP rate on a real codebase before shipping a detector on-by-default.** A detector that fires
  179 times — even when structurally true — is noise if it's dominated by a benign archetype. Split or
  down-rank the archetype; don't ship the flood.

## Feasibility tiers — sort every detector before building

- **Tier 1 — structural co-occurrence / sequence.** rig's sweet spot; no dataflow needed. Read-before-write
  in a method, an effect inside a loop/fan-out, two writes to different systems in one method, a cycle over
  graph edges. **Build here.**
- **Tier 2 — absence claims** ("missing outbox / reconciliation / idempotency / invalidation"). Only honest
  as *"no **visible** X on the reachable path"*; weak across service/repo/instance boundaries (the repair
  job is often in another repo rig can't see). **Disclose hard, rank low, never assert "missing".**
- **Tier 3 — value-flow / intended ordering** ("read *influences* write", "A *must precede* B"). rig has no
  dataflow and no spec of intended order. **Approximate structurally or don't claim** — the
  read-before-write-same-cell proxy stands in for value-flow; intended ordering needs an external invariant.

## A finding's anatomy (Layer 3 — ranking)

Each finding carries: **detector / confidence / severity / path / supporting evidence** — structured for the
consumer. Two ranking inputs beyond per-finding confidence:

- **Corpus-calibrated FP rate per detector** — decides on-by-default vs opt-in.
- **Historical-similarity boost** (speculative) — a path structurally similar to an incident-linked /
  reverted change ranks higher; generalises corpus-grounding from rule *design* into per-path *scoring*.

## The detector catalog (patterns; status as of 2026-06-21)

Implemented detectors (intra-method / effect-local, tier 1):

| Hazard | Pattern | Confidence |
|---|---|---|
| `race_window` | read → write of the same cell, same method, not bracketed by a transaction (TOCTOU / lost-update) | high; medium when tx-bracketed (disclosed — read-committed ≠ safe) |
| `lazy_init_race` | `race_window` whose shape is lazy-init / do-once (single write + getter/init/ctor context) | low (heuristic) |
| `thread_local_context` | a `race_window`-shaped RMW on a `[ThreadStatic]` cell — thread-confined, so NOT a cross-thread race; rerouted here as the context-propagation surface (a value lost across an `await`/thread boundary). Detected via the attribute's ctor reference (no dedicated attribute fact). | low (disclosed — can't prove the await-crossing) |
| `n_plus_1` | a read inside a loop whose key varies per iteration | high |
| `unserializable_payload` | a store/serialize whose payload type is serializer-unsupported (e.g. `Option<T>`) | high |
| `dual_write` | a method writing to ≥2 distinct durable systems (db/queue/search/cache/http/…) | medium (no-outbox-check disclosed) |

Backlog detectors:

- **mutation-under-concurrency-region** (tier 1) — a shared mutation reachable under a parallel/handoff region.
- **event / cascade cycle** (tier 1, *graph*) — a cycle over publish→consume / invalidate→publish edges.
  **Blocked on a prerequisite graph edge:** rig links *subscription* (`+= Handler`) but not
  *raise/publish → handler*; that edge (resolve "what runs when this fires" by event/message identity) must
  exist before any event cycle can close. Same edge unblocks the cache-invalidation cycle.
- **retry-around-transaction / non-idempotent op** (tier 1 + tier 3).
- **missing-invalidate** (tier 2 absence), **contention hotspot** (speculative).
- **context-propagation (ThreadStatic)** — shipped as `thread_local_context`, a tier-1 STRUCTURAL PROXY for
  the tier-3 ideal: it flags a `[ThreadStatic]` cell's RMW (disclosed low — the await-crossing isn't proven,
  which would need flow modeling) rather than nothing. The **AsyncLocal** sibling is a separate detector (a
  field-*type* signal, not an attribute, and not an RMW reroute — its state lives behind `.Value`); deferred
  until AsyncLocal is back in scope to calibrate against (the grounding migration, !10208, was reverted).

## Delivery edges — producer→consumer by identity (the FR-10 prerequisite)

The graph layer the cycle/cascade detectors stand on. **Conjecture:** a producer→consumer relation (and,
separately, a read→write relation) is resolved by a shared **identity**, and that identity is *keyed on
different things by mechanism* — sometimes a symbol, sometimes a type, **often a call-site parameter value**:

| Mechanism | Identity | Keyed on | Join precision |
|---|---|---|---|
| C# event raise → subscribers | the event | a **symbol** (`E:` DocID) | exact |
| MediatR publish/send → handler | the message | a **type** (arg-0 type, `argument_type`) | exact (type-relation) |
| Echo actor tell/ask → spawn handler | the process | a **parameter** (process name, arg-0 `argument_name`) | `~heuristic` (resolves through a static field) |
| HTTP / arbitrary RPC → endpoint | the route | a **parameter** (route/URL string, `string_argument`) | `~heuristic` (interpolated client URL vs route template) |
| db / io / cache write ↔ read | the cell | a **parameter** (table / path / key) | `~heuristic` |

rig already captures these identities as effect **resources** (`argument_name` / `string_argument` /
`argument_type` / `type_argument`) — the param-keyed cases are exactly the ones that are *sometimes* a
value and therefore fuzzy (`~heuristic`), vs the symbol/type cases which bind exactly.

**The load-bearing split — delivery vs coupling:**
- **Delivery** (the runtime *causes* the consumer to run): event / actor / mediatr / http-RPC. These become
  **call (handoff) edges** — raiser→handler — so reachability and cycle detection traverse them. Modeled as
  handoffs: sync-cut by default, walked under `--async`, cycle-visible.
- **Coupling** (two independent flows touch the same cell; nothing is delivered): db / io / cache same-cell
  write↔read. This is a **correlation, NOT a call edge** — folding it into reachability would make every
  writer "reach" every reader and destroy the graph's meaning. It stays in the same-cell / consistency
  layer (FR-1 race_window, dual_write), and the row/object-level keying it needs is **FR-1f**.

**Status:** events shipped (`AddEventDeliveryEdges`, exact symbol join, baked into `call_edges` at `rig
graph`/`index`); actors next (process-name param join, `~heuristic`). http-route (the documented but
undeveloped *cross-repo contract / rpc* item) and mediatr are drop-in delivery resolvers on the same
machinery. Once the edges exist, **event/cascade cycle (FR-10)** and the **cache-invalidation cycle (FR-7)**
are a DFS over the enriched graph.

## How a Hazard reaches the consumer (surfaces)

The same "summary → react → full deal" model on every surface:

- **`rig derive`** — whole-store Hazards summary: named, counted, per-confidence-tier, sampled, with a
  `hazard` tsv row carrying full evidence. The triage list.
- **`rig impact`** — per-EP hazard **delta** (`+/- hazard <type>(<conf>)`) between two commits. The CI
  gate. (Note: `--expect-no-effect-change` is a *deterministic effect-set* gate and must NOT trip on a
  hazard-only delta — a heuristic hazard gain on a behaviour-preserving refactor stays green; a separate
  opt-in would gate hazards.)
- **`rig tree <ep> --hazards`** — hazards inline on one entry point's reachable tree (drill-in): a ⚠ marker
  per hazard-bearing node + a summary section + `--format tsv` `hazard` rows. Re-derives the EP's bounded
  closure with the static-field refs threaded in + the hazard post-pass; the augmented effects are not cached
  (the hazard-free caches are untouched), so a plain `tree` is unaffected.

A detector is modeled as an observation on an effect; adding its type to the hazard catalog
(`HazardKinds`) flows it through the derive view and the impact delta automatically.

## Explicit non-goals (the scope line)

rig finds **operational hazards observable from static execution semantics** — and *nothing else*:

- not business-rule / algorithm / formula correctness
- not runtime cardinality, not performance characteristics needing production data
- not infrastructure misconfiguration
- not cross-repository contract violations
- not value-flow / interleaving it cannot statically see

Keeping this line is what stops the framework overselling — and stops the consumer learning to ignore it.

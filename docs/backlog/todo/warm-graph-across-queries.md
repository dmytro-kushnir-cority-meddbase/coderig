# Warm graph across queries in `rig serve` (bounded, demand-gated)

**Status:** TODO / DESIGN-GATED · **Found:** 2026-06-26 from `callers --time` measurements · **Family:** performance

## Measured problem

On the MedDBase store, a one-shot reverse query paid an approximately 8-second floor; one representative
`--time` run attributed 5.7 seconds and 1.5 GB of disk reads entirely to graph loading, with traversal itself
negligible. A 35-query review batch therefore spent about 5.2 minutes repeatedly materializing the same graph.

The companion [dispatch calibration](../done/dispatch-precision-substrate.md) showed why this is not a
cheap SQL problem: dispatch expands a representative bounded closure from 157 to 41,626 methods. Even after
deferring effect inputs, the recursive edge walk retained an approximately 4.7-second / 1.1-GB floor. A static
connection does not help, and the additive `~mono` persistence proposal was rejected.

## Current direction

Keep one-shot CLI commands stateless. If repeated-query demand justifies state, reuse a shaped graph inside the
existing, explicitly started `rig serve` process. Do not introduce an MCP server or a forever daemon for this
feature.

The cache must be bounded:

- Default to one warm store; an optional small LRU needs an explicit memory budget.
- Key by the same store identity and rule fingerprint used by query caches.
- Detect store replacement/reindex and rules changes before every lookup; evict rather than answer stale.
- Release the graph when `rig serve` exits; an idle eviction may reclaim it sooner.
- Share one immutable shaped graph safely across concurrent queries; keep per-query traversal state separate.

At MedDBase scale one warm graph was measured near 1.7 GB, so multi-store retention is an opt-in cost, never an
unbounded dictionary.

## Gate before implementation

First capture a current `rig serve` batch baseline because the 2026-06-26 numbers predate later graph and
storage changes. Proceed only if repeated queries still spend most wall time in identical graph loads and the
expected review workflow issues enough queries to amortize the retained memory.

The alternative is heavy receiver-narrowed dispatch persistence at graph time. That attacks cold single-shot
latency but has context-sensitive edge/schema complexity and store blow-up risk; decide between that and warm
reuse from current measurements, not from the old additive materialization design.

## Acceptance

- Query 1 may pay the normal graph load; identical queries 2..N reuse it without re-reading the graph.
- Reindexing, switching stores, or changing rules produces a miss and a fresh graph.
- Concurrent queries are output-equivalent to fresh-process CLI queries.
- Memory stays within the configured single-store/LRU bound and is released on eviction or process exit.
- MedDBase A/B reports cold latency, warm latency, retained memory, and break-even query count.

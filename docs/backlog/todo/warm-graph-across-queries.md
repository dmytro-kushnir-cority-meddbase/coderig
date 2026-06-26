# Warm graph across queries (the #1 perf lever for repeated/agent use) — MCP front-end

**Status:** todo · **Found:** 2026-06-26 (measured via the new `--time` on `callers`) · **Family:** perf

## The measurement (this is the evidence, not a guess)
`rig callers <x> --async` has a **~8s per-call floor** (median 8.1s over a 35-site batch = **5.2 min** for the
hazard→EP mapping; the 0-EP call still took 7.9s). `--time` on a hot 3801-EP reverse query attributes it:
```
phase        wall   %    cpu:self  cpu:sys  gc%   peakRAM   diskR   diskW
graph load   5.7s 100%      4%       14%    3%    1.7GB    1.5GB     0MB
total        5.7s 100%
```
**100% graph LOAD, disk-IO bound** — `diskR 1.5 GB` per query, `cpu:self 4%` (process idle), traversal
negligible (total == load). Every query re-reads ~1.5 GB of `call_edges`/`dispatch_edges`/reverse-maps off
disk and then barely computes.

## The lever (reranked)
The cost is **re-reading the graph per process**, so the win is **holding the loaded graph warm in one
process across many queries** — erases the ~8s floor for query 2..N. Ranked:
1. **Warm in-memory graph across queries (this card)** — biggest win for any *repeated*/batch/agent workflow
   (the hazard map, an agent firing many `callers`/`reaches`). The natural front-end is an **MCP server**
   (rig as a long-lived server holding a store-keyed warm graph, exposing callers/reaches/tree/derive/effects-diff
   as tools) — which also matches the "LLM is the consumer, composes rig" architecture. A `batch` command is the
   cheap/narrow stopgap (one process, fixed query list); **MCP subsumes it** (agent fires N warm tool-calls).
2. **Graph-time materialization** ([dispatch-precision-substrate.md](dispatch-precision-substrate.md)) —
   *complementary*: bakes the narrowed graph so the per-call load reads *less* (smaller `call_edges`). Shrinks
   the 1.5 GB; doesn't eliminate the re-read. Do both; warm-graph is the larger lever.
3. **Single static SQL connection** — **❌ WON'T DO** (confirmed): the cost is 1.5 GB of *data read*, not
   connection-open. A shared connection saves ~nothing here.

## The real engineering (shared by batch and MCP)
- **Warm graph cache** keyed by store id, held in a long-lived process.
- **Lifecycle**: invalidate on re-index / store change (staleness detection); the ~1.7 GB RAM cost of holding
  a MedDBase-scale graph; concurrency if multiple queries in flight.
- **Front-end**: MCP server (the .NET MCP SDK) exposing the query commands as tools. Single-shot CLI usage is
  unaffected (still loads per-invocation — fine for interactive one-offs; the floor only hurts *repeated* use).

## Why it matters now
Interactive single queries are fine (8s is tolerable once). But the agent-consumer workflows this project is
built around — bulk hazard→EP mapping, an LLM composing many `callers`/`reaches`, the `cache-coherence`/`event_cycle`
per-EP wiring — pay the floor N×. This is the perf work that actually unblocks those.

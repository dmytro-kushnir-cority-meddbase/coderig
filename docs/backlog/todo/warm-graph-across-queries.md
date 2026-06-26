# Warm graph across queries (bounded — only if the agent-batch workflow demands it) — MCP front-end

**Status:** todo · **Found:** 2026-06-26 (measured via the new `--time` on `callers`) · **Family:** perf

> **Reordered 2026-06-26: this is the SECOND perf lever, not the first.** Build
> [graph-time materialization](dispatch-precision-substrate.md) first — it cuts the cold-load cost while
> keeping `rig` a stateless CLI, and shrinks whatever a daemon would later hold. A resident warm-graph process
> is real cost: it **pins ~1.7 GB per store indefinitely** (multi-store is additive → easily ~10 GB if it
> holds several warm), **owns staleness/invalidation** (re-index must evict-or-reload, else it silently
> answers from a stale graph — the cache-coherence bug, ironically), and **owns lifecycle** (start/idle-death/
> walk-away-RAM). Only pull this lever if materialization+lazy-load don't get the cold load low enough for the
> genuine "agent fires 100 queries" workflow — and even then, keep it **bounded** (single/LRU-evicted store,
> idle-timeout death, explicit `rig serve` for a batch session — NOT a forever daemon).

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

## The levers (ordered — stateless first, stateful last)
The cost is **reading + materializing 1.5 GB per process**. Order of attack, cheapest-state first:
1. **Graph-time materialization** ([dispatch-precision-substrate.md](dispatch-precision-substrate.md)) —
   bakes the narrowed graph so each cold load reads *less* (smaller `call_edges`, no query-time
   monomorphization). Cuts the 1.5 GB while `rig` stays a **stateless CLI**. Build this first; it also shrinks
   whatever a daemon would later hold.
2. **Lazy / partial load** — a reverse query from one symbol needs only its reverse-reachable cone, not the
   whole 1.5 GB. If the on-disk layout supported pulling just the touched partition, cold load drops with
   **zero resident state**. More storage-layer work; still stateless.
3. **Warm in-memory graph across queries (this card)** — *only if* 1+2 don't get the cold load low enough for
   the genuine repeated/agent workflow (the hazard map; an agent firing many `callers`/`reaches`). Front-end
   is an **MCP server** holding a store-keyed warm graph as tools — matches "LLM composes rig"; a `batch`
   command is the narrow stopgap MCP subsumes. **Keep it bounded** (see banner): the win (erase the ~8s floor
   for query 2..N) is real, but so is the forever-RAM + invalidation cost.
4. **Single static SQL connection** — **❌ WON'T DO** (confirmed): the cost is 1.5 GB of *data read*, not
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

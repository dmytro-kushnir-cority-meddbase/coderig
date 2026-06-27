# `branch-aware-effects`: control-dependence over effects (must-run spine vs guarded shell)

**Status:** todo · **Found:** 2026-06-26 (appointment-lifecycle dive — the "`RemoveConfirm` reaches 54 effects but ~90% are under a branch" over-count) · **Family:** effect-precision / new-substrate
**Design settled:** 2026-06-27 (CFG-only; Path-A syntactic proxy rejected — see below)

## The gap
`reaches`/`tree` report a **path-insensitive UNION** of reachable effects: every effect that *could* be hit on *any*
path, with no notion of which run on a given invocation. So "`RemoveConfirm` → 54 typed effects" conflates the handful
that always run (open tx, audit, the unconditional delete) with the dozens behind `if (invoice.IsHealthcode)` / mutually
exclusive `if/else` arms / a per-item loop. The count over-states the typical path and buries the spine.

## Mental model: control dependence
An effect is **control-dependent** on the branch predicates that decide whether it runs.
- **must-run** = control-dependent only on method entry; its CFG block **dominates the Exit** (every Entry→Exit path
  passes through it).
- **guarded** = control-dependent on ≥1 predicate; the **guard set** is those predicates, each with a **polarity** (the
  `if`-arm vs the `else`-arm).
- **mutually exclusive** = two effects guarded by the SAME predicate with opposite polarity ("delete OR bulk_write,
  never both").

## Substrate decision — CFG only. The syntactic proxy (Path A) is REJECTED.
A cheap "walk the syntax ancestors for an enclosing `if`/`switch`" bit (the sibling of today's loop marker, see below)
was considered and **rejected**: it has no node to find for whole classes of conditional execution and silently mislabels
them must-run —
- `a?.Save()` (runs only if `a != null`; no `if` in the tree),
- `cond && Save()` / `x ?? Fetch()` (short-circuit; no statement ancestor),
- `x switch { Healthcode => Bill(), _ => Skip() }` (switch *expression*),
- `is`-pattern `when` guards, and whatever sugar C# adds next.

The Roslyn **ControlFlowGraph lowers every one of these into the same branch-block shape.** Do it once on the CFG and it
is sugar-proof forever. (Validated in the `CfgSpike` learning prototype: must-run via dominators is correct through
`if/else` merges AND loop back-edges.)

**Bonus — this RETIRES the syntactic loop walk.** Today `LoopKind`/`LoopDetail` come from a nearest-enclosing-loop
ancestor scan in `StructuralContextOf` (`FactExtractor.cs:990`). A CFG back-edge gives loop membership directly, so loops
and branches both become CFG-derived from one pass — consistent and sugar-proof. (Migrating the loop marker is a
follow-up, not a prerequisite; ship branch must-run first, then fold loops in.)

## Data model — where the fact lives and what it carries
**Granularity: the call-SITE (reference fact), NOT the deduped call-edge.** The same callee can be called twice in one
method with different guards (`Save(x); if (x.IsDirty) Save(x);`) — one `A→B` edge, two conditionalities. So the
control-dependence rides the **`ReferenceFact`** — the same home as `LoopKind` in `StructuralContext` — and the derived
effect inherits it from the reference it came from. (rig's `call_edges` are materialized from references; the walk reads
conditionality at reference granularity, never from a merged edge row.)

**Payload per effect-bearing reference — the guard set (intra-procedural):**
```
guards: [ (predicateId, displayText, polarity), … ]
```
- **empty guard set ⟺ must-run** (block dominates the method Exit) — the unconditional spine.
- **polarity** — which successor edge (the `WhenTrue`/`WhenFalse` from `BasicBlock.ConditionKind`) the block sits on;
  without it you cannot tell the `if`-arm from the `else`-arm.
- **predicateId** — a STABLE id for the controlling branch (its `BranchValue` syntax span). Lets stage 2 **group**
  ("these 12 effects share guard `IsHealthcode`") and detect **mutual exclusion** (same id, opposite polarity). Text alone
  can't group; the span can.
- **displayText** is heterogeneous and that's fine: `a == null` for an `if`, the case pattern for a `switch`, `a != null`
  for a `?.`. All come from `BranchValue` + `ConditionKind`.
- Encode as a flat-string list in `FactStructuralContext` (mirror `EncodeScopes`).

## The two-stage split (be firm on this)
- **Stage 1 (extraction) freezes INTRA-procedural truth per call-site**: "is this site must-run / guarded *within its own
  method M*", computed from M's CFG. It does NOT know whether M itself always runs — unknowable at extraction (depends on
  the caller), so don't bake EP-context into the fact.
- **Stage 2 (derivation) composes to the EP-level answer**: an effect is must-run-*from-EP* iff *every* call-site on the
  EP→…→M chain is must-run (AND-folded along the path). This rides the EXISTING memoized cyclic call-graph walk in
  `FactPathFinder` — the same `visited`-set that makes `reaches` terminate on recursion (incl. indirect A→B→A). No new
  infinite-recursion risk; do NOT hand-roll a fresh recursion.

## Build order (milestones — ship must-run first)
1. **The syntax-node → basic-block bridge** (the fiddly crux; prototype in `CfgSpike` first). Given an effect's invocation
   syntax node, find the CFG block that holds it. Gotchas: a source statement can split across blocks under lowering
   (`a?.B()` → null-check block + call block), so map the **invocation** node, not its enclosing statement; `BranchValue`
   (the condition) is NOT in `block.Operations` — enumerate it separately; index via `IOperation.Descendants().Syntax`.
2. **Must-run per reference**: bridge + the dominator pass (already prototyped) → a boolean on each effect-bearing
   reference. Surface the spine/guarded partition in `reaches`/`tree` (header `8 unconditional · 46 guarded`, an
   ALWAYS-RUNS group + a GUARDED group, a `--spine` flag). **This is the first shippable slice and the headline value.**
3. **Guard sets**: control-dependence (post-dominance frontier) → `(predicateId, text, polarity)` per guarded reference.
   Enables guard grouping + mutual exclusion + the `impact` "added behind a NEW `if`" signal.
4. **Detector customers** (already waiting on #3): [[lazy-init-race-precision-tightening]] bucket #2 (write must be
   control-dependent on the null/flag `if` — part of 57.5%→~92%) and [[non-transactional-write-loop]] (write guarded by
   `if(!alreadySent)` vs unconditional).

## Output surface (milestone 2+)
- **`reaches`**: `54 typed effects · 8 unconditional · 46 guarded (⎇ branches · 🔁 loops)`, then an ALWAYS-RUNS group and
  a GUARDED group (grouped by guard once #3 lands).
- **`tree`**: a `⎇` edge glyph beside the existing `🔁`; `--spine` prints only must-run frames.
- **`impact`**: tag a delta'd effect "`+ added UNCONDITIONALLY`" vs "`+ behind a NEW guard if(…)`".

## Scope boundaries (explicitly NOT now)
- **First-party source only.** The CFG exists only where we have source. An effect reached *through* a BCL/third-party
  frame has no CFG on the external side — its external guards are unknowable without ILSpy-decompiled IL. Guards stop at
  the source boundary; disclose, don't fabricate.
- **No migration of the semantic context-detectors** (`parallel_fanout`, `resilience_retry`, `resource_span`). They read
  enclosing constructs that often live in opaque external frames; CFG buys little there and breaks at the assembly
  boundary. Once the CFG substrate exists they *could* migrate, but it's out of scope and low-value — noted, not planned.

## Cost gate — MEASURE before committing the substrate
Per-body `GetOperation` + `ControlFlowGraph.Create` across the 436k-symbol monorepo is a real extraction-time adder
(today's extractor pulls neither `IOperation` nor a CFG). Gate it:
- scope CFG build to bodies with ≥1 effect-bearing reference (skip DTOs / pure getters);
- handle lambda/accessor regions (CFG exposes anonymous-function sub-graphs separately — enumerate them);
- **the cost spike**: build CFGs for the methods on `RemoveConfirm`'s tree, run milestone 1+2, eyeball the spine against
  the 54, AND clock the extraction wall-clock delta on a re-index. Commit only if acceptable.
- Implementation note: the per-method must-run can use the **naive delete-test** (CFGs are tiny, computed once); the
  dominator-tree (CHK) is only worth it if profiling says so — the `CfgSpike` has both, cross-checked equal.

## Why it matters
Turns an over-approximated worst-case effect count into an honest typical-vs-worst-case view — the single most-requested
sharpening from the lifecycle dive — and the same substrate feeds two backlog detectors and (via back-edges) finally puts
loop detection on a sugar-proof footing. Fits rig's disclose-the-approximation ethic.

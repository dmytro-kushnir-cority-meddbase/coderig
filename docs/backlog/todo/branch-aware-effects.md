# `branch-aware-effects`: control-dependence over effects (must-run spine vs guarded shell)

**Status:** todo · **Found:** 2026-06-26 (appointment-lifecycle dive — the "`RemoveConfirm` reaches 54 effects but ~90% are under a branch" over-count) · **Family:** effect-precision / new-substrate

## The gap
`reaches`/`tree` report a **path-insensitive UNION** of reachable effects: every effect that *could* be hit on *any* path,
with no notion of which ones actually run on a given invocation. So "`RemoveConfirm` → 54 typed effects" conflates the
handful that always run (open tx, audit, the one unconditional delete) with the dozens that are mutually-exclusive
`if/else` arms or sit behind `if (invoice.IsHealthcode)` / a per-item loop. The count over-states the typical path and
buries the spine. Today rig **cannot measure** conditionality at all — it only has the syntactic loop marker (🔁).

## Mental model: control dependence (the PDG notion)
An effect is **control-dependent** on the branch predicates that decide whether it runs.
- **unconditional / must-run** = control-dependent only on method entry (no branch gates it).
- **guarded** = control-dependent on ≥1 predicate; the **guard set** is those predicates.
- **mutually exclusive** = two effects control-dependent on the SAME predicate with opposite edge polarity
  (the `if`-arm vs the `else`-arm — "delete OR bulk_write, never both").

## Substrate decision — build on the REAL Roslyn CFG, not a syntactic proxy
`FactExtractor` today is **syntax + `SemanticModel`** driven (`root.DescendantNodes()` + `model.GetSymbolInfo(...)`); it
pulls **neither `IOperation` nor the CFG**, and effects are keyed to syntax nodes. So branch-awareness is a NEW extraction
capability, not a tweak. The bridge is standard:
- per **effect-bearing** method body: `model.GetOperation(body)` → `ControlFlowGraph.Create(op)` → index basic blocks by
  syntax span → for each effect's syntax node, find its block.
- derive and **freeze as facts** (the CFG itself is transient, never stored — same "freeze Roslyn analysis into facts"
  ethic as `dispatch_facts` freezing `FindImplementationForInterfaceMember`):
  - **must-run bit** = block dominates the CFG exit. SOUND — handles early-`return`/`throw` (the case the syntactic
    proxy gets wrong). *This is the cheapest derivation and the one to ship first.*
  - **controlling predicate(s)** = post-dominance frontier → the branch block → its condition operation (a symbol/span
    reference, not the full expression). The guard set.
  - **loop membership** = CFG back-edge (upgrades the purely-syntactic 🔁).
- The syntactic "is it lexically under an `if`/`switch`/`catch`" bit is only a **fallback** for bodies where CFG
  construction fails.

## What the one substrate unlocks (the payoff for paying the CFG cost)
1. **must-run spine vs guarded shell** — sound; the headline partition ("9 always / 45 guarded").
2. **guard sets** — which predicate gates each effect (the actionable layer; NOT a separate project once the CFG is in).
3. **mutual exclusion** — same predicate, opposite arms.
4. **precise loops** — back-edge, replacing the syntactic loop-ancestor heuristic.
5. **dead statements** — Roslyn marks unreachable CFG blocks → a dead-code-adjacent signal (bonus).
Two existing customers are already waiting on bit #2: [[lazy-init-race-precision-tightening]] bucket #2 (the write must be
**control-dependent on the null/flag `if`** — literally this signal; part of 57.5%→~92%) and
[[non-transactional-write-loop]] (a write guarded by `if(!alreadySent)` vs unconditional).

## Stage 2 — inter-procedural composition
The CFG is intraprocedural, so it gives a SOUND per-call-edge "is this call must-executed in its caller" bit. Compose to
"always runs **from the EP**" by BFS over must-edges: a node is must-run from the EP iff ≥1 all-must path reaches it; the
guarded shell is the complement. (This is the tractable approximation of inter-procedural control dependence.)

## Output surface — ship this FIRST (small), keep the rest behind it
- **`reaches` header + grouping**: `54 typed effects · 9 unconditional · 45 guarded (⎇ 32 branches · 🔁 19 loops)`, then
  an ALWAYS-RUNS group and a GUARDED group.
- **`tree`**: a `⎇` edge glyph beside the existing `🔁`; a `--spine` flag prints only the unconditional frames
  ("what definitely happens on every invocation").
- **`impact`**: tag a delta'd effect "`+ added UNCONDITIONALLY` (fires on every call)" vs "`+ behind a NEW guard if(…)`" —
  a real review signal the diff alone can't give.

## Honesty rule (which way each label errs)
- **`guarded` is SOUND** under the CFG (a branch genuinely gates it) — lead with this set.
- The **must-run** set is sound *only* with the CFG; if we ever fall back to the syntactic proxy, must-run becomes an
  over-claim (early-`return` over-counts the spine) → label it "likely-always, verify", never "guaranteed".

## Cost caveat — MEASURE before committing the substrate
Per-body `GetOperation` + `ControlFlowGraph.Create` across the 436k-symbol monorepo is a real extraction-time adder
(today's extractor pulls neither). Gate it:
- scope CFG build to bodies with ≥1 effect (skip DTOs / pure getters).
- handle lambda/accessor regions (CFG exposes anonymous-function sub-graphs separately — enumerate them).
- **Task 1 = a measured spike**: build the CFG for the methods on `RemoveConfirm`'s tree, derive the must-run spine,
  eyeball it against the 54, AND clock the extraction wall-clock delta on a re-index. Commit the CFG substrate only if the
  cost is acceptable; else fall back to the syntactic bit for the must/guarded partition and drop the guard/mutex layers.

## Why it matters
Turns an over-approximated worst-case effect count into an honest typical-vs-worst-case view — the single most-requested
sharpening from the lifecycle dive — and the same substrate feeds two backlog detectors and a dead-code signal. Fits
rig's disclose-the-approximation ethic.

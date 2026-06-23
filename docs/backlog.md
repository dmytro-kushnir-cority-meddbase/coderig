# rig — feature backlog

Forward-looking feature specs not yet scheduled. Distinct from
[rig-review-issues.md](rig-review-issues.md) (the MR-!10645 audit punch-list). Promote an item to a branch
+ commits when picked up; convert to a GitHub issue (`gh issue create`, remote `dv00d00/coderig`) if tracked
externally.

---

## Feature: LLM-optimised call-tree summary format (`--llm-summary`)

### Problem

CodeRig currently produces two output formats for call-tree analysis:

| Format | Approx. size | Issue |
|---|---|---|
| Annotated tree (terminal) | ~3 k chars | Box-drawing chars and emoji tokenise badly; structure encoded twice (indent + box chars) |
| Flat TSV (`--summary`) | ~100 k chars | Full CLR signatures, unreduced effect lists, and per-row file paths make it prohibitively token-expensive |

Neither is well-suited as LLM input. The terminal format is readable by humans but wastes tokens on
decoration. The flat TSV is structurally sound but ~30–50× larger than necessary, primarily due to full CLR
signatures.

The primary consumer of this output is an LLM doing structural reasoning: redundancy detection, side-effect
analysis, entry-point classification. That consumer does not need namespaces, parameter types, or file paths.

### Proposed solution

Add a `--llm-summary` flag (or `--summary=llm`) that emits a compact, flat, deterministically diffable TSV
optimised for LLM token budgets.

#### Format specification

Tab-separated, one row per node, with a header row. File is UTF-8, LF line endings.

```
depth    parent    name    arity    calls    effects    flags
```

| Column | Type | Description |
|---|---|---|
| `depth` | int | 0-based nesting depth |
| `parent` | string | Short name of the direct caller; empty for roots |
| `name` | string | `TypeName.MethodName` — no namespace, no parameter types |
| `arity` | int | Parameter count (preserves overload disambiguation without listing types) |
| `calls` | int | Number of call sites from parent (replaces `×N` in tree format) |
| `effects` | string | Deduplicated, counted effect list: `io:read ×3, efcore:read ×2` |
| `flags` | string | `cycle`, `x-phase`, `elided`, `lambda` — pipe-separated if multiple |

#### Name shortening rules

1. Strip all namespace segments — keep only the declaring type's simple name and method name.
2. Strip parameter types — preserve arity (count) only.
3. Lambda nodes: omit the row entirely (flag on parent as `lambda` if relevant); lambda bodies are token
   waste for structural reasoning.
4. Compiler-generated types (`<>c`, `d__N`): suppress or fold into the nearest named ancestor.

#### Effect deduplication rules

Current flat TSV emits one token per effect occurrence: `io:write,io:write,...×16`.
New format aggregates: `io:write ×16`. If only one occurrence: `io:write` (no count).
Multiple distinct effects: comma-separated after aggregation: `io:read ×3, efcore:read ×2`.

#### Elision policy

`⋯elided` in the tree format is a correctness hazard for redundancy analysis — the LLM cannot distinguish
"not called again" from "called but suppressed." The new format should either:

- **Include** the elided call with `flags=x-phase` and full effect annotation (preferred), or
- Emit a synthetic row with `name=<elided>` and a stable reference back to the first occurrence.

The first option is preferred because it makes redundancy analysis unambiguous without expanding token cost
significantly.

#### Example

Input tree fragment (current):
```
├─ Reads.LoadFactGraphAsync ⋯elided  {⚡ efcore:read Data.CallEdge, ⚡ efcore:read Data.ImplementsEdge, ...}
```

New format row:
```
1    DeriveCommand.RunAsync    Reads.LoadFactGraphAsync    1    1    efcore:read ×4    x-phase
```

Full example output (abbreviated):
```
depth    parent    name    arity    calls    effects    flags
0        DeriveCommand.RunAsync    9    1    io:write ×16    
1    DeriveCommand.RunAsync    RuleSetLoader.Load    2    1        
2    RuleSetLoader.Load    RuleSetLoader.LoadMergedDocument    3    1    io:read    
3    RuleSetLoader.LoadMergedDocument    RuleSetLoader.LoadBuiltIn    1    1    io:read    
3    RuleSetLoader.LoadMergedDocument    RuleSetLoader.MergeWithFile    2    2    io:read ×2    
1    DeriveCommand.RunAsync    Reads.LoadFactGraphAsync    1    1    efcore:read ×4    
1    DeriveCommand.RunAsync    Reads.LoadFactGraphAsync    1    1    efcore:read ×4    x-phase
```

The duplicate `Reads.LoadFactGraphAsync` rows — one plain, one `x-phase` — make the redundant load
immediately visible without any tree traversal.

### Non-goals

- Human terminal readability (that is the existing tree format's job).
- File paths and line numbers (use the existing format or the full TSV for navigation).
- Full type-resolution fidelity (arity is sufficient for structural reasoning; the full TSV remains
  available when types matter).

### Acceptance criteria

- [ ] `--llm-summary` flag produces valid TSV with header row.
- [ ] No CLR namespaces or parameter type names appear in output.
- [ ] Effect lists are aggregated (`×N` form).
- [ ] X-phase calls are included with `x-phase` flag rather than silently suppressed.
- [ ] Lambda nodes are suppressed.
- [ ] Output is deterministic across runs for the same input (diffable).
- [ ] Size regression test: output for the reference codebase stays under a defined token budget
  (suggested: 8 k tokens for a mid-sized solution).

### Implementation notes (orchestrator)

- The tree is already built (`TreeCommand` / `FactPathFinder.BuildTree`); this is a new **renderer** over the
  existing forest + the effect annotations, alongside the terminal renderer and the `--summary` TSV — not a
  new traversal. Name shortening reuses `SymbolNameFormatter`'s simple-name logic.
- The `x-phase`/`elided` flag is exactly the `⋯elided` "seen" marker the tree renderer already computes (see
  `docs/bugs/tree-spurious-seen-footer-for-lambdas.md` for the lambda edge case) — surface it as a column
  instead of suppressing the subtree. This dovetails with the redundant-reload findings the derive call-tree
  surfaced (x-phase duplicates become first-class, greppable rows).

### Token efficiency: the `parent` column

The `parent` column re-spells the parent's short name on every child row (and the same name is also that
node's own `name` on its own row) — long names repeated N× across N siblings. Cut it **per projection**:

- **Reconstructable views (default spine-kept / full):** rows are DFS pre-order with `depth`, so a row's
  parent is *the nearest preceding row at `depth-1`* — fully derivable (lambda-folding and x-phase both
  preserve this). So **drop `parent` entirely** in these views: biggest token save, zero indirection (the LLM
  reads it like an indented tree, natively). Verified the depth+order linkage holds after lambda folding.
- **Effects-flat view (gaps, no spine):** `parent` cannot be recovered from depth+order, so it stays
  explicit. *Here* a surrogate row-id (`id` column; `parent` = parent's id) earns its keep — saves the
  repeated long name AND disambiguates short-name collisions (two `Foo.Bar` from different namespaces shorten
  identically, making a name-parent ambiguous). Trade-off: an id forces the LLM to build a row-id lookup vs.
  reading a name locally, so prefer it only where the name is genuinely repeated/ambiguous.
- Introduce surrogate ids *globally* only if short-name collisions prove common in practice — measure first;
  the indirection cost is real. Touches `LlmSummaryRenderer`; sequence after the `--format llm` refactor.

---

## Refactor: single graph-shaping entry point (`LoadShapedGraphAsync`)

### Problem

The reachability-shaped call graph (`classify methodGroup→handoff` → `RewriteGenericFactories` → delivery
edges) is assembled in **three scattered, partial places**:

- `GraphMaterializer.BuildFromGraphAsync` — bakes classify + factory + delivery into the persisted `call_edges`.
- `DeriveCommand.RunAsync` — hand-rolls `LoadFactGraphAsync → RewriteGenericFactories → LoadDeliverySites →
  AddDeliveryEdges` inline (for FR-10 `event_cycle`).
- `FactPathFinder.ShapeGraph` (used by `impact` + the EF-fallback traversals) — does factory + cut + context
  but **omits delivery edges entirely**, so `impact`'s per-EP reach and EF-fallback `reaches`/`tree`/`path` do
  not see publish→consumer delivery at all.

Three definitions that can drift, a real coverage gap (impact/EF-fallback miss delivery edges), and a
documented-only ordering invariant (the delivery join consumes the classifier's `Kind=handoff` output, so it
must run after — enforced by comment, not structure).

### Proposed solution

One Storage entry point `Reads.LoadShapedGraphAsync(context, RuleSet rules, ct)` that returns the fully
in-memory-shaped graph: `LoadFactGraphAsync` (load + classify) → `RewriteGenericFactories` → delivery edges
(`LoadDeliverySitesAsync` + `AddDeliveryEdges`) → attach cut/context metadata. Every in-memory consumer
(`derive`, `impact`, EF-fallback traversals) calls it; `GraphMaterializer` persists **exactly its
edge-creating output** to `call_edges` (cut/context stay traversal-time, as today). Net:

- **Closes the gap**: `impact` per-EP reach + EF-fallback traversals gain delivery edges uniformly.
- **Resolves review finding #1a**: the graph is loaded + shaped **once** in `derive` and reused by both the
  handoff-EP derivation and the cycle pass (was loaded twice — `DeriveHandoffEntryPointsAsync` internal +
  `DeriveCommand:115`).
- **Dissolves the ordering coupling**: stage order lives inside one function, tested — not a cross-call comment.
- One shaping definition; `call_edges` becomes purely its materialization.

### Acceptance criteria

- [ ] `derive`, `impact`, EF-fallback traversals, and `GraphMaterializer` all obtain the shaped graph from the
  one entry point; no hand-rolled `classify→factory→delivery` sequence remains at a call site.
- [ ] `impact --per-ep` and EF-fallback `reaches`/`tree` now traverse delivery (event/actor) edges (new test).
- [ ] `derive` loads the graph once (verify via the call tree — no duplicate `LoadFactGraphAsync`).
- [ ] Behavior otherwise unchanged: `rig derive` output byte-identical; MedDBase `event_cycle` 24/all-high;
  persisted `call_edges` count unchanged; full suite green.
- [ ] `dead`'s unshaped-CHA-superset requirement still met (the raw/`--raw` path bypasses delivery shaping).

### Related: parallelise the independent query-side loads — INVESTIGATED, DOES NOT PAY (do not rebuild)

The derive (and impact) commands issue several **data-independent** loads — graph edges, EP data, delivery
sites, effect inputs — that run **sequentially** on one `DbContext`. They are temporally decoupled, so they
*looked* like candidates to overlap across **separate read `DbContext`s / connections** (sound — the store is
opened read-only and SQLite allows concurrent readers; not `Task.WhenAll` on one context, which throws).

**Profiled + built the lowest-risk slice + measured on the real store → reverted.** Findings (2026-06-23,
MedDBase, Threadripper 32-logical, NVMe):
- The synthetic raw-SQLite concurrency experiment looked promising: 2 concurrent `reference_facts` scans on
  separate connections ran **1.94–2.75× faster** than sequential. The reads ARE CPU/marshaling-bound, not
  single-disk-serialised, so in isolation they overlap.
- **But the real `derive` command got no win — a slight regression.** Built the cleanest slice
  (`LoadShapedGraphAsync ∥ LoadFactEntryPointDataAsync` via `Task.WhenAll` on a second read context, in both
  `derive` and impact's `LoadHeadSideDataAsync`). Output stayed **byte-identical** (correctness fine), but warm
  `derive` went **~13.2 s → ~13.7 s median** (5+ runs each). The DB-load region is only ~33–36 % of wall-clock
  (Amdahl ceiling ~1.1–1.3 ×), and even that didn't materialise: the two big loads contend on EF marshaling /
  memory bandwidth, and the second context's setup (per-connection `mmap_size=1 GB` + 256 MB page cache) +
  EF compiled-model warmup outweigh the DB-layer overlap.
- **Conclusion:** adding a second `DbContext` + concurrency for net-negative perf is the trade we explicitly
  rule out. The bottleneck is the single-threaded CPU passes (`FactEffectDeriver.Derive`, `FactCycleDeriver`)
  + EF row materialisation, which DB-connection parallelism can't touch. If derive latency ever matters,
  attack THAT (the CPU passes / marshaling), not the load sequencing. Do not re-attempt the connection
  parallelisation without a materially different store profile.

---

## Perf: redundant work per entry point (rig self-dogfood, F1–F9)

Found by running `rig` on its own store and reading every EP's `--format llm` call tree (the `x-phase` flag
makes a re-reached node a first-class row). One command calling the same heavy load more than once in a
single invocation. The **derive-path** instances are FIXED (commit `perf(derive): cut redundant reloads`);
the rest are the same patterns in other commands, still open. Severity = the cost of the repeated work.

| # | Redundant work | EPs | Status |
|---|---|---|---|
| F1 | `LoadFactGraphAsync` (efcore:read ×4) loaded inside `DeriveHandoffEntryPointsAsync` AND again directly | Derive | **FIXED** (`9caef5d1`) — `LoadShapedGraphAsync` loaded once, threaded into `DeriveHandoffEntryPointsAsync` + the cycle pass |
| F2 | `LoadFactEntryPointDataAsync` (efcore:read ×5) loaded top-level AND again inside a derivation callee | **FIXED** (Derive + Tree/Reaches) (`1be1094f`) | the real duplicate was the EF-fallback path (`TraversalGraphLoader` + `EntryPointContext.DeriveEpSiteKind`); threaded one load via `ReachInputs.EpData`. Callers/Path/Impact load epData at their own level (no dup through `BuildEpContext`) |
| F3 | `LoadFactGraphAsync` HEAD + BASE in Impact; each opens a fresh ADO conn via `LoadDispatchFactsAsync` | Impact | conn-reuse part FIXED in `LoadFactGraphAsync`; the base/head double-load is **intentional** (different stores) |
| F4 | `LoadDeploymentsAsync` (io:read ×3, slnx+projrefs parse) runs **twice** (`calls=2`) | Impact | **FIXED** (`78dbe9c2`) — hoisted before the cache branch, reused on both paths |
| F5 | `EffectDerivation.DeriveEffects` (full effect-match loop) runs twice on cold cache | Tree/Reaches/Derive | **NOT A REDUNDANCY** (investigated, `1be1094f`) — the bounded tree-path derive and the whole-store hazard-augmented `DeriveHazardEffectsAsync` use different complementary inputs; merging would change semantics |
| F6 | `RuleSetLoader.LoadMergedDocument` re-run for fingerprinting (4× total per command) | **FIXED** (`1be1094f`) | derive + Tree + Impact + EntryPointContext.Materialize now use out-param `Load` + `ComputeFromPaths` (one caller, `LoadOrDeriveEpSiteKind`, has no nearby `Load` — left) |
| F7 | `StoreLayout.ResolveReadStoreDir` (io:read ×7) resolved in `OpenReadContext` AND again for `StoreKey` | Derive | **FIXED** (`78dbe9c2`) — `OpenReadContext` surfaces the dir via out-param, reused for `StoreKey` |
| F8 | `LoadStaticField{Write,Read}RefsAsync` — two reads, identical base query | **FIXED** (Derive + Impact) | derive + impact (both sides) use the combined `…AccessRefsByKindAsync` (`78dbe9c2`); Tree already routes through the shared `DeriveHazardEffectsAsync` (combined) |
| F9 | `LoadDeploymentsAsync` (io:read ×3) loaded in `RunEntryPointsAsync` AND again at depth-1 | Callers | **FIXED** (`78dbe9c2`) — `DeploymentMap` loaded at the call site, threaded into `RunEntryPointsAsync` |

Cross-EP heavy shared methods (benign at once-per-command, the F1–F9 cases are the >once ones):
`LoadFactGraphAsync` (7/9 EPs), `LoadFactEntryPointDataAsync` (7/9), `LoadDeploymentsAsync` (7/9),
`DeriveEffects` (4/9), `RuleSetLoader.Load` (9/9). The `LoadShapedGraphAsync` consolidation (above) plus a
shared per-command `DeploymentMap` cache and threading already-loaded data into callees would clear most of
the open rows; F6's non-derive instances want `RulesFingerprint` to accept pre-resolved paths everywhere.

### Residual follow-ups surfaced by the work

- **Route EF-fallback `TraversalGraphLoader` through `LoadShapedGraphAsync`. — WON'T DO.** The consolidation
  (`9caef5d1`) routed derive + impact through the single shaped-graph loader, closing impact's `--async`
  delivery-edge gap — but the EF-fallback traversal loader (reaches/tree/path/callers when not on the SQL
  `call_edges` path) was left doing its own `LoadFactGraphAsync + ShapeGraph + MarkEventSubscriptionHandoffs`
  WITHOUT `AddDeliveryEdges`, so those fallback paths don't see delivery edges. **Decided not to fix
  (2026-06-23):** it's a corner case — the EF-fallback only triggers when `rig graph` hasn't run (no
  `call_edges`: `--no-graph` or pre-graph stores), and every modern graph-by-default index takes the SQL path
  where delivery edges are baked into `call_edges`. The fix is delicate (shaping is split between the loader's
  `ShapeGraph` and the command's `MarkEventSubscriptionHandoffs`, so threading `AddDeliveryEdges` in with the
  load-bearing ordering — `AddDeliveryEdges` must precede `MarkEventSubscriptionHandoffs`, the one that cost a
  24→0 `event_cycle` regression — is fiddly) and is not validatable on the MedDBase store (which has
  `call_edges` and never hits the fallback) without constructing a `--no-graph` store. The risk to the
  contended shaping path outweighs fixing a fallback modern indexes don't reach; left as a known limitation.
- **`seen` flag: split into `seen` vs `depth-capped` via a `TruncationCause` on `TraceNode`. — DONE**
  (`861bd0c4`). `TruncationCause { None, AlreadyExpanded, DepthCapped, BudgetCapped }` is set by precedence in
  `BuildTree`; the llm `seen` flag maps only to AlreadyExpanded, with distinct `depth-capped`/`budget-capped`
  flags and `seen:<id>` back-ref only for AlreadyExpanded. Tree payload-schema version bumped v1→v2.

---

## Detector coverage gaps (RCA production corpus)

Source: `meddbase-analysis/docs/rca-corpus-meddbase.md` (real production reverts/fixes), made executable by
`tests/Rig.Tests/Fixtures/ProductionFixCorpus.cs` + `…/Analysis/ProductionFixCorpusTests.cs` — each bug is
compiled in-memory and run through the real extract→derive with shipped rules; `_Gap_`-named tests pin a
KNOWN blind spot. **Status (2026-06-23): 4 of 7 FR families implemented + corpus-proven** (FR-1/1b shared-
mutation-under-concurrency *candidate*; FR-3 N+1 looped read; FR-4/1e per-EP effect/read-set + hazard delta in
`impact`; FR-6 unserializable `object_store` payload). The uncovered families, promoted here:

- **FR-7 — cache coherence (entity_cache write with no matching invalidation). NOT IMPLEMENTED — biggest open
  opportunity.** Maps the largest RCA cluster: !7721 (Redis entity-cache invalidation), #4199 (import doesn't
  invalidate person cache), #3941 (billing↔import invalidation missing), #4367/#4235 (signing-key cache miss),
  #940 (corrupted cache keys via race). Likely shape: a derive-side reachability rule — an `entity_cache:write`
  (or its keyed variant) reachable on an EP whose reach lacks a corresponding invalidation call for the same
  key/region. Design first: what counts as an "invalidation", per-key vs blanket, and how to avoid the FP class
  FR-1 hit (disclose candidate, don't claim proof). Ship with a corpus fixture per mapped case.
- **FR-1 PRECISION (not recall) — the pinned `_Gap_` sub-patterns. PARTIALLY DONE (`039d2eec`).** FR-1 already
  fires (recall is fine); the gap is false positives + uncoupled findings.
  - **DONE this pass** (the triage half — UX panel #2, no new extraction): `#cctor` exemption (CLR type-init
    lock → not a race; was a `lazy_init_race` FP class), per-`(type, method)` dedup with a `×N` count in the
    rollup (the 26-site `HandleSettingsToBeLogged` cluster → one row), and a `--exclude-namespace` filter for
    framework/vendored noise. Validated on MedDBase (`#cctor` 16→0, real findings survive).
  - **STILL OPEN** (needs NEW extraction + a re-index, NOT query-side): (a) **#2930** TOCTOU coupling /
    conditional-overwrite-vs-true-RMW — distinguishing `S.X = f(S.X)` (real RMW) from `S.X = independentValue`
    (conditional overwrite, agent C's dominant `high`-tier FP) needs a fact for whether a write's RHS DEPENDS
    on the read cell; the extractor doesn't capture it today. (b) **#4246** lock-attribution across a
    wrapper/callback boundary — needs cross-method happens-before/span propagation. (c) **#2892** quantified
    per-EP query-count. These are the FR-1 follow-up; until then race_window stays a disclosed candidate.
- **FR-2 — AsyncLocal/ThreadStatic flow + deadlock / lock-ordering. WON'T DO (declined by design).** Motivating
  bugs (!10208 ThreadStatic→AsyncLocal, !7194 SQL background deadlock, #311) stay pinned in the corpus as named
  targets, but detecting them needs AsyncLocal/ThreadStatic *flow* modeling and lock-ordering analysis — both
  beyond the fact-based, query-time reachability model (same boundary as the "no path-sensitive analysis"
  principle). Recorded so it isn't re-attempted; revisit only if rig ever grows a real type/value-flow pass.

---

## Reach post-commit callbacks (`DoWhenCommitted`) — effects fire but aren't reachable from the EP

> **SUPERSEDED / MISDIAGNOSED (verified 2026-06-23).** This item's *cause* is WRONG. `DoWhenCommitted`
> lambdas are NOT sync-cut — a `methodGroup`→lambda edge is walked synchronously (proven: in-repo unit test
> `tests/Rig.Tests/Domain/DoWhenCommittedHandoffTests.cs`, and a recursive walk from the exact
> `AbsenceRecordEntity.Save` node reaches its `~λ0/~λ1` + `LogAbsenceRecordAdded`). The webhook on the
> SaveLetter path is a plain `invocation` from `DocumentEntity.Save`, not a deferred lambda. The real cause
> of `reaches SaveLetter --only webhook,audit = 0` is the **external-virtual-override orphan** below — see
> that section. A `handoffDispatchers` rule for `DoWhenCommitted` would do nothing for it and would *reduce*
> recall (reclassifying currently-walked lambda edges to sync-cut handoffs). Do NOT build the fix described
> here. (The deferred-vs-synchronous *precision* question — should commit callbacks be modeled as deferred
> at all — is a real but separate, lower-priority semantic question.)

Surfaced closing the UX-panel "missing effects" loop (the `webhook:emit` + `audit:write` rules added to
MedDBase `rig.rules.json`, 2026-06-23): both effects are now MODELED and fire at the right sites (e.g.
`DocumentEntity.TriggerDocumentWebhook`, the `auditLogEvent.Log()` sites), but `reaches SmartLetter.SaveLetter
--only webhook,audit` is **0 even with `--async`**. Cause: on the document-save path these run inside
`DoWhenCommitted(() => …)` *deferred transaction-commit callbacks* — the effect's enclosing method is the
commit-callback lambda (`…~λ0`), which today is NOT on a handoff class rig walks, so it's sync-cut and
`--async` doesn't reach it either. So the effect is greppable store-wide but invisible from the entry point
that triggers it.

Likely fix is a **rule, not engine work** (correcting my first take): `DoWhenCommitted(Action)` is the
classic "delegate handed to a dispatcher to run later" handoff shape, so a `handoffDispatchers` entry
(per-repo data) should let the classifier promote the commit-callback lambda to a walked handoff edge —
making its effects reachable under `--async`, tagged as scheduled, exactly like timer/actor/event callbacks.
TO VERIFY before building: (a) confirm `DoWhenCommitted`'s registration is co-located-methodGroup/lambda
shaped (what `handoffDispatchers` matches) vs. needing a delivery-rule or genuine engine support; (b) decide
the semantics tag — it's deferred-but-SAME-THREAD (runs at commit, not cross-thread), so it should walk under
`--async` but ideally not be mislabelled `cross_thread`. Scope: start with the `DoWhenCommitted` dispatcher
on the MedDBase store, calibrate (does `SaveLetter --async` then reach the audit/webhook?), then generalize.

---

## External-virtual-override orphans — first-party overrides unreachable through an external base call

**Root cause of the "DoWhenCommitted" symptom above (verified 2026-06-23, in-repro + MedDBase store).** A call
to a method *declared on an external base class* whose **first-party override** carries the effect:
`document.Save()` (parameterless) statically binds to `M:SD.LLBLGen.Pro.ORMSupportClasses.EntityBase.Save`
(external, `TargetInSource=0`). The graph-load filter (`TargetInSource &&` in `Reads.LoadFactGraphAsync` /
`FactProjection.GraphData`) **drops that edge**, so `NewTextDocument` never reaches
`DocumentEntity.Save(IPredicate,bool)` — the override that fires `webhook`/`audit`/cache-invalidation/
`OnDataChanged`. The 0-arg convenience method trampolines to the virtual `Save(IPredicate,bool)` *inside the
external DLL* (invisible to rig). rig already mines the override chain from the 2-arg virtual down
(`EntityBase.Save(IPredicate,bool) ← CommonEntityBase.Save ← ~114 entity overrides`); **only the 0-arg→2-arg
hop is missing.**

- **`dead` does NOT catch this.** The overrides stay reachable via the parallel 2-arg `.Save(pred,recurse)`
  path (121 sites → `CommonEntityBase.Save` → dispatch fan; all 114 overrides have inbound edges, zero
  orphaned). The gap is PATH-specific (the 0-arg sites miss), not global; dead-code is a zero-reacher signal,
  blind to a missing edge when a parallel path keeps the target alive. (Total orphaning — a codebase using
  *only* parameterless `.Save()` — WOULD surface as a dead-code FP cluster, which is the tell.)
- **Blast radius (heuristic scan, validated 2026-06-23):** external-virtual targets (`TargetInSource=0`,
  first-party receiver) whose same-named method is overridden first-party — a **name-stripped** join
  (`reference_facts` × `dispatch_facts` override; signature stripped so the 0-arg call target matches the
  2-arg override base — exact-DocID would miss it). Top hits on MedDBase: `EntityBase.Save` **1614**,
  `EntityBase.Delete` **320**, `EntityCore.ValidateEntity` 34, `EntityBase.OnFetchComplete` 29, `OnSave` 11,
  `OnDelete` 5, plus framework hooks (`Page.OnInit`, `Hub.OnDisconnected`). Low-value same-signature overrides
  (`ToString`/`GetHashCode`) sort to the bottom by site count; a "reaches an effect" filter drops them.

### Confirmed trampoline map (LLBLGen `SD.LLBLGen.Pro.ORMSupportClasses`, net452 DLL, decompiled 2026-06-23)

Ground-truth from decompiling `EntityBase` (netstandard2.0 copy identical). The **4 redirect candidates** —
all on `EntityBase` (SelfServicing; `EntityBase2`/Adapter has no parameterless `Save`/`Delete`, so every
flagged 0-arg site is necessarily `EntityBase`-derived → anchor rules on `EntityBase` only). Each is a single
direct `this.`-call to the virtual, no reflection/multi-hop:

```
EntityBase.Save()            → EntityBase.Save(IPredicate, bool)   // Save(GetConcurrencyPredicate(...), recurse:false)
EntityBase.Save(bool)        → EntityBase.Save(IPredicate, bool)
EntityBase.Save(IPredicate)  → EntityBase.Save(IPredicate, bool)
EntityBase.Delete()          → EntityBase.Delete(IPredicate)
```

**NOT candidates** (single virtual overload, no convenience form → nothing to bridge): `OnSave`, `OnDelete`,
`OnFetchComplete`, `EntityCore`1.ValidateEntity`, `PreProcessValueToSet`. Why they appeared in the name-stripped
scan but are benign: a call binds to an external **same-signature** virtual only when the *receiver has no
first-party override of it* (else it binds first-party) — so there is nothing to reconnect. **Heuristic
refinement for the skill:** a true candidate requires the receiver to override a **different** overload than
the one called (overload mismatch); same-signature matches are non-orphans and must be excluded.

Two design facts the map forces (see route below): (1) the redirect *target* (`Save(IPredicate,bool)`) is
itself external (`TargetInSource=0`), so the mechanism must KEEP the redirected edge and let receiver-narrowed
dispatch resolve it to the first-party override — not just rewrite the callee (which would re-drop it). (2) the
rule source must match the *specific convenience signatures*, never the virtual target itself (no self-redirect).

### STATUS: Phase A DONE (2026-06-23) — `redirectRules` shipped + calibrated on MedDBase

Implemented end-to-end: `FactRedirectRule` + `RedirectClassifier` (Domain), the `redirectRules` schema +
`FactRedirectRuleProvider` + `RuleSet.Redirect` + loader cascade-merge, and the projection hook in BOTH
`FactGraphProjection.FromAnalysis` (`rig index`) and `Reads.LoadFactGraphAsync` (`rig graph` / EF-fallback),
threaded through `GraphMaterializer`/`TraversalGraphLoader`. The 2 LLBLGen rules
(`EntityBase.Save → Save(IPredicate,bool)`, `EntityBase.Delete → Delete(IPredicate)`) are in MedDBase
`rig.rules.json`. Tests: `ExternalVirtualOverrideOrphanTests` (RED orphan + GREEN reconnect via real
two-assembly extraction), `RuleSetLoaderTests.RedirectRules_round_trip…` (the cascade-merge regression — the
bug real-store calibration caught, since rule-constructing tests bypass the loader). Full suite 565 green.
MedDBase calibration (re-graph): `reaches SmartLetter.SaveLetter --only webhook,audit` **0 → 7** (1 webhook
via `DocumentEntity.TriggerDocumentWebhook` + 6 audit); +1,988 call_edges; redirect edges **2108
receiver-narrowed / 43 null-receiver CHA-fan** (98% precise); `event_cycle` **24** (unchanged — no regression).
Residual: the 43 null-receiver edges over-approximate (standard CHA disclosure); the `dead` detector still
can't see partial orphans (parallel-path-masked) — both noted, not blocking.

### Chosen route: a projection-time `redirectRules` rule (NOT decompilation, NOT `handoffDispatchers`)

A new rule kind that, at the **reference-fact→CallEdge projection** (BEFORE the `TargetInSource` filter — the
edge is already gone post-filter), rewrites a call to external `EntityBase.Save` (any overload) with
first-party receiver `T` → `T`'s `Save(IPredicate,bool)` override (receiver type is already mined → no CHA
fan-out); existing dispatch carries it the rest of the way.
- **Phase A (mechanism):** `redirectRules` schema + the projection hook, proven by an in-memory
  **two-assembly** RED→GREEN repro (external base ⇒ `TargetInSource=0` — the only vehicle that reproduces the
  drop; a single-source fixture would be `TargetInSource=1` and show no bug). Then calibrate on the MedDBase
  store (the scan above = the target set; verify SaveLetter→webhook reconnects; watch `event_cycle`/`impact`
  deltas — adding ~1,900 edges is a large but CORRECT reach increase, so calibrate before on-by-default).

### Backlog items

1. **Pack a rule-extraction skill.** Automate the heuristic scan (external-virtual-override orphans), rank by
   blast radius, propose `redirectRules` JSON with a per-rule reach-delta preview, human-in-the-loop (never
   auto-apply; FP-calibrate like every detector). **Downstream of Phase A** (it proposes rules of a kind the
   engine must already understand). Playbook skill first (`SKILL.md` + the mining query); promote to a
   `rig suggest-rules` native command only if it earns repeated use. Generalizes later to mine other families
   (candidate effects, `handoffDispatchers`). This is "detectors are data, mined from the codebase."
2. **Analyze which external assemblies to decompile for white-box rule extraction.** Investigate the decompile
   route as an *offline rule-GENERATION aid* (not a runtime subsystem): IL-read the external trampolines
   (LLBLGen `ORMSupportClasses`, LanguageExt, Echo, `System.Web`/SignalR lifecycle) to auto-discover
   `X() → callvirt X(args)` self-trampolines and emit `redirectRules`. Keeps runtime rule-based; sidesteps the
   runtime-decompile costs (DocID-identity-at-scale, fact-store bloat, the two-stage-philosophy break — see the
   decompile analysis in session notes). **Deliverable:** a ranked list of assemblies worth decompiling + the
   trampoline patterns each yields.

---

## Generated-code indexing policy: index everything; derive page EPs from source, not generated proxies

**Decision (2026-06-23): index generated code — NO `files.exclude` for generated, and NEVER a `<auto-generated>`
blanket exclusion.** The redirect work surfaced that the MedDBase `files.exclude` glob `**/*EntityBase.cs`
(intended for LLBLGen generated entity bases) also matched the HAND-WRITTEN `MMSHelperClasses/CommonEntityBase.cs`
— the partial holding `Save`/`Delete` + the `DoWhenCommitted` cache/audit/webhook wiring. Once `rig index` was
fixed to honor cwd rules, that exclusion finally applied and silently broke every entity-save reachability chain
(`CommonEntityBase.Save` dropped → hatch had no dispatch fan → redirect dead-ended → SaveLetter→webhook went 0).
Removing the generated exclusions (index everything) fixed it. Measured cost: **+22% index time / +24% store /
+54% symbols**, with NO analysis distortion (`event_cycle` stayed 24; the extra symbols are benign leaf accessors).

**Why no exclusion at all — "generated ≠ noise."** The same `<auto-generated>` header sits on BOTH pure boilerplate
(LLBLGen `*EntityBase.cs` — field/relation accessors, zero EPs/effects, safe to drop) AND behaviorally-essential
code (`*_RequestProxy.g.cs` — the RequestResponseProxyGenerator output that is the source of **554 entry points**,
the client→server page-action seam). So `<auto-generated>` is a bulletproof *is-generated* signal but the WRONG
predicate: there is no clean marker for *is-noise vs is-structure* — that needs per-generator judgment, exactly the
brittleness that bit us. Index-everything is the safe default; any future size-driven exclusion must target a
SPECIFIC proven-noise generator (e.g. LLBLGen entity bases via the `LLBLGen Pro` header, verified to contain zero
EPs/effects) and NEVER a `<auto-generated>` blanket.

**Follow-up (worth pursuing): derive page EPs from SOURCE rules, not the generated proxies.** Today 554 page entry
points are derived from the GENERATED `*_RequestProxy.g.cs`. The proxy is an INDIRECT artifact; the DIRECT signal is
whatever the proxy generator itself keys off in the page SOURCE (page-action declarations / attributes / handler
conventions). If rig's entrypoint rules detected those page-action EPs directly from source — extending the existing
`meddbase.clientaction`/page-EP rules to cover the full set the generator produces — EP detection would be decoupled
from the generated artifact: more robust (survives generator-format changes) AND it would re-open the option of
EXCLUDING the generated proxies for store size WITHOUT losing the 554 EPs. Scope: map what the
RequestResponseProxyGenerator keys off in page source → express as entrypoint rules → verify the 554 EPs reproduce
from source → only then are the proxies excludable. Until then, index everything (the proxies carry the EPs).

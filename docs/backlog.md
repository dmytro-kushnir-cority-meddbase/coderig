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

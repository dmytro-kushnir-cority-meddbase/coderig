# `rig` Query Storage & Retrieval Strategy

Status: research / architecture proposal (2026-06-13). No implementation in this document.
Target store for all measurements: `C:\Git\meddbase-analysis\.rig\rig.db` (3.32 GB, 1 run).
Baseline command: `rig tree Master.SubmitToHealthcode --full` — cold 16.0s, ~95% IO/SQL.

All timings below are `sqlite3` CLI against the real store with `.timer on`, warm-ish OS file cache
(numbers are *lower bounds* for the C# path, which adds DataReader marshalling on top). They are used
to size proposals, not as absolute latency.

---

## 0. Executive summary

The 16s is **not** an inherent cost of the data volume. Two specific, fixable defects dominate:

1. **A catastrophic join-order misplan in Load 1.** Every `reference_facts JOIN reach_set` query
   (invocations / ctors / throws) is planned as `SCAN reference_facts` (2.21M rows) probing the temp
   table, instead of scanning the tiny `reach_set` (hundreds of rows) and probing
   `IX_reference_facts_EnclosingSymbolId`. SQLite has **no statistics on the TEMP table**, so it
   guesses wrong. One `ANALYZE reach_set` (or an inlined-rowid materialisation) flips the plan and
   takes the invocation read from **0.93s → 3ms** — measured, same query, same result. This single
   change removes the bulk of Load 1's 4.9s of row reads.

2. **`DeriveHandoffEntryPoints` rebuilds the whole graph to re-derive data already persisted.** It
   calls `LoadFactGraphAsync` (scans 539k `reference_facts` rows through EF, marshals every one into a
   `CallEdge`, ~3.7s) only to feed `HandoffClassifier.HandoffEntryPoints`, which reads **only** the
   `handoff` (170) + `methodGroup` (4768) edges — **all of which `rig graph` already wrote into
   `call_edges` with `Kind` + `HandoffDispatcher` columns**. Reading those 4938 edges directly is
   0.46s cold / sub-ms warm. This kills the single biggest sub-step (3.69s) and is pattern-independent
   so it also helps every other query that triggers Load 2.

Neither requires a schema redesign. Together they target ~8.5s of the 16s. The recommended sequence
(section 6) does these two first, then a single-round-trip consolidation of Load 1, then connection
pragmas. The more invasive ideas (CSR adjacency blobs, columnar) are **deferred** — the evidence says
they are not where the time is.

---

## 1. Current store: schema + index inventory (from sqlite3)

### Row counts (this store)

| Table | Rows | Notes |
|---|---:|---|
| `symbol_facts` | 326,882 | 217,719 methods, 24,042 types |
| `reference_facts` | 2,210,522 | the big one; see RefKind split |
| `dispatch_facts` | 27,900 | Roslyn-mined exact dispatch |
| `type_relation_facts` | 17,655 | base/interface edges |
| `call_edges` (derived) | 532,799 | 459,997 invocation · 67,864 ctor · 4,768 methodGroup · **170 handoff** |
| `dispatch_edges` (derived) | 10,334 | |
| `nodes` (derived) | 254,883 | edge endpoints ∪ methods, `WITHOUT ROWID` |

`reference_facts` RefKind split: invocation 740,941 · typeUse 674,628 · read 569,998 ·
ctor 136,632 · write 75,894 · throw 6,953 · methodGroup 5,476.

### Indexes that exist

- `reference_facts`: `IX_..._TargetSymbolId`, `IX_..._EnclosingSymbolId`, `IX_..._RunId_TargetSymbolId`, PK `(RunId, ReferenceFactIndex)`. **No index on `RefKind`.**
- `symbol_facts`: `IX_..._Name`, `IX_..._SymbolId`, `IX_..._RunId_SymbolId`, PK `(RunId, SymbolFactIndex)`. **No index on `Kind`.**
- `call_edges`: `IX_call_edges_FromSym`, `IX_call_edges_ToSym`. **No index on `Kind`.**
- `dispatch_edges`: `IX_..._FromSym`, `IX_..._ToSym`.
- `nodes`: PK on `sym` (`WITHOUT ROWID`).
- `type_relation_facts`: `IX_..._TypeSymbolId`, `IX_..._RelatedSymbolId`.

### Trigram FTS already present (relevant to Direction 3)

`rig graph` ALREADY builds two FTS5 trigram virtual tables (`GraphMaterializer.BuildSearchIndexAsync`):
- `symbol_fts` — trigram over `symbolid, name`, one row per distinct SymbolId; carries display payload UNINDEXED.
- `ref_target_fts` — trigram over distinct `TargetSymbolId`.

`Reads.SearchSymbolsAsync` / `FindReferencesAsync` use these for `rig symbols` / `rig refs` when
pattern ≥ 3 chars. **The trigram infra Direction 3 asks for is built and wired** — see §4.3.

### Connection configuration

Query path opens `Data Source=...;Mode=ReadOnly` (RigDbContext) with **no PRAGMAs**:
`PRAGMA mmap_size=0`, `PRAGMA cache_size=-2000` (2 MB), default `temp_store`. Only the *writer*
(`Writes.cs`) tunes pragmas. A 3.3 GB DB is being queried through a 2 MB page cache with memory-mapped
IO off — every cold scan is a syscall-per-page read.

### EXPLAIN QUERY PLAN — the hot queries

**Load 1 — the join that dominates (invocations; ctors/throws/graph identical shape):**
```
SELECT r.* FROM reference_facts r JOIN reach_set s ON r.EnclosingSymbolId = s.sym WHERE r.RefKind='invocation';
QUERY PLAN
|--SCAN r                                            ← scans all 2.21M reference_facts rows
`--SEARCH s USING COVERING INDEX sqlite_autoindex_reach_set_1 (sym=?)
```
This is the bug. `reach_set` for a real query (`%Healthcode%`) has **232 rows**. The right plan is
`SCAN reach_set` → `SEARCH reference_facts USING IX_..._EnclosingSymbolId`. SQLite picks the wrong
driver because the TEMP table has no `sqlite_stat1` row, so it estimates `reach_set` at the default
~1M rows and `reference_facts` at its true count, and "scan the smaller, probe the larger" inverts.

Measured, same query, same 1302-row result:
| Variant | Time |
|---|---:|
| Current plan (SCAN reference_facts) | **0.927 s** |
| `INDEXED BY` forcing the EnclosingSymbolId probe (no stats) | 1.48 s (worse — see note) |
| **After `ANALYZE reach_set`** (plan flips to `SCAN s` → `SEARCH r`) | **0.003 s** |
| Single pass, all 3 refkinds in one scan, after ANALYZE | **0.003 s** |

Note the `INDEXED BY` row is *slower* than the scan: forcing the index without flipping the driver
makes it scan reference_facts AND do per-row index lookups. The fix is **statistics**, not a hint —
once SQLite knows `reach_set` is tiny it chooses the nested-loop from the small side itself.

**Load 2 — whole-store scans (pattern-independent):**
```
SELECT ... FROM reference_facts WHERE EnclosingSymbolId IS NOT NULL AND TargetInSource=1
          AND RefKind IN ('invocation','methodGroup','ctor');
QUERY PLAN
`--SCAN reference_facts            ← 539,539 rows match; 0.96 s just in SQLite, before EF marshalling
```
```
SELECT sym FROM nodes WHERE sym LIKE '%SubmitToHealthcode%' ESCAPE '\';   → SCAN nodes (expected; LIKE)
SELECT ... FROM symbol_facts WHERE Kind='method';                          → SCAN symbol_facts (0.14 s)
```

---

## 2. Why the time is where it is (synthesis)

- **Load 1 (7.6s)**: ~85% is the 4 separate `reference_facts`/`symbol_facts`/`call_edges` joins each
  *scanning the big table* due to the misplan, then marshalling. The recursive CTE (2.14s) is real but
  secondary, and itself inflated because `reach_set` is a **receiver-blind CHA superset** — it pulls
  far more of the graph than the rendered tree, so every downstream join processes a bloated closure.
- **Load 2 (6.8s)**: `LoadFactEntryPointData` (2.15s) + `DeriveHandoffEntryPoints` (3.69s) are BOTH
  whole-store EF reads, **identical for every query**, re-deriving things `rig graph` could persist.
  `DeriveHandoffEntryPoints` is pure waste: it rebuilds the 539k-edge graph to read 4,938 edges that
  are already columns in `call_edges`.
- The store is opened with a 2 MB cache and no mmap, so none of this benefits from the OS having
  warmed the file.

---

## 3. Direction-by-direction evaluation

### 3.1 Direction 1 — Single round-trip retrieval (collapse Load 1's 4 queries)

**Proposal.** Replace BuildReachSet + 4 follow-up joins with: (a) build `reach_set`, (b) **`ANALYZE
reach_set`**, (c) ONE query that emits the closure's reference rows tagged by RefKind in a single scan
(`WHERE RefKind IN ('invocation','ctor','throw')`), partitioned in C#; keep the graph/method reads as
their own (now index-driven) queries or fold them in via a `UNION ALL` tagged-union.

**Expected win.** The single tagged pass over the closure measured **3 ms** (vs 0.93 + 0.83 + ~throw
for the three separate scans ≈ 1.8–2s today, and that's the SQLite floor — the C# path is the full
1.34 + 1.73 = 3.07s). Realistically collapses Load 1's invocation + ctor + throw reads (~3.1s) to well
under 100 ms including marshalling. Round-trip count drops from 4 to 1–2.

**Crucial dependency.** The win is ~95% from the `ANALYZE` plan flip, ~5% from fewer round-trips. Do
**not** ship the union without the stats fix — a single scan that still SCANs reference_facts is no
faster. Conversely, `ANALYZE` alone (keeping 4 queries) already captures most of the win; the union is
a clean follow-on that also removes 3 redundant passes over the same closure.

**Implementation sketch.** In `LoadReachInputsAsync`, after `BuildReachSetAsync`:
`ANALYZE reach_set;` then a single `SELECT r.RefKind, r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath,
r.Line, <invocation-only cols...> FROM reference_facts r JOIN reach_set s ON r.EnclosingSymbolId = s.sym
WHERE r.RefKind IN ('invocation','ctor','throw')`. Invocation rows carry the full column set; ctor/throw
rows ignore the extra columns. Partition into the three lists in the existing `onRow` callback. The
`TypeArguments` sub-read and the method/graph reads also get the flipped plan for free once stats exist.

**Risks / equivalence.** Output equivalence is preserved — same rows, same dedup keys (ctor by
(file,line), throw by (file,line,target)), just one cursor instead of three. `ANALYZE` on a TEMP table
writes to the *temp* database, so it is legal on the `Mode=ReadOnly` main connection (same place the
temp tables already live). Watch: `ANALYZE` cost itself — on a 232-row temp table it was 0.14 ms;
negligible. For a pathological huge closure it stays cheap because it samples.

**Verdict: PURSUE (high leverage, low risk).** The `ANALYZE reach_set` line is the single
highest-payoff change in the whole document.

---

### 3.2 Direction 2 — Different physical layout (denormalise / covering / CSR / columnar)

**What's actually not index-covered today?** After the Direction-1 stats fix, the closure joins become
`SEARCH reference_facts USING IX_EnclosingSymbolId` — but that index is **not covering**: SQLite seeks
the index then does a rowid lookup into the table to fetch `TargetSymbolId, FilePath, Line, ReceiverType,
…`. For the bounded closure (hundreds–low-thousands of rows) this is fine. The whole-store Load-2 scans
are not index-coverable at all (they touch a third of the table).

**Sub-options:**

- **(a) Covering index on the closure read.** `CREATE INDEX IX_rf_enc_kind_cover ON reference_facts
  (EnclosingSymbolId, RefKind) ` — or a wider covering index including the marshalled columns. Verdict:
  **DEFER.** After the stats fix the bounded read is already ~ms; a covering index adds write cost at
  index time and storage to a 3.3 GB DB for a read that is no longer hot. Reconsider only if profiling
  after Directions 1+4 still shows the rowid lookups as material.
- **(b) Adjacency as CSR blob per node / JSON per node.** Store, per symbol, a single blob of its
  out-edges (and the effect-relevant refs). Verdict: **DEFER / likely REJECT.** This is a large new
  materialised structure and a second source of truth for the call graph that must stay bit-identical
  to `call_edges` or the in-memory `FactPathFinder` diverges (the equivalence constraint). The win it
  targets — adjacency walk cost — is the recursive CTE (2.14s), which is *already* index-driven on
  `FromSym/ToSym` and is secondary to the marshalling. CSR shines for whole-graph traversal in RAM, but
  rig's whole point is to NOT load the whole graph. Bounded closures don't benefit enough to justify
  the divergence risk.
- **(c) Columnar.** Verdict: **REJECT** for this engine. SQLite is row-store; columnar would mean a
  different format (e.g. Parquet/DuckDB), a rewrite of the storage layer, and loss of the read-only
  SQLite invariant the architecture depends on. Out of scope.
- **(d) Index on `RefKind` / `Kind`.** A standalone `RefKind` index is low-selectivity (7 distinct
  values over 2.2M rows) and won't be chosen; only useful *composite* with EnclosingSymbolId (= option
  a). Verdict: **fold into (a), DEFER.**

**Verdict for Direction 2 overall: DEFER.** The physical layout is not the bottleneck once the
misplan (D1) and the redundant whole-store rebuild (D4) are gone. Covering index is the only piece
worth revisiting, and only post-measurement.

---

### 3.3 Direction 3 — Trigram / 3-char infix index for pattern search

**Finding: already built and wired — for `rig symbols`/`rig refs`, but NOT for the tree/reach SEED.**

`rig graph` builds `symbol_fts` and `ref_target_fts` (FTS5 trigram). `SearchSymbolsAsync` /
`FindReferencesAsync` use them. **But the reachability seed** — `SeedSql` in `SqlReachability.cs` —
still does `nodes WHERE sym LIKE $pat` (or the 4-way edge-column LIKE fallback), which is a SCAN.

For `rig tree Master.SubmitToHealthcode`, the seed LIKE over `nodes` (254k rows, `WITHOUT ROWID`) is
fast in absolute terms (~ms in EQP) and is **not** in the measured hot path — BuildReachSet's 2.14s is
the recursive walk, not the seed. So a trigram-backed seed is a **correctness/consistency** improvement
more than a latency one *for tree*. It matters for latency only when the seed pattern is short/common
and `nodes` LIKE returns a large seed set.

**Proposal.** Add a `node_fts` trigram (or reuse `symbol_fts.symbolid` — `nodes` ⊆ symbol ids ∪ edge
endpoints) and seed via MATCH for patterns ≥ 3 chars, LIKE fallback below 3. This gives the seed the
same index-backed substring semantics the search commands already enjoy, and unifies the "find symbols
by infix" code path.

**Parent/child / DocID-prefix ("infix via 3-char index for parent-child hacks").** DocIDs are
hierarchical (`M:Ns.Type.Method(args)`). Prefix/containment queries ("everything under `Ns.Type.`")
are *prefix*, not infix — a plain B-tree on `sym` already serves `sym LIKE 'M:Ns.Type.%'` (no leading
wildcard → index range scan). Trigram is the right tool for **infix** (`%Healthcode%`); a normal index
is the right tool for **prefix**. Recommendation: keep trigram for infix, and ensure prefix/child
lookups are written as anchored LIKE (no leading `%`) so they range-scan the existing `sym` PK on
`nodes` / `IX_symbol_facts_SymbolId`.

**Risks / equivalence.** Trigram MATCH must reproduce LIKE's case-insensitive ASCII substring
semantics for the seed set or the closure changes. The search commands already rely on this equivalence
(documented in `SearchSymbolsAsync`), so the pattern is proven; reuse it. Trigram needs ≥3 chars — keep
the LIKE fallback for short patterns (already the established convention).

**Verdict: PURSUE (small), but as consistency/robustness, not as a top-3 latency fix.** Unify the seed
on the existing trigram infra so short-pattern / high-fanout seeds stop scanning; low effort because the
infra and the equivalence argument already exist. Not in the critical path for the benchmark command.

---

### 3.4 Direction 4 — Reusable, multi-level-cacheable derived model (materialise at `rig graph`)

This is where Load 2 dies. Two concrete materialisations:

**(a) Materialised handoff entry-point table — kills the 3.69s `DeriveHandoffEntryPoints`.**

`DeriveHandoffEntryPointsAsync` → `LoadFactGraphAsync` (scan 539k refs, EF-marshal to CallEdges, 3.7s)
→ `HandoffClassifier.HandoffEntryPoints`, which consumes **only** edges with `Kind ∈ {handoff,
methodGroup}`. Those edges, with their `HandoffDispatcher`, are **already persisted in `call_edges`**
(verified: 170 handoff + 4768 methodGroup rows carry Kind + HandoffDispatcher). Reading them directly:
```
SELECT FromSym, ToSym, Kind, FilePath, Line, HandoffDispatcher
FROM call_edges WHERE Kind IN ('handoff','methodGroup');   → 0.46 s cold, ~ms warm
```
The only field not on the row is the rule's `Requires`/`Kind` token list, which is looked up from the
rules JSON by `HandoffDispatcher` id — a trivial in-C# join the classifier already does via `ById`.

**Proposal — two tiers:**
  1. *Minimum:* change `DeriveHandoffEntryPointsAsync` to read the `handoff`+`methodGroup` edges
     directly from `call_edges` (add `CREATE INDEX IX_call_edges_Kind ON call_edges(Kind)` so the read
     is index-driven, since only ~5k of 533k rows match) and feed them through the SAME
     `HandoffClassifier.HandoffEntryPoints` (output equivalence by construction — same function, same
     edge set, just sourced from the persisted column instead of re-derived). This alone removes ~3.5s.
  2. *Fuller:* have `rig graph` persist the finished `handoff_entry_points` table (Target, RegisteredIn,
     FilePath, Line, Dispatcher, Kind). Then query-time reads N rows with zero classification. Marginal
     gain over (1) since (1) is already ~ms warm, but it removes the per-query C# classify + the rules
     dependency at read time and gives a clean cacheable artifact. The `Requires` tokens are
     rules-derived so either persist them too or re-attach from rules at read (cheap).

  Because handoff EPs depend on `handoffRules`, persist a **rules-hash** alongside (the app already keys
  its EP-set cache by store identity + rules hash) so a rules change invalidates the materialised table
  / triggers a `rig graph` rebuild. Tier (1) sidesteps this entirely by classifying at read from raw
  edges, so it's the safer first step.

**(b) Materialised entry-point input / EP set — addresses the 2.15s `LoadFactEntryPointData`.**

`FactEntryPointData` (base edges, methods, types, ctor refs) is whole-store and pattern-independent.
`FactEntryPointDeriver.Derive` over it is only 0.47s CPU; the cost is the **load** (2.15s of EF scans +
marshalling). Options, increasing ambition:
  - Persist the **derived entry-point set** itself (the output of `FactEntryPointDeriver.Derive`) as a
    table at `rig graph` time, keyed by rules-hash, exactly as the handoff table. Query time then reads
    the EP list directly — no whole-store load, no derive. This is the natural generalisation of (a)
    and composes with the existing app-layer EP-set cache (it becomes the cold-load source that warms
    that cache, fixing reindex warm-up too).
  - The ctor-ref read inside `LoadFactEntryPointData` (0.83s, scans 134k ctor rows) is only needed for
    attribute-application EPs; if the EP set is materialised, this disappears with the rest.

**Why this is the right architectural shape.** `rig graph` already owns the
derive-once-persist-many pattern for `call_edges`/`dispatch_edges`/`nodes`/FTS. Handoff EPs and
structural EPs are the **same kind of artifact** — deterministic functions of (facts + rules) that are
currently recomputed on every query. Persisting them: (i) removes both Load-2 sub-steps from cold
latency, (ii) makes them pattern-independent reads that warm the app cache instantly on reindex, (iii)
keeps "detectors are data" intact — the *rules* still drive derivation at graph time, we just store the
*result* of applying them, tagged with the rules-hash so staleness is detectable.

**Risks / equivalence.** The materialised EP/handoff tables MUST be produced by the SAME
deriver/classifier functions the in-memory oracle uses (they already are — `GraphMaterializer` calls
`Reads.LoadFactGraphAsync` with the rules and `HandoffClassifier`). Persisting their *output* rather
than recomputing is equivalence-preserving **as long as** the rules-hash gate forces a rebuild when
rules change. The danger is a stale table after a rules edit without a `rig graph` — mitigate with the
rules-hash column + a `HasGraph`-style freshness probe that falls back to the live derive when the hash
mismatches (degradation pattern already used throughout the codebase for missing columns/tables).

**Verdict: PURSUE.** Tier-(a)-minimum (read handoff edges from `call_edges`) is high-leverage and
low-risk — do it first. The materialised EP tables (a-fuller + b) are the structural follow-up that
also fixes reindex warm-up; medium effort, high payoff, gated on rules-hash.

---

## 4. Additional structural findings (from the evidence)

1. **Connection pragmas on the read path.** The query connection uses default `cache_size=-2000` (2 MB)
   and `mmap_size=0` against a 3.3 GB file. Set, once per query connection (read-only safe):
   `PRAGMA mmap_size=1073741824;` (1 GB memory-mapped IO — turns page faults into mapped reads, big win
   on cold whole-store scans), `PRAGMA cache_size=-262144;` (256 MB), `PRAGMA temp_store=MEMORY;` (the
   `reach_set`/`reach_depth` temp tables + `ANALYZE` then live in RAM). This is orthogonal to all the
   above and helps **every** scan, cold latency especially. Low risk, low effort.
   **Verdict: PURSUE** (cheap multiplier; stack it under Directions 1+4).

2. **The receiver-blind CHA superset inflates everything downstream.** `reach_set` over-approximates
   the rendered tree, so every Load-1 join processes more rows than the output shows. The proper fix is
   large (push receiver-type narrowing into the SQL closure so the bounded set is tighter), and it risks
   output divergence (narrowing is currently a traversal-time concern the in-memory `FactPathFinder`
   owns — see `GraphMaterializer`'s comment on why cut/context-dispatch shaping is deliberately NOT
   baked). **Verdict: DEFER** — note it as the next frontier after the cheap wins; the row counts it
   would shave are already made cheap by the stats fix, so the payoff shrinks once D1 lands.

3. **`ANALYZE` the main store once at `rig graph` time.** The persistent tables also benefit from
   `sqlite_stat1`. Run `ANALYZE` (or `PRAGMA optimize`) at the end of `rig graph` so the planner has
   real selectivity for `RefKind`/`Kind`/join-cardinality on the persistent tables too. The TEMP-table
   `ANALYZE` (D1) handles the per-query closure; this handles the rest. Low effort.
   **Verdict: PURSUE** (pairs with D1).

4. **`IX_call_edges_Kind`** (or composite) is needed for D4-tier-1 to be index-driven; without it the
   handoff read scans 533k rows (still only 0.46s, but a 5k-row indexed read is ~ms). Add at graph time.

---

## 5. Equivalence ledger (where each proposal could diverge from the in-memory oracle)

| Proposal | Divergence risk | Guard |
|---|---|---|
| D1 `ANALYZE reach_set` + single union | None — same rows, same dedup | n/a (pure plan change) |
| D3 trigram seed | Trigram MATCH ≠ LIKE substring on edge cases | reuse the proven `SearchSymbols` equivalence; LIKE fallback <3 chars |
| D4a read handoff from `call_edges` | None — same `HandoffClassifier`, edges already classified at graph time | rules-hash already baked into `call_edges.Kind` at graph time |
| D4b materialised EP set | Stale table after rules edit w/o re-graph | rules-hash column + freshness probe → fall back to live derive |
| D2b CSR/blob adjacency | Second source of truth for the graph | (why it's deferred) |

---

## 6. Recommended sequence (highest leverage first)

| # | Change | Targets | Est. effort | Est. payoff |
|---|---|---|---|---|
| **1** | **`ANALYZE reach_set` after BuildReachSet** (and reuse for `reach_depth`) | Load 1 join misplan: invocation read 0.93s→3ms, ditto ctor/throw | **XS** (one statement) | **~3s of Load 1** |
| **2** | **`DeriveHandoffEntryPoints` reads `handoff`+`methodGroup` from `call_edges`** (+ `IX_call_edges_Kind` at graph time) | Load 2's 3.69s biggest sub-step | **S** | **~3.5s** |
| **3** | **Connection pragmas on read path** (`mmap_size`, `cache_size`, `temp_store=MEMORY`) + **`ANALYZE` main store at `rig graph`** | every cold scan, incl. Load 2's remaining 2.15s | **S** | **~1–2s cold + general** |
| 4 | **Collapse Load 1 to a single tagged-union pass** | round-trips + redundant closure passes | S–M | hundreds of ms (most already captured by #1) |
| 5 | **Materialise structural EP set at `rig graph`** (rules-hash gated) | Load 2's `LoadFactEntryPointData` 2.15s + reindex warm-up | M | ~2s cold + warm-up |
| 6 | **Trigram-backed reachability seed** (reuse `symbol_fts`) | seed robustness; short/high-fanout patterns | S | situational |

Changes 1–3 are independent, low-risk, and should land first — they target ~8.5s of the 16s with
essentially no equivalence risk (1 and 2 are provably output-identical; 3 is a pure IO multiplier).
Changes 4–6 are the structural follow-through. Directions 2b/2c (CSR, columnar) and receiver-narrowed
closures are explicitly **deferred** — the evidence shows the time is in a planner misplan and a
redundant whole-store rebuild, not in the physical layout.

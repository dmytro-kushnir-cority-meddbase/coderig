# `rig index` memory & wall-time optimization strategies (ranked)

Research-backed, ranked optimization plan for `rig index` on the MedDBase target (~123 projects,
~551k call edges, ~264k nodes, ~1.4M reference facts). **This is a strategy document — no code was
changed.** Ground-truth numbers come from the three `rig index --time` runs + dotMemory profiling +
dump composition already on file (summarized in the task brief); code citations are to the current
tree on branch `paket-per-project-invalidation`.

## TL;DR — what actually moves the needle

The profiling already settled two things, and reading the code settled a third:

1. **Extract-phase allocation churn (~80 GB cumulative, gc% 9–10%) is intrinsic Roslyn binder
   cost** (LINQ query lowering + error recovery on a partial compilation). It is **not** a wall-time
   wall at gc%≈9%. Chasing allocation-rate here is low-value for *time*.
2. **Peak working set (~9 GB) is structural**: `SolutionSourceLoader` calls
   `compilation.GetDiagnostics()` per project (fully binding every body — `SolutionSourceLoader.cs:188`)
   and then retains **every** `SemanticModel` in `sourceSet.IndexedSources` until after the whole
   extract phase (`SolutionAnalyzer.cs:51,125`). All bound-node caches for all 123 projects are
   co-resident. This is reducible, but only by **streaming extraction per project**.
3. **The real wall-time hog is save+graph (~45% combined) at ~3% CPU.** Reading the code, the
   `graph` phase (`GraphMaterializer`) is the prime offender and is **not** using the fast-write
   tuning the `save` phase already has — it opens a vanilla `RigDbContext` with **no PRAGMAs**
   (`IndexCommands.cs:532`), inserts ~815k edge rows with **`await ExecuteNonQueryAsync` per row**
   (`GraphMaterializer.cs:308,347`), then builds **two FTS5 trigram tables + `ANALYZE`** over the
   whole store (`GraphMaterializer.cs:103,109`). On a default 2 MB page cache with the rollback
   journal on, that is exactly where the "mysterious 7 GB of reads" comes from.

**The highest impact-per-effort work is in category B (SQLite), not the Roslyn stage.** Fixing the
graph-phase PRAGMA/journal gap is a few lines and should reclaim a large fraction of ~18–20% of wall
time. The Roslyn peak-memory win (streaming) is real but is a larger, correctness-sensitive refactor.

---

## Category B — save + graph SQLite I/O (the wall-time hog). Do this first.

The `save` path (`Writes.SaveFactsBatchedAsync`) is already well engineered: raw ADO with one reused
prepared statement, synchronous `ExecuteNonQuery` (deliberately — Microsoft.Data.Sqlite's "async" is
synchronous under the hood, see `Writes.cs:497`), one transaction, fast PRAGMAs, and **deferred
secondary indexes** rebuilt once at the end (`Writes.cs:317,487`). The `graph` path does **none** of
this. The recommendations below mostly bring `graph` up to `save`'s standard.

### B1. Apply the fast-write PRAGMAs to the `graph` connection ⭐ TOP PICK
**What:** In `MaterializeGraphAsync` (`IndexCommands.cs:532`) / `GraphMaterializer.BuildFromGraphAsync`,
issue the same per-connection PRAGMAs the save path uses (`Writes.cs:60`): `journal_mode=OFF`,
`synchronous=OFF`, `temp_store=MEMORY`, `cache_size=-262144` (256 MB — there's ~9 GB headroom),
`locking_mode=EXCLUSIVE`, and add `mmap_size` (see B2).
**Why here:** The graph phase currently runs on SQLite defaults: a **2 MB** page cache and the
**rollback journal on**. Every `DELETE FROM call_edges`, every edge INSERT, and especially the FTS5
build + `ANALYZE` page through a tiny cache and write+read a rollback journal. A 2 MB cache against a
multi-GB store guarantees constant eviction and re-reads — the direct mechanical cause of "graph reads
7 GB despite building from the in-memory graph." The store is already a throwaway-until-published temp
that the save path treats as crash-disposable; the graph runs on the same file, so journal-off is
equally safe here.
**Expected magnitude:** Large. This is the single change most likely to cut the graph phase's wall
time substantially (journal elimination + cache that actually holds the working set). Plausibly halves
the graph phase or better.
**Effort:** Low (a handful of lines; the save path is a copy-paste template).
**Correctness risk:** None for query correctness. Durability risk is the same the save path already
accepts (atomic-publish-via-rename; a torn temp is never published). Note `journal_mode=OFF` +
`locking_mode=EXCLUSIVE` is incompatible with WAL — pick the OFF/EXCLUSIVE family (matches save), not
WAL, on this publish path.
**Citations:** [SQLite PRAGMA reference](https://www.sqlite.org/pragma.html);
[phiresky, SQLite performance tuning](https://phiresky.github.io/blog/2020/sqlite-performance-tuning/);
[Database School — recommended PRAGMAs](https://databaseschool.com/articles/sqlite-recommended-pragmas).

### B2. Set `mmap_size` for the read-heavy graph build ⭐
**What:** `PRAGMA mmap_size=8589934592;` (8 GB, or sized to store) on the graph connection (and
arguably the read commands).
**Why here:** The FTS5 `INSERT … SELECT … FROM symbol_facts/reference_facts` and the `nodes`
`UNION` (`GraphMaterializer.cs:135,156,177`) and `ANALYZE` are **read-dominated** full scans of the
just-written fact tables — exactly the "7 GB reads" workload. Memory-mapped I/O serves those pages
from the OS page cache without per-page `read()` syscalls and without competing with the SQLite page
cache. mmap is most effective precisely for large sequential read scans like these.
**Expected magnitude:** Medium; compounds with B1 (B1 stops journal/eviction churn, B2 makes the
unavoidable big scans cheap).
**Effort:** Low (one PRAGMA).
**Correctness risk:** None. mmap_size is a soft hint; SQLite caps it at the compiled
`SQLITE_MAX_MMAP_SIZE`. On Windows the mapping is read-mostly here.
**Citations:** [SQLite mmap docs](https://www.sqlite.org/mmap.html);
[phiresky tuning post](https://phiresky.github.io/blog/2020/sqlite-performance-tuning/).

### B3. Defer FTS5 + `nodes` + `ANALYZE` index creation until after the bulk edge insert is already done — and confirm edge-table indexes aren't maintained per-row
**What:** The edge insert order is already index-light (the edge indexes are created in
`EnsureSchemaAsync` *before* the inserts, though — `GraphMaterializer.cs:231`). Apply the save path's
proven pattern: **create `IX_call_edges_*` / `IX_dispatch_edges_*` AFTER** the ~815k-row inserts, not
before, so each insert isn't maintaining three B-trees per row. The FTS5 tables and `ANALYZE` already
run after the commit, which is correct; keep them last.
**Why here:** "Create indexes after bulk load" is the canonical SQLite bulk-load win — CREATE INDEX
pre-sorts and builds the B-tree sequentially, vs. random-access maintenance on every INSERT. The save
path already does exactly this via `DropSecondaryIndexesAsync` (`Writes.cs:317`); the graph path
regressed by creating edge indexes up front in `EnsureSchemaAsync`.
**Expected magnitude:** Medium (815k rows × 3 indexes maintained per-row eliminated).
**Effort:** Low–medium (reorder schema/index creation in `GraphMaterializer`; the `CREATE INDEX IF
NOT EXISTS` statements just move to after the insert transaction).
**Correctness risk:** Low. Indexes are pure acceleration; the recursive-CTE reachability needs
`IX_*_FromSym`/`ToSym` to exist before the first *query*, not before the *insert*. Build order only.
**Citations:** [SQLite forum — drop indexes before inserting](https://sqlite.org/forum/forumpost/91df4dddf8?t=h);
[SQLite insert speed](https://voidstar.tech/sqlite_insert_speed/);
[Squeezing performance from SQLite: indexes](https://medium.com/@JasonWyatt/squeezing-performance-from-sqlite-indexes-indexes-c4e175f3c346).

### B4. Drop the per-row `await ExecuteNonQueryAsync` in the edge inserts; use synchronous `ExecuteNonQuery`
**What:** In `InsertCallEdgesAsync` / `InsertDispatchEdgesAsync` (`GraphMaterializer.cs:308,347`),
replace `await command.ExecuteNonQueryAsync(ct)` with synchronous `command.ExecuteNonQuery()` inside
the hot loop, mirroring `Writes.InsertRows` (`Writes.cs:540`) and its explanatory comment
(`Writes.cs:497`).
**Why here:** Microsoft.Data.Sqlite implements the async ADO methods synchronously, so awaiting per
row buys nothing and costs a `Task`/state-machine allocation **per edge** (~815k allocations) plus
sync-context hops. The save path already eliminated this deliberately; the graph path didn't.
**Expected magnitude:** Small on wall time (this phase is I/O-bound, not alloc-bound), small-positive
on allocation. Mostly consistency + removing pointless overhead; do it alongside B1–B3.
**Effort:** Low.
**Correctness risk:** None.
**Citations:** in-repo `Writes.cs:497` comment (already validated against Microsoft.Data.Sqlite).

### B5. Batch the edge inserts under fewer, larger transactions / consider multi-row VALUES
**What:** The edge inserts are already inside one transaction (`GraphMaterializer.cs:80`), which is
good. Marginal further gains: bind via a multi-row `INSERT … VALUES (…),(…),…` (e.g. 128 rows per
statement) to cut statement-step overhead, as a second-order optimization after B1–B4.
**Why here:** Per-statement stepping dominates only once the journal/cache problems (B1) are gone.
This is the textbook "wrap inserts in a transaction / batch rows" advice, but the transaction half is
already done — only row-batching remains, and its upside is modest.
**Expected magnitude:** Small (single-digit %), and only visible after B1.
**Effort:** Medium (parameter management for N-row statements).
**Correctness risk:** Low.
**Citations:** [phiresky tuning post](https://phiresky.github.io/blog/2020/sqlite-performance-tuning/);
[SQLite insert speed](https://voidstar.tech/sqlite_insert_speed/).

### B6. Pipeline `graph` with `save` (or skip the round-trip) — structural, larger
**What:** Today `save` writes facts, closes, and then `graph` builds derived tables in a *separate*
context. The graph already builds from the **in-memory** `FactGraphProjection.FromAnalysis(result)`
(`IndexCommands.cs:539`), so it does NOT re-read facts to build edges — good. The remaining serial
gap is that FTS5/`nodes`/`ANALYZE` read facts back off disk. Option: build FTS5/`nodes` source data
from the in-memory `result` too (it's already in RAM), avoiding the read-back entirely; or overlap the
graph edge-insert transaction with the tail of the save on a second connection.
**Why here:** Attacks the read-back directly rather than just making it cheap (B1/B2). The fact
arrays are still rooted in `result` at graph time, so FTS5 content need not be re-`SELECT`ed from
SQLite.
**Expected magnitude:** Medium-large, but overlaps with what B1+B2 already recover, so do B1/B2 first
and re-measure before investing here.
**Effort:** High (restructure FTS5 population to read from in-memory facts; concurrency on one DB file
needs care even with `locking_mode=EXCLUSIVE`).
**Correctness risk:** Medium (must keep FTS5 dedup/`GROUP BY SymbolId` semantics identical —
`GraphMaterializer.cs:137`). Verify search results byte-identical after.

### B-non-recommendation: WAL mode
WAL is the usual "make SQLite writes fast" headline, but it is the **wrong** tool on this publish
path. The atomic-publish design writes a throwaway temp and renames; `journal_mode=OFF` +
`EXCLUSIVE` (B1) is strictly faster than WAL for a single exclusive writer that doesn't need crash
durability or concurrent readers. WAL also leaves `-wal`/`-shm` sidecars that complicate the atomic
`File.Move`. Keep WAL only if/when a future path needs concurrent readers during the write.

---

## Category A — reduce peak memory / allocation in the Roslyn extract stage.

Skeptical framing up front: **gc% is only ~9–10%, so allocation-rate reductions barely move wall
time.** The legitimate target in category A is **peak working set (~9 GB)**, which matters for running
on smaller machines and for headroom, not for the clock. Rank accordingly.

### A1. Stream extraction per project so `SemanticModel`s are released as you go ⭐ (the real memory win)
**What:** Today the pipeline is **compile-all → retain-all → extract-all**: `LoadAsync` accumulates a
`SourceModel` (with a live `SemanticModel`) for every file of every project into `IndexedSources`
(`SolutionSourceLoader.cs:1035`), and `SolutionAnalyzer` only runs extraction *after* the whole set
exists (`SolutionAnalyzer.cs:51,62`). Restructure to: for each project (in dependency-safe order),
get its compilation, extract its files, emit facts, then **drop that project's `SemanticModel`s and
trees** before moving on. The compilations of *dependency* projects must stay alive (see A2), but a
project's own per-file `SemanticModel` instances — which hold the expensive bound-node caches — can
be released immediately after its files are extracted.
**Why here:** This is the documented canonical pattern: "sweep across the Compilation one tree at a
time, get the semantic model for that tree, do all processing, then release" — holding a
`SemanticModel` "may keep a significant amount of memory from being garbage collected." rig violates
this by holding *all* of them. Because `GetDiagnostics()` is called per project (`SolutionSourceLoader.cs:188`),
every body is bound and that bound state is pinned by the retained `SemanticModel`. Releasing
per-project converts the peak from "sum over all 123 projects" to "max single project + facts."
**Expected magnitude:** Large on **peak RAM** (the ~9 GB co-resident semantic state is the single
biggest live contributor); roughly neutral on wall time and on cumulative allocation (you still bind
everything once — same churn, just not retained). Could plausibly cut peak working set by a large
fraction (toward "biggest project + growing fact arrays").
**Effort:** High. This is the load/extract phase boundary (`SolutionAnalyzer.cs:37–75`) — the cleanest
seam in the codebase. Must preserve the deterministic input ordering the FactIndex surrogate keys
depend on (`SolutionAnalyzer.cs:55–59` comment) — extract per project but assign global fact indices
in a stable project order. Keep the `Parallel.For` *within* a project (or across a bounded window of
projects) rather than across all files at once.
**Correctness risk:** Medium — **must not lose cross-project recall.** The intra-project parallelism
and global determinism need care. Dependency ordering must ensure a project's dependencies' live
compilations still exist when it binds (A2). Regression-guard with a full `rig derive`/`reaches`
diff against the current store (no edge count may drop).
**Citations:** [Roslyn SemanticModel API remarks (memory/lifetime)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.semanticmodel?view=roslyn-dotnet-4.9.0);
[Roslyn issue #39840 — caching SemanticModel instances](https://github.com/dotnet/roslyn/issues/39840);
[Getting Started — C# Semantic Analysis](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Getting-Started-C%23-Semantic-Analysis.md).

### A2. Keep dependency compilations resident but bound a sliding window (don't hold all 123 at once)
**What:** Compilations of a project's transitive in-set dependencies must co-reside while that project
binds (rig deliberately pulls the whole in-set closure in as live `ProjectReferences` for one assembly
identity / cross-project recall — `SolutionSourceLoader.cs:714–732`). But the *root/leaf* projects'
compilations don't all need to be alive simultaneously. Process in reverse-topological order so a
project's compilation can be dropped once every project that depends on it has been extracted.
**Why here:** A `Compilation` is far lighter than its `SemanticModel`s' bound-node caches, but with
123 of them plus their metadata references it's still material. Reference-counting compilations by
remaining dependents lets the GC reclaim leaf compilations mid-run.
**Expected magnitude:** Medium on peak RAM, additive to A1 (A1 drops SemanticModels, A2 drops the
underlying compilations once safe).
**Effort:** High (dependency-aware scheduling + lifetime bookkeeping).
**Correctness risk:** **High if done wrong** — dropping a compilation still referenced by an
unextracted dependent reintroduces the dual-identity CS0012 recall gap the closure logic exists to
prevent (`SolutionSourceLoader.cs:719–726`). Only drop when the dependent set is empty. Treat as a
follow-on to A1, not a prerequisite.
**Citations:** in-repo `SolutionSourceLoader.cs:714` design comment; same Roslyn docs as A1.

### A3. Set `DocumentationMode.None` on the parse options ⭐ (cheap, safe, modest)
**What:** `CSharpParseOptions` at `SolutionSourceLoader.cs:662` does not set `DocumentationMode`, so
it defaults to `Parse`. Set it to `DocumentationMode.None`.
**Why here:** Profiling attributed ~205 MB to `ProcessDocumentationCommentTriviaNodes` (doc-comment
trivia compilation) and ~619 MB to `GetDocumentationCommentId` calls. `DocumentationMode.Parse` makes
Roslyn parse and bind XML doc-comment trivia that rig never consumes as documentation. **Caveat:**
rig uses `GetDocumentationCommentId()` for its DocID keys (`FactExtractor`), and that API derives the
ID from the *symbol*, not from parsed doc trivia — so `DocumentationMode.None` should not affect DocID
generation (verify with a fact diff). It removes the *trivia parsing/binding* cost, not the DocID
cost.
**Expected magnitude:** Small (a few hundred MB of cumulative allocation; minor peak relief). Not a
wall-time mover at gc%≈9%.
**Effort:** Very low (one enum on the parse options).
**Correctness risk:** Low — **must verify** DocIDs are byte-identical before/after (fact-store diff).
If any DocID changes, revert; recall depends on stable DocID keys.
**Citations:** [CSharpParseOptions.DocumentationMode](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.csharp.csharpparseoptions);
[DocumentationMode enum](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.documentationmode).

### A4. Reduce error-recovery churn by improving reference completeness (fewer CS0012/CS0246)
**What:** The #1 allocator is `ExtendedErrorTypeSymbol` (~11.8 GB), manufactured on **failed** binds
when design-time builds miss generated types → CS0012/CS0246 cascades. Reduce the failures: ensure
source generators / proxy generators / T4 outputs are present in the compilation (rig already wires
some generators — `SolutionSourceLoader.cs:1198`), and that the netstandard2.0 sibling-reference and
F#/VB built-output-DLL fixups (`SolutionSourceLoader.cs:700,738`) cover all real gaps. Each resolved
reference removes a whole error-recovery cascade.
**Why here:** Error types are pure waste — they exist only because a bind failed. Fewer failures =
less of the single largest allocation source. This is the *one* category-A item that also reduces
*cumulative* allocation meaningfully (and could shave a little extract CPU, since failed inference
retries are not free).
**Expected magnitude:** Potentially large on **cumulative allocation** (attacks the 11.8 GB head-on),
small-to-medium on wall time, small on peak. Highly dependent on how many cascades are eliminable —
some (genuinely-missing generated code) are irreducible.
**Effort:** Medium–high and investigative (audit the actual CS0012/CS0246 set from `--verbose`, find
the missing references, wire them). Diminishing returns once the easy ones are fixed.
**Correctness risk:** **Positive for recall** — every fixed reference is a bind that now succeeds, so
this can only *add* call edges. Low risk, but re-baseline expected fact counts (they should rise).
**Citations:** Roslyn binder behavior — error recovery is inherent;
[Optimizing C# code analysis](https://dev.to/asimmon/optimizing-c-code-analysis-for-quicker-net-compilation-4e3d).

### A5. LINQ query-syntax churn (`MakeQueryInvocation`, ~67% of `Binder.ResolveExtension` ≈ 8.4 GB) — disclose, don't fix
**What:** Query-syntax (`from/where/let/join/select`) lowers each clause to a generic extension-method
call requiring type inference; the MedDBase source is LINQ-heavy. There is **no safe rig-side lever**:
this is the compiler doing exactly the binding rig needs for call edges (the `Select`/`Where` etc.
target methods are real reference facts). Skipping it would drop edges.
**Why here / verdict:** Listed to **rule it out**. It's a large allocation bucket but it is *load-bearing*
binding, not waste like A4's error types. At gc%≈9% it isn't a wall-time problem either. **Do not
optimize.** Accept it.
**Effort/risk:** N/A — recommendation is to not touch it.

### A6. GC runtime config for a batch job ⭐ (cheap experiments; measure, don't assume)
**What:** `Rig.Cli.csproj` already sets `ServerGarbageCollection=true` (31 heaps, DATAS on, .NET 11).
Experiments, in priority order:
- **`GCConserveMemory` (0–9):** trades a little throughput for lower peak working set by collecting
  more aggressively and fragmenting less. Most relevant lever for the ~9 GB peak. Set via
  `runtimeconfig` `System.GC.ConserveMemory` (decimal) or `DOTNET_GCConserveMemory` (hex). Known to
  be finicky to honor — verify with `GC.GetConfigurationVariables()`.
- **An explicit `GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true)` at the
  phase boundary** — right after extraction emits facts and the `SemanticModel`s become collectable
  (and especially after A1 drops them), *before* save/graph. DATAS + ServerGC does **not** reclaim
  Gen2 aggressively without memory pressure (per the brief), so a chunk of the 9 GB is uncollected
  garbage; one forced compacting collect at the seam returns it to the OS before the I/O phase.
- **Heap-count / DATAS:** on a 31-logical-core box, 31 server-GC heaps is a lot of per-heap overhead
  for a workload whose parallel width is bounded by project/file count. Try `DOTNET_GCHeapCount` (e.g.
  8–16) and/or toggling DATAS (`DOTNET_GCDynamicAdaptationMode=0` to pin a fixed count) and measure
  both peak and wall.
- **`GCHeapHardLimit`:** *not* recommended as an optimization — it caps the heap and will OOM the
  partial-compilation peak. Only useful as a guardrail on a constrained host, and only after A1.
**Why here:** These are config-only, reversible, and directly target "uncollected Gen2 garbage inside
the 9 GB." The forced collect at the phase seam is the highest-confidence of the set.
**Expected magnitude:** Peak: medium (ConserveMemory + the seam collect can return multiple GB of
uncollected garbage); wall: neutral-to-slightly-negative (more GC work). Heap-count tuning is a coin
flip — must measure.
**Effort:** Very low (config + one `GC.Collect` call).
**Correctness risk:** None.
**Citations:** [GC config settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector);
[Memory-limit design doc](https://github.com/dotnet/designs/blob/main/accepted/2019/support-for-memory-limits.md);
[How to set GCConserveMemory (#93914)](https://github.com/dotnet/runtime/issues/93914);
[GCSettings not honored (#92458)](https://github.com/dotnet/runtime/issues/92458).

### A7. `CSharpCompilationOptions` knobs — mostly already optimal; nullable already off
**What:** `nullableContextOptions` is already forced `Disable` (`SolutionSourceLoader.cs:685`) — the
in-repo comment already measured this as marginal (~3s / 0.3 GB) because `TypeWithAnnotations` is
Roslyn's *universal* type representation, not nullable-specific. Other knobs (`OptimizationLevel`,
`ConcurrentBuild`, `metadataImportOptions`) don't change binding-side allocation in a way that helps
extraction. `concurrentBuild` defaults true (fine). No further win here.
**Verdict:** Already done / no lever. Don't spend time.
**Citations:** in-repo `SolutionSourceLoader.cs:677–685` measured comment.

### A8. `GetSemanticModel(tree, ignoreAccessibility)` / fresh-vs-cached model — no win
**What:** rig already binds through `compilation.GetSemanticModel(tree)` over the same warmed
compilation (`SolutionSourceLoader.cs:1033`), which reuses the bound-node cache from the per-project
`GetDiagnostics()` warm-up. A *fresh* model per file would **re-bind** (more CPU + more allocation),
not less. `ignoreAccessibility` doesn't reduce memory. The current approach is the memory-cheap one
*per query*; the problem is purely *retention* (A1), not per-model construction.
**Verdict:** No change. The lever is retention (A1), not model construction.

---

## Category C — getting a real retained-size / dominator view of the .NET 11 dump

Situation: the existing `C:\Git\extract-peak (roslyn live).dmp` (~9.6 GB) cannot be walked by
`dotnet-dump` 10.0.x — it errors "Unable to create a ClrHeap." Root cause: `dotnet-dump analyze`
bundles a fixed ClrMD/DAC and there is **no released `dotnet-dump` whose DAC understands the .NET 11
runtime that produced the dump.** Heap walking needs a DAC (`mscordaccore.dll`) whose version exactly
matches the runtime in the dump.

**Ranked practical paths (best first):**

1. **WinDbg + the matching .NET 11 DAC/SOS (most practical on Windows). ⭐**
   WinDbg can load the dump and, via the symbol server, auto-download the `mscordaccore.dll` /
   `sos.dll` matching the exact runtime build embedded in the dump. Open the `.dmp`, ensure
   `.sympath srv*` includes the Microsoft symbol server (and, if needed, the runtime's private
   symbols), then `.loadby sos coreclr` (or `!analyze` will prompt to load SOS). Then use
   `!dumpheap -stat` for type histograms and `!gcroot` / `!objsize` for retained size, and
   `!eeheap -gc` for heap composition. If auto-DAC fails, point WinDbg at the matching DAC by
   copying `mscordaccore.dll` from the **exact** .NET 11 SDK/runtime install that produced the dump
   (same commit hash). This is the canonical "no matching dotnet-dump" workaround.
   - Citations: [SOS debugging extension](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/sos-debugging-extension);
     [dotnet-dump tool](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump);
     [diagnostics #4686 "No CLR runtime found" — DAC matching](https://github.com/dotnet/diagnostics/issues/4686).

2. **JetBrains dotMemory — "Import dump" of a Windows full-process dump. ⭐ (best for dominator/retained-size UX)**
   dotMemory can open a Windows process dump and give exactly the **retained size / dominator tree**
   view you want (far better UX than SOS for "what holds the 9 GB"). It has its own heap reader and is
   often more forgiving of runtime versions than the bundled ClrMD in `dotnet-dump`. Since the
   allocation profiling was already done in dotMemory, this is the lowest-friction path to a
   dominator view — try importing `extract-peak (roslyn live).dmp` directly. (If dotMemory's reader
   also lags .NET 11, fall back to path 1.)
   - Citation: JetBrains dotMemory "Import dump" feature (product docs).

3. **dnceng nightly / preview diagnostics feed — a `dotnet-dump` built against .NET 11.**
   Install the latest preview/nightly `dotnet-dump` from the dotnet tools nightly feed
   (`https://pkgs.dev.azure.com/dnceng/public/_packaging/...`) rather than the released 10.0.x. A
   nightly whose ClrMD/DAC support targets net11 will walk the heap. This is the "official path once
   it ships" — worth a quick check of the diagnostics repo releases before investing in WinDbg setup.
   - Citations: [dotnet/diagnostics releases](https://github.com/dotnet/diagnostics) (check for an
     11.0 `dotnet-dump`); [dotnet-dump tool docs](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump).

4. **Re-capture as a gcdump instead of a full dump (if you can reproduce the peak).**
   The brief notes a gcdump truncated at 10M objects, but `dotnet-gcdump` (matching version) gives a
   managed-only object graph with retained sizes that PerfView/VS can open as a dominator tree, and is
   far smaller than a 9.6 GB full dump. Use `RIG_PROFILE_PAUSE` (already wired — `SolutionAnalyzer.cs:125`)
   to hold the process at the true co-resident peak and `dotnet-gcdump collect -p <pid>`. Prefer a
   gcdump version aligned to net11; raise the object cap if the tool exposes it. This is the cleanest
   *managed* dominator view if re-running is acceptable.
   - Citation: [dotnet-gcdump](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump).

**Recommendation:** Try **dotMemory import (2)** first for the dominator UX; if its reader can't parse
net11, use **WinDbg + symbol-server DAC (1)**. Keep **gcdump (4)** as the clean re-capture option
since `RIG_PROFILE_PAUSE` already exists to freeze the peak. Don't wait on (3) unless a nightly is
already published.

---

## Consolidated ranking (impact ÷ effort)

| Rank | Strategy | Category | Impact | Effort | Risk |
|------|----------|----------|--------|--------|------|
| 1 | **B1 — fast PRAGMAs on the graph connection** | wall time | High | Low | None |
| 2 | **B2 — `mmap_size` for graph read scans** | wall time | Medium | Low | None |
| 3 | **A3 — `DocumentationMode.None`** | alloc/peak | Small | Very low | Low (verify DocIDs) |
| 4 | **A6 — GC config + forced collect at phase seam** | peak RAM | Medium | Very low | None |
| 5 | **B3 — build edge indexes after bulk insert** | wall time | Medium | Low–med | Low |
| 6 | **B4 — synchronous `ExecuteNonQuery` in edge inserts** | alloc | Small | Low | None |
| 7 | **A4 — fewer CS0012/CS0246 (kill error-type churn)** | alloc(+recall) | Med–high | Med–high | Low (raises counts) |
| 8 | **A1 — stream extraction per project (release SemanticModels)** | peak RAM | High | High | Medium |
| 9 | **B6 — FTS5 from in-memory facts / pipeline save+graph** | wall time | Med-large | High | Medium |
| 10 | **A2 — sliding-window compilation lifetime** | peak RAM | Medium | High | High |
| — | A5 (LINQ churn), A7 (compilation knobs), A8 (model construction), WAL | — | none | — | disclose / don't do |

Sequencing advice: ship **B1+B2+B4** together (one small PR, biggest wall-time return, re-measure
the graph phase), then **A3+A6** (cheap, config-level), then **B3**, then re-profile before committing
to the big structural items **A1/A2/B6**. Re-baseline `rig derive`/`reaches` edge counts after A1 and
A4 — neither may *lose* an edge.

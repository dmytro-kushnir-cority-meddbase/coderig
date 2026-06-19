# Index pipeline: fuse the compile/read/extract passes + stream to the writer

> Status: **proposal, not built.** Captured 2026-06-18. Targets `SolutionSourceLoader.LoadAsync`,
> `SolutionAnalyzer.AnalyzeAsync`, and `Writes.SaveFactsBatchedAsync`. The risky surface is **source
> generators on the old-MSBuild / .NET 4.8 closure** (MedDBase), which is exactly the surface the
> current multi-pass shape grew to defend. Read the *Attack vectors* section before touching code.

## TL;DR

`LoadAsync` walks every in-scope project **three times** in three barrier-separated passes — a compile
pass (errors), a read pass (sources), and then a separate AsParallel extract pass — and only *then*
hands a fully-materialised set of fact arrays to the DB writer. The first (compile-only) pass produces
nothing but a warning string, eagerly binds every method body, and — because Roslyn's compilation
cache is bounded — can cause projects to be **compiled twice** on a large closure.

Proposal: collapse compile → read → extract into **one per-project task** (build-once, consume-hot),
and **stream** the resulting facts into a single DB-writer task through a bounded channel, so the
serial save overlaps the parallel analysis and peak memory drops from "whole graph" to "one batch."

This is partly an *undo*: the separateness was never load-bearing. But the generator and old-MSBuild
machinery around it **is** load-bearing, and the proposal must preserve it exactly. Hence the long
attack-vector list.

## Background: why the pipeline looks like this today

Three independent reactions to real failures stacked up. None of them *required* a separate compile
pass; the passes are incidental, the reactions are not.

### 1. The old-MSBuild / .NET 4.8 reaction → out-of-process Buildalyzer

`BuildWorkspace` (`SolutionSourceLoader.cs:162`) drives **Buildalyzer**, which runs `MSBuild.exe`
out-of-process, because the in-process Roslyn `BuildHost-net472` throws `TypeInitializationException`
on `XMakeElements` under VS18/MSBuild18 when loading old-style `ToolsVersion="4.0"` projects (the
`System.Collections.Immutable` identity conflict; Roslyn PR #83477, unreleased as of May 2026). The
design-time builds run with `DesignTimeBuild=true`, `BuildingInsideVisualStudio=true`,
`UseSharedCompilation=false`, emit **no binary**, and are parallelised with `Parallel.ForEach`
(commit `4844261`). Buildalyzer is invoked with `addProjectReferences:false`, so P2P references load
from compiled DLLs rather than re-evaluating each `.csproj`.

Consequence: the workspace Roslyn ends up with is an **AdhocWorkspace** hand-assembled from
`IAnalyzerResult`s (`BuildWorkspaceFromResults`, `:274`), not a normal MSBuildWorkspace. Everything
downstream — references, project references, parse/compilation options — is reconstructed by hand from
MSBuild properties. That reconstruction is where the .NET 4.8 quirks live:

- **net452/netstandard2.0 sibling refs** (`:351`): a net48 project referencing a netstandard2.0 lib
  built against the netstandard2.0 flavour of a package (LLBLGen) gets the net452 ref resolved by
  Buildalyzer; we add the netstandard2.0 sibling DLL so both identities are present and the base-type
  chain resolves.
- **Transitive in-set closure as live ProjectReferences** (`:365`–`:413`): Roslyn project references
  don't flow transitively, so a project using a *transitively*-referenced project's types would
  otherwise see a **second** assembly identity (the metadata DLL alongside the live compilation) and
  silently drop call edges. We pull the whole in-set closure in as live `ProjectReference`s and drop
  the matching DLLs from metadata, so each in-set assembly has exactly one identity.
- **F#/VB project-reference DLLs** (`:506`, commit `95f4041`): the C# workspace can't compile F#/VB,
  so their types are invisible (CS0012) unless we add their built output DLL as metadata.

### 2. The generator reaction → wire + emit + redirect + run

Source generators (the `ClientPage` request/response proxy generator) caused four separate pieces,
added incrementally (commits `13601a5`, `88ae573`):

- **`WireGeneratorAnalyzersAsync` (`:710`)** — Buildalyzer drops `OutputItemType="Analyzer"`
  `ProjectReference`s, so generators referenced project-to-project are invisible. We re-parse the
  `.csproj` XML (`AnalyzerProjectReferencePaths`, `:468`), **emit** each generator project's
  compilation to a temp DLL (`EmitCompilationToTempAsync`, `:771` — design-time builds emit nothing),
  and add it as an analyzer reference. This **mutates the solution** (`AddAnalyzerReference` +
  `TryApplyChanges`) after the workspace is built.
- **`HostRedirectingAnalyzerLoader` (`:875`)** — a generator compiled against an older Roslyn
  implements that version's `ISourceGenerator`; our 5.x host's `GetGenerators()` won't recognise it
  (different assembly identity → 0 generators). We redirect `Microsoft.CodeAnalysis*` /
  `System.Collections.Immutable` / `System.Reflection.Metadata` to the host's loaded copies.
- **`RunSourceGeneratorsAsync` (`:797`)** — `AdhocWorkspace` does **not** run generators in this
  design-time setup (`GetSourceGeneratedDocumentsAsync` returns nothing), so we drive a
  `CSharpGeneratorDriver` explicitly per project and index the trees it produces, with a semantic
  model bound to the **generator-updated** compilation.

### 3. The two-pass split (the part this proposal removes)

`LoadAsync` then does, over the same `csharpProjects` set:

1. **Compile pass** (`:67`–`:101`): `GetCompilationAsync` + `GetDiagnostics()` per project, collecting
   error-severity diagnostics into a bag whose **only** consumer is the warning print at `:104`–`:121`.
2. **Read pass** (`:123`–`:145`): `LoadProjectSourcesAsync` per project — classify, fetch syntax tree +
   semantic model per indexed document, then `RunSourceGeneratorsAsync`.

`SolutionAnalyzer.AnalyzeAsync` (`:42`–`:64`) then runs a **third** walk, `AsParallel().AsOrdered()`
over all sources, into giant fact arrays, which `Writes.SaveFactsBatchedAsync` finally drains.

## The claim: the compile pass is an over-reaction

The compile pass exists to (a) surface compile errors up front and (b) warm the compilation cache. But:

- Its only product is a **warning string**. Nothing downstream reads the diagnostics.
- `GetDiagnostics()` forces a **full eager bind of every method body in every project**; the read pass
  only needs semantic models for the documents it actually extracts (`:653`–`:660`).
- Roslyn's per-project compilation cache is **bounded/LRU**. Two separate full-solution parallel passes
  mean a project's compilation, built in pass 1, can be **evicted** before pass 2 reaches it — at which
  point `GetSemanticModelAsync` rebuilds it. On a 135-project closure under high `--parallelism`, this
  is a genuine second compilation, not just an over-bind.

So "second compilation pass" is real on big inputs, and even when it isn't, it's wasted eager binding.

## Proposal

### P1 — Fuse compile → read → extract into one per-project task (build-once)

After the solution is **frozen** (all builds done, all generator wiring applied — see *barriers*
below), run one task per project that: fetches the compilation **once**, collects its error
diagnostics, reads + classifies its documents, runs its generators, and extracts facts — all against
that single hot compilation, before the cache can evict it. This subsumes the compile pass entirely.

### P2 — Stream facts to a single writer through a bounded channel

Each per-project task pushes its extracted facts into a `Channel<FactBatch>`; one consumer task drains
it into SQLite (SQLite is single-writer, so exactly one consumer). The run header is written first; the
four fact tables stream; `source_files` + `di_registrations` (incl. the post-analysis XML miner) +
the assembly registry are written at the tail.

Wins:
- **Save overlaps analysis.** The serial DB write hides under the parallel, expensive compile/bind, so
  the save phase ≈ disappears from wall-clock.
- **Peak memory collapses.** Today extraction materialises every fact into arrays
  (`SolutionAnalyzer.cs:60`–`64`) — the very thing that OOM'd a 2M+ reference store (`Writes.cs:130`).
  A bounded channel gives backpressure: retained memory ∝ batch, not ∝ whole graph.
- Removes the compile→read and read→extract barriers.

Pairs with the already-TODO'd save changes (`Writes.cs:272`): defer secondary indexes until after the
bulk load, use a reused prepared raw-ADO insert (the `GraphMaterializer.InsertCallEdgesAsync` pattern),
one transaction, and **synchronous** `ExecuteNonQuery` (Microsoft.Data.Sqlite's async is synchronous
anyway — confirmed via `RigDbContext.cs:54` `UseSqlite`).

### P3 (optional) — Narrow or defer diagnostics

Since the only consumer is a warning, either filter to `Error` severity at the source or gate the
whole diagnostic collection behind `--verbose`. Independent of P1/P2.

## What MUST stay a barrier

- **All design-time builds → workspace.** `BuildWorkspaceFromResults` (`:274`) needs every build result
  before it can compute the transitive in-set closure and wire cross-project `ProjectReference`s — you
  cannot decide "is this dep in the indexed set" until the whole set is known. This barrier is correct.
- **Generator wiring → per-project fusion.** `WireGeneratorAnalyzersAsync` mutates the solution
  (`AddAnalyzerReference` + `TryApplyChanges`), which **invalidates consumer compilations**. The
  build-once guarantee only holds if fusion runs *after* the solution is final. Freeze, then fuse.
- **Save → graph.** `GraphMaterializer.BuildAsync` (`:42`) is a whole-store reads-facts-back pass; it
  can only run after the last fact is committed.

Net shape: `builds ─▮─ wire ─▮─ [per-project ∥: compile→read→extract→channel] → 1 writer ─▮─ graph`.

## Attack vectors

The scenarios that could break the fused/streamed pipeline. Each notes whether today's code defends it
and what the proposal must do to not regress.

1. **Generator-consumer compilation invalidation race.** Wiring adds analyzer refs to *consumers*,
   invalidating their compilations. If fusion starts reading a consumer before wiring is fully applied,
   its compilation lacks generated trees → missing `*Proxy : ProxyBase` facts → the `clientpage_proxy`
   effect gate silently under-fires. *Defense:* the wiring barrier above is non-negotiable; assert the
   solution is frozen (no pending `TryApplyChanges`) before the per-project stage begins.

2. **Generator project compiled twice.** A generator project (e.g. `RequestResponseProxyGenerator`) is
   itself a C# project in `csharpProjects` **and** gets emitted in `EmitCompilationToTempAsync`. Wiring
   forks the solution; if the generator project is unchanged across the fork its compilation *should*
   be retained, but build-once is not guaranteed for it. *Mitigation:* accept one extra emit for
   generator projects (small N), or memoise the generator compilation across wiring and the fused pass.

3. **Compilation-cache eviction defeats build-once anyway.** The whole point of P1 is build-once, but
   if a single project's `GetCompilationAsync` and its documents' `GetSemanticModelAsync` straddle a GC
   that evicts the compilation, it rebuilds. *Mitigation:* within a project task, hold a strong
   reference to the `Compilation` for the lifetime of that project's reads/extract; never re-fetch it.

4. **Generator driver needs the base compilation, not a forked one.** `RunSourceGeneratorsAsync` calls
   `GetCompilationAsync` then `RunGeneratorsAndUpdateCompilation`. If fusion passes a compilation that
   already had generators run (double-run), generated trees duplicate or `originalTrees` dedup
   (`:822`) misbehaves. *Mitigation:* run generators exactly once per project, off the same base
   compilation used for normal docs; keep the `originalTrees` HashSet dedup.

5. **`HostRedirectingAnalyzerLoader` is process-global, one-shot.** `EnsureRedirectHook` hooks
   `AssemblyLoadContext.Default.Resolving` once (`_hooked` interlock, `:888`). Reordering or
   parallelising generator emit must not assume per-call setup. *Defense:* unchanged — it's already
   idempotent and global; just don't remove the first call site.

6. **Per-project failure must stay best-effort.** Today a project whose design-time build throws is
   skipped (`:250`), an unavailable compilation is logged and skipped (`:84`), and a misbehaving
   generator is swallowed (`:849`). Fusion concentrates compile+read+extract in one task — an unhandled
   throw there must **drop that project**, not fault the whole `WhenAll`/channel. *Mitigation:*
   try/catch per project task; on failure, emit zero facts for it and record a warning, exactly as now.

7. **FactIndex determinism.** `SymbolFactIndex = i` etc. is the array position; the store is
   commit-keyed and used for behavioral diffing. Streaming assigns indexes in **project-completion
   order**, which is nondeterministic. The indexes are surrogate PK components (diffs compare by DocID/
   content, not index) — *probably* fine, but **verify** nothing joins positionally. *Mitigation:*
   assign a deterministic per-project base offset (projects sorted by path) + local index, so two runs
   of the same commit produce byte-identical stores.

8. **Assembly-registry digest under streaming.** `WriteAssemblyRegistryAsync` folds symbols+references
   into per-assembly accumulators. Its `AssemblyAccumulator` (`:235`) is already an order-independent
   XOR+sum fold, so folding rows as they stream is **safe by construction** — but a reference is
   attributed to the assembly of its *enclosing symbol* via `symbolAssembly` (`:124`), which needs that
   symbol seen first. *Mitigation:* build the SymbolId→assembly map at the writer over all symbols
   before (or alongside) folding references — i.e. fold references in a tail step, not inline, or keep a
   running map. Do **not** assume per-project locality (an enclosing symbol can live in another project).

9. **Single-writer contention starves producers.** If the writer can't keep up, the bounded channel
   backpressures and CPU producers stall — wall-clock could *regress* vs. the current "extract all, then
   write." *Mitigation:* size the channel for several batches of slack; ensure the writer uses the fast
   raw-ADO path (P2 prereq), so write throughput exceeds aggregate extract throughput.

10. **net452/netstandard2.0 + F#/VB references are per-project metadata, computed in
    `BuildWorkspaceFromResults`.** These run **before** the passes and are unaffected by fusion — but
    fusion must not "optimise" by recomputing references lazily per task, or it reintroduces the CS0012
    / duplicate-identity recall gaps commit `95f4041` fixed. *Defense:* leave reference assembly intact
    in the pre-freeze stage; fusion only consumes the finished `Project`.

11. **Diagnostics interleave / error-report ordering changes.** Today all errors print before any
    reading (`:104`). Fused, they interleave per project. Cosmetic, but `--from`/CI log scrapers keying
    off the "Warning: N compilation error(s)" block could break. *Mitigation:* keep a final aggregated
    summary line after the per-project stage; per-project lines become incremental.

12. **Cancellation mid-stream leaves a partial DB.** Today a cancelled/failed index never publishes
    (temp file, atomic rename — `IndexCommands.cs:190`,`:232`). Streaming writes into that same temp, so
    the invariant holds — *as long as* the channel writer respects the token and the atomic publish only
    happens on full success. *Defense:* unchanged publish model; just confirm the writer honours
    `cancellationToken` and the caller still gates the rename on a clean completion.

13. **Memory from generator temp DLLs + retained compilations.** Holding a strong ref per in-flight
    project compilation (vector 3) raises the floor: peak memory ∝ `--parallelism` × compilation size,
    not 1 × compilation. On the MedDBase closure this is the main new memory cost. *Mitigation:* the
    channel bound already caps fact memory; cap concurrent project tasks (the existing `maxParallelism`
    semaphore) so retained compilations stay bounded, and let each task release its compilation ref the
    moment its extract finishes.

## Validation plan

- **Golden-store diff.** Index the MedDBase closure (or eShop fixture, `docs/eShop-callgraphs.txt`)
  before and after; assert the fact tables are identical up to FactIndex (vector 7), and that
  `call_edges` / `dispatch_edges` counts match. This catches generator regressions (vectors 1, 2, 4)
  as missing proxy edges.
- **Generator-specific assertion.** A test that the `ClientPage` proxy base-type facts exist after a
  fused index (the `clientpage_proxy` discriminator) — the exact thing commits `13601a5`/`88ae573` were
  added for.
- **Old-MSBuild assertion.** Index a project with a `ToolsVersion="4.0"` / net48 reference to a
  netstandard2.0 lib (vector 10) and assert the cross-assembly base-type chain still binds (no new
  CS0012s in the warning block).
- **Memory ceiling.** Re-run the 2M+ reference store that originally OOM'd (`Writes.cs:130`); assert
  peak working set is bounded and lower than the array-materialising baseline.
- **Build-once counter.** Instrument `GetCompilationAsync` call count per project in a debug build;
  assert ≤ 1 per non-generator project on the fused path (vector 3), vs. the current ≥ 2.
- **Determinism.** Index the same commit twice; assert byte-identical stores (vector 7 mitigation).

## Rollout

Land in dependency order, each independently shippable and reversible:

1. **Save-path raw-ADO + deferred indexes + sync inserts** (`Writes.cs:272` TODO). No pipeline change;
   pure write speedup. Lowest risk.
2. **Streaming writer (P2)** behind the same fact set — extract-all still, but feed the channel instead
   of arrays. Validates the writer + memory wins without touching the compile/read fusion.
3. **Per-project fusion (P1)** — the generator-risk step; gate behind a `--legacy-passes` escape hatch
   for one release so a regression on the MedDBase closure can be bisected against the old shape.
4. **Diagnostics narrowing (P3)** — independent, anytime.

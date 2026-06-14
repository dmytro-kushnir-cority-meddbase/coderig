# Multi-solution storage — project/assembly-keyed unified store (Option C)

Status: **decided 2026-06-14**, implementation in slices. Supersedes any "DB per solution" idea.

## Problem

The MedDBase monorepo has **21 `.slnx` solutions** (`.slnf` filters are stale and out of scope).
Ground truth (parsed from the `.slnx`):

- Master `MedDBase.slnx` = **320 projects**.
- Union across all 21 `.slnx` = **384 projects**.
- **64 projects live outside the master** (standalone tail: `audits/*`, `client-data-transformation/*`
  incl. its own vendored MMS copy, `echo-process` samples, `sql-runner`, `event-log-monitor`,
  `permissions-db-model`, api-gateway source generator, `dfs`, `docman.mac`).

We want one store that answers reachability/effect questions across the **whole** universe, scoped to a
solution when asked — without the redundancy of storing shared projects once per solution.

## Why not "DB per solution"

`rig`'s identity is the **DocID** (global, solution-independent); symbols already carry
`DefiningAssembly`, references carry `TargetAssembly`, and the query layer is already
*"cross-project (all runs), DocID-keyed, no latest-run concept"* with client-side dedup by `SymbolId`
(`Reads.cs`). So cross-solution joins already work *in one DB*. A DB-per-solution design would:

- **lose** those native joins (21-way SQLite `ATTACH`, no shared FTS, manual cross-file dedup),
- **duplicate** every shared project across DBs, and
- **re-introduce the exact boundary that causes F5** — a call from solution A into a project only
  present in solution B's DB dead-ends.

**F5 is a coverage problem, not a storage-architecture problem.** Confirmed: in the current
full-master store, `Master.SubmitToHealthcode` reaches 3,753 methods / 235 effects across many
projects — cross-project stitching works. F5's `ExportQueue` dead-end was a *scoped*-mine artifact
(the scoped mine omitted that project). The fix is "get every project into one store," which a unified
store does and DB-per-solution undoes.

## Design — assembly is the unit of storage

- **Identity:** an assembly (`DefiningAssembly`, e.g. `MedDBase.ServiceTier`) is stored **once**,
  regardless of how many of the 21 solutions reference it. Re-indexing replaces only that assembly's facts.
- **Dedup key:** `(AssemblyName, ContentHash)` where `ContentHash` is a deterministic, order-independent
  hash of the assembly's source texts. Re-indexing a solution **skips** any assembly whose hash is
  already present (no re-store; later, no re-extract).
- **Solutions are membership views:** a `solution_membership` table maps `SolutionPath → AssemblyName`.
  Default queries span the whole store; `--solution <path>` filters to that solution's assemblies.
- **Store accumulates** across `rig index <soln>` calls (merge/upsert by assembly), rather than
  atomic-replacing the whole DB. The master is indexed once; each standalone solution adds only its
  unique assemblies.

### Schema additions

- `assemblies` (`AssemblyEntity`): `AssemblyName` (PK), `ContentHash`, `IndexedAtUtcText`,
  `SymbolCount`, `ReferenceCount`, `SourceSolutionPath` (first contributor).
- `solution_membership` (`SolutionMembershipEntity`): key `(SolutionPath, AssemblyName)`.
- Fact tables gain an **owning-assembly** attribution so facts can be replaced per assembly
  (`SymbolFact.DefiningAssembly` already exists; references/dispatch/source-files get the enclosing
  symbol's assembly). [slice 2]

New tables are additive; `EnsureCreatedAsync` creates them on the next fresh index (atomic-publish
writes a new DB). Queries tolerate their absence on old stores via `StorageProbes.TableExistsAsync`
(same pattern as `symbol_fts`).

## Re-mine timing (2026-06-14, master `MedDBase.slnx`, ~320 projects)

| Attempt | Parallelism | Analysis | Save | Total | Result |
|---|---|---|---|---|---|
| 1 (`b7tlce15v`) | 32 | 13m43s | — | — | **crashed in save** (registry OOM, pre-fix); no store published, live store intact |
| 2 (`bus2vrtp1`) | 32 | 15m31s | 3m42s | **19m13s** | published; 326,872 symbols, 2,224,276 refs, **223 assemblies registered** |

Save is ~3.7 min at this scale (2.2M references + the streaming assembly registry). The registry no
longer OOMs.

**Parallelism-32 is unreliable here.** Compilation errors swung **32 → 846** between two identical mines —
nondeterministic races where concurrent design-time builds trample shared `bin/` outputs, so some C#
projects intermittently fail to resolve their project references (`CS0234`/`CS0103` on first-party types
like `MedDBase.DataServer.Core`). This is the documented `--parallelism` hazard. For an *authoritative*
store use `--parallelism 1` or `2` (slower, but stable); `-p32` is only acceptable for a fast, partial
"good enough" index. The F# fix is orthogonal — it removed the `MedDBase.Pathways.DSL` `CS0012` errors;
the 846 are a different (parallelism) failure class.

## Slices

1. **Foundation (this slice):** `AssemblyEntity` + `SolutionMembershipEntity` schema; a deterministic
   per-assembly `ContentHash` helper; dedup primitive + unit tests. Additive, non-breaking.
2. **Write path:** attribute every fact to an owning assembly; `Writes` upserts assemblies, records
   membership, and skips/replaces per `(AssemblyName, ContentHash)`. `rig index` gains a merge mode so a
   store accumulates solutions instead of atomic-replacing.
3. **Query path + CLI:** `--solution <path>` membership filter on `reaches`/`callers`/`tree`/`derive`;
   `rig solutions` listing; `rig runs`-style summary per assembly. Incremental per-project re-mine
   (skip-by-hash also at extract time) as a follow-up.

F5 closes at slice 2 (full universe in one store); slices 1/3 add efficiency and scoping.

## Fork verification (2026-06-14) — no real forks on disk

Concern: same assembly name + namespace in two roots with divergent source (vendored copies) would
collide in the unified DocID space. Checked empirically:

- Master store **already contains** `MMS.*` (`src/mms/MMS.NewTypes`, `MMS.Data.Linq`, `MMS.Data.Standard`)
  and `Echo.Process` (`echo-process/Echo.Process`) source.
- The CDT `client-data-transformation/mms/*` projects referenced by `ClientDataTransformation.slnx`
  **do not exist on disk** — only one `MMS.Data.Standard.csproj` / `MMS.NewTypes.csproj` exists, both
  under `src/mms`. The CDT vendored copy was consolidated into `src/mms`; the `.slnx` reference is stale.
- `echo-process.slnx`'s extra projects are samples/tests; the core `Echo.Process` is in the master.

**Verdict: no real source-level forks.** The unified store with `AssemblyName` as PK is correct as-is.
The separate-store fork path stays as a safety net (and the write path should *detect* a same-name /
divergent-content collision from a different solution and warn), but it isn't needed today. Corollary:
because the `.slnx` are stale, mining must **skip `.slnx` project entries that don't resolve on disk**.

# `rig refs --unused` — declared-but-unused ProjectReferences (build-time pruning)

**Status:** SLICE 1 (CLI core) SHIPPED 2026-07-08 — `rig refs --unused` / `--usage` / `--tsv`, folded into
`refs` (not a standalone command); validated on MedDBase (361 candidates, reproduces PACS→ServiceLayer).
Remaining: slice 2 (reflection/markup risk column), slice 3 (web view). See "Slices" below.
**Found:** 2026-07-08 (Slack #platform thread: Damian's ReferenceTrimmer run + Nick's deployable-artifact angle) · **Family:** query-side analysis / new command
**Related:** reuses the deployment-attribution csproj `<ProjectReference>` parsing; complements [[detector-coverage-gaps]]. Runtime-loaded-DLL gate is a follow-on (below).

## Origin / why
Two engineers independently attacked MedDBase **build time** by cutting unused references:
- Damian ran [ReferenceTrimmer](https://www.nuget.org/packages/ReferenceTrimmer), removed unused ProjectReferences, iterated until it compiled → **359s→281s (−22%)** local dev build; ~220 candidates. Gap he called out: **can't detect unused *package* references**.
- Nick traced from the ~31 deployable artifacts to make per-service builds lean (PDF2 in AKS builds ~90% of the monolith). A/B'd `MedDBase.PACS` declaring but never using `MedDBase.ServiceLayer` → big compile saving.
- Shared blocker both hit: **reflection loads** (`Assembly.LoadFile`, `Activator.CreateInstance`, Nucleus DI, the object store, Echo.Process, MMS.Standard) — a statically-unused ref may be runtime-load-bearing, so they lean on the AUT suite to confirm cuts.

rig already holds the substrate: it can produce the candidate list **without a build**, and — uniquely — annotate it with the reflection risk ReferenceTrimmer/AUT can't.

## The key architectural call: QUERY-SIDE, no re-index
The usage numerator is already frozen into facts and the declared denominator is already parsed at query time (deployment attribution reads csproj `<ProjectReference>`). So this is a `derive`-class command over existing facts + the csproj tree — **no extraction change, no re-index**. Do NOT persist declared refs at index (that would force a slow full MedDBase re-index just to validate). This matches rig's two-stage ethos.

## Proven prototype (2026-07-08, against `LATEST` MedDBase store)
Pure SQL + csproj parse, no code change. Numbers:
- 2,400,418 `reference_facts` scanned; 217 assemblies.
- **used** assembly→assembly edges = 2,049; **declared** (both endpoints indexed) = 1,812.
- **declared − used = 361 trustworthy cut candidates.** `MedDBase.PACS → MedDBase.ServiceLayer` **reproduced** (declared, zero usage). Order-of-magnitude matches Damian's ~220 (his was the narrower compiles-clean subset).

The three joins (this IS the implementation):
- **used side** — `reference_facts` grouped by `(enclosing DefiningAssembly, TargetAssembly)` where `TargetInSource=1`:
  `JOIN symbol_facts s ON s.SymbolId = r.EnclosingSymbolId` → `s.DefiningAssembly → r.TargetAssembly`.
- **usage count per assembly** (Dmytro's preferred low-commitment surface) — `SELECT TargetAssembly, COUNT(*), COUNT(DISTINCT EnclosingSymbolId) FROM reference_facts WHERE TargetInSource=1 GROUP BY TargetAssembly`. (On MedDBase: `MedDBase.DataAccessTier` = 688k refs = the hub; `MMS.Math`=1, `…Core.Resources`=3 = barely used.)
- **declared side** — parse each csproj `<ProjectReference Include>`, resolve the include to an absolute csproj path, then map **csproj → assembly** via the store: assign each indexed `symbol_facts.FilePath` to its nearest-enclosing csproj directory and take the modal `DefiningAssembly`. (This is the *fix* that killed the naïve csproj-filename≈assembly mismatch — filename ≠ `<AssemblyName>` in many projects.)
- **diff** — `declared_asm_pairs − used_asm_pairs`, keeping only edges where **both** endpoints are indexed assemblies (90 csproj had no indexed files — tests/F#/VB/excluded; 53 ref targets weren't analyzed — correctly excluded: we can't judge usage of code we didn't analyze).

## Prod build (query-side)
- New command (surface fork — see below) that: (1) resolves the csproj `<ProjectReference>` graph (reuse the deployment-attribution parser), (2) builds csproj→assembly from `symbol_facts` (extract the owning-dir/modal-assembly map into a reusable `Rig.Cli` service — deployment attribution wants it too), (3) diffs against `reference_facts` usage, (4) renders candidates grouped by declaring project + the usage-count table, (5) TSV via `--format tsv`.
- **Risk annotation (do in v1, it's the whole differentiator):** flag each candidate whose declaring project reaches a reflection seam (`Activator.CreateInstance`/`Assembly.Load*`/Nucleus/object-store) or is a web project (see ceiling) as `high-risk`; the clean ones as `low-risk`. This turns "compile-and-pray" into a ranked queue.
- Tests: NEW `UnusedRefsTests.cs`. `DeepChain` playground already has the exact fixture shape (a project that binds a transitively-flowed type without a direct ProjectReference / a declared-but-unused edge) — assert the unused edge is reported and a genuinely-used one is not. Acceptance on real data: reproduce `PACS→ServiceLayer` on the MedDBase store.
- Docs: README command table + `.claude/skills/rig` REFERENCE.md. No cache-schema bump (new command, no change to existing cached artifacts).

## Caveats / ceiling (bake into output, don't oversell)
1. **Candidates are *statically* unused, not *safe to cut*.** Reflection-loaded refs show as false positives — e.g. PACS's own list includes `Echo.Process.Redis`/`Echo.ProcessJS` (actor framework, config/reflection-loaded). The list is a work-queue; **AUT is the gate.**
2. **Markup usage is invisible.** `reference_facts` are `.cs`-only; `.aspx`/`.ascx` type usage produces none, so web projects (`MedDBase.Pages`, MMS.Web.UI-adjacent — high on the list) over-report. Flag web projects explicitly.
3. **Only judges indexed↔indexed edges.** Not-indexed projects (tests/F#/VB/excluded) are correctly excluded, not assumed-unused.
4. **csproj tree may drift from the store commit** (`LATEST` was `…-dirty`). Query-side command should read the csproj set from the store's `SourceProjectPath`/commit where possible, or warn on mismatch.

## Slices
- **Slice 1 — CLI core (SHIPPED 2026-07-08).** `rig refs --unused [pat]` / `--usage [pat]` / `--tsv`. Pure
  `UnusedReferenceAnalyzer` + three `Reads` aggregates + `DependencyGraph`-parsed declared graph. Honest
  disclaimer header; no risk ranking yet.
- **Slice 2 — reflection/markup risk column.** Flag each candidate whose declaring project hits a reflection
  seam (`Activator.CreateInstance`/`Assembly.Load*`/Nucleus/object-store) or is a web (.aspx/.ascx) project →
  ranks the AUT-gated cut queue. Small, inline-able.
- **Slice 3 — web view (SHIPPED 2026-07-08).** `rig serve` → "Refs" tab, Unused/Usage sub-tabs + filter,
  candidates grouped by declaring assembly (validated live: 361 / 112 projects, PACS→ServiceLayer). Shared
  `UnusedRefsQueryService` (CLI + `/api/refs/*` endpoints — one codepath, no drift); client fetch is UNcached
  (csproj mtime isn't on the derivation-version axis). Remaining below.
- **Slice 3 (orig) — web view.** Expose in the web UI (`RigApiEndpoints` + `wwwroot`): per-project declared-vs-used
  drill-down, the assembly→assembly usage graph with unused edges highlighted, the `--usage` ranking as a
  sortable table; risk column (slice 2) as color-coding; runtime-loaded overlay (follow-on) as a second layer.
  This is the shareable-report surface the platform team actually wants (Nick hand-built Claude artifacts for
  it). Cache note: query-side, so it does NOT ride `derivationVersion` — its cache axis is store + csproj
  mtime; needs its own web cache-key handling, not the `*Schema` machinery.

## Follow-ons (separate items when v1 lands)
- **Runtime-loaded gate (closes the reflection gap).** Ingest an AUT-run loaded-module list (`(Get-Process w3wp).Modules`, the net48 Fusion binding log, or `AppDomain.CurrentDomain.GetAssemblies()`) as a deployment-scoped observed-loaded fact stream. Cut decision becomes a 2×2: static-unused ∩ **not** runtime-loaded = high-confidence cut; static-unused ∩ runtime-loaded = keep (reflection). Evidence not proof (coverage-bounded) — ranks, pairs with AUT.
- **Package references (Damian's gap).** `TargetAssembly` is assembly-level; a paket/NuGet package → 1+ assemblies. Need an assembly→package map (derivable from the resolved DLL paths in `ProjectBuildInfo.References`, which sit under the packages/paket dirs) to roll used-assemblies up to used-packages and diff against declared PackageReferences.

## Verdict
BUILD query-side (no re-index; prototype already proves the numbers + reproduces the human result). Ship candidates as **ranked-by-reflection-risk**, framed as "AUT-gated cut queue," never as verdicts. **Surface fork for Dmytro:** standalone `rig unused-refs` vs folding into `rig refs`/`rig dead`; and whether the reflection-risk column is v1 or a fast-follow. DEFER the runtime-loaded gate and package-refs to follow-on items above.

_Prototype scripts (session scratchpad, transient): `ref-usage.sh` / `ref-usage-v2.sh` / `diff-fix.sh` — the three joins above are the durable record._

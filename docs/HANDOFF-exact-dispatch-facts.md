# Handoff — Exact Roslyn-mined dispatch facts + heuristic provenance

## Principle (the whole point)
Extract as much as Roslyn can resolve EXACTLY, and use it. Fall back to a heuristic ONLY where Roslyn
couldn't bind — and when you do, **mark the edge as heuristic so the user is told.** Today rig resolves
overloads exactly (Roslyn, at extraction) but reconstructs **virtual/interface dispatch** at query time
by NAME (+ a just-added arity gate) over the *type-level* hierarchy — a guess about the
interface-member→impl-member correspondence. That guess produced a real false path
(`IWorkflows.Register(int,IWorkflowController)` → `WorkflowManager.Register(IWorkflowMaster)` — a
same-named overload), which bridged unrelated pages into the Healthcode background job. See the session
that produced this doc, and `project_coderig_status.md` (the "Signature-aware dispatch" entry).

## What's exact vs guessed today (read these first)
- `src/Rig.Analysis/Extraction/FactExtractor.cs` — `model.GetSymbolInfo(name).Symbol` → the EXACT bound
  member; its DocID is the call-edge target (`AddReference`, ~L173). Overload resolution is exact. BUT a
  call through an interface/base receiver binds to the **interface/base declaration**, not the runtime
  impl — that's correct, and unknowable statically.
- `AddTypeRelations` (~L142) emits only **type→type** edges (`base`, `interface`). There is **no
  member-level "impl method implements interface member" or "override" fact.**
- `src/Rig.Domain/Functions/FactPathFinder.cs` — `DispatchTargets` (~L729) reconstructs dispatch from the
  type edges by **name + arity** matching (`AddImplMethods`, the override descendant scan, and the
  error-type simple-name fallback `ImplsByErrorInterfaceName`). THIS is the guess to replace.
- `src/Rig.Storage/Queries/GraphMaterializer.cs` — writes `dispatch_edges` from
  `FactPathFinder.AllDispatchEdges` (CHA superset; bounds the SQL load).
- `src/Rig.Storage/Queries/SqlReachability.cs` — `dispatch_edges` is traversed by the recursive-CTE set
  queries; `LoadBoundedGraphAsync`/`LoadReachInputsAsync` load call_edges + type_relation_facts + methods
  and the in-memory `FactPathFinder` RE-derives dispatch over them. (dispatch_edges is NOT loaded into the
  in-memory graph — it only bounds/serves the SQL set walk.)
- `src/Rig.Cli/CliApplication.cs` — renders dispatch hops (`«impl-dispatch ×N fan-out»`, the reaches
  fan-out bucket, path `[impl-dispatch ×N]`).

## FREEDOMS
- **Drop / re-index mined DBs freely.** This change is an EXTRACTION change → it REQUIRES a re-index to
  get the new facts. `meddbase-analysis/.rig/rig.db` is rebuildable; a backup exists at
  `rig.db.bak-preXprime`. Schema/tables/columns are all changeable — no migration gymnastics, just rebuild.
- Detectors-are-data still holds, but EXACT DISPATCH IS A STRUCTURAL FACT (like the call edge), not rule
  data — it belongs in the facts, mined by Roslyn. No rule JSON needed for it.

## HARD CONSTRAINTS (keep these)
1. **Recall safety.** The mined edges must be a superset-or-equal of today's TRUE dispatch targets for
   bound code. Where Roslyn DIDN'T bind (error-typed `!:` interfaces — pervasive under net48 partial
   binding), KEEP the name/arity CHA fallback — it is the single highest-recall feature in this tool
   (a traced entry point went 9→763 reachable methods via it). Never drop a real target.
2. **SQL == in-memory oracle, per traversal mode.** dispatch_edges (materialized) must equal the edge set
   `FactPathFinder.DispatchTargets` produces (CHA mode), or the equivalence tests in
   `tests/Rig.Tests/Analysis/SqlReachabilityTests.cs` break. Land the extraction + Domain + materializer +
   SQL-bounded-load changes together.
3. Don't regress: receiver-type narrowing, source-order Successors, the async-handoff classification,
   `--sync`/`--async` defaults (callers=sync, reaches/tree/path=async), arity gate stays as the fallback
   matcher for the heuristic path.

## DESIGN

### 1. Mine exact dispatch facts (extraction — Roslyn)
In `FactExtractor.Extract`, add a `DispatchFact` per exactly-resolved dispatch relationship:
- **Override edges:** for each declared method symbol `m` with `m.OverriddenMethod is { } ov`, emit
  `DispatchFact(Source=ov.OriginalDefinition DocID, Target=m.OriginalDefinition DocID, Kind="override")`.
  (Immediate base→override; the transitive chain is reconstructed by forward closure at query time.)
- **Interface-impl edges:** for each `INamedTypeSymbol type`, for each `iface in type.Interfaces` (the
  DIRECTLY-declared interfaces), for each interface member `im` that is an `IMethodSymbol`, compute
  `var impl = type.FindImplementationForInterfaceMember(im)`. When `impl is IMethodSymbol` and resolves,
  emit `DispatchFact(Source=im.OriginalDefinition DocID, Target=impl.OriginalDefinition DocID, Kind="impl")`.
  This is signature-EXACT and maps generics correctly (`IFoo`1.M(`0)` → `Bar.M(System.Int32)`), which is
  exactly what string/arity matching cannot do.
- Dedup by (Source, Target, Kind). First-party targets are what matter for the graph; keep the filter
  consistent with how call edges are kept (TargetInSource semantics — but note the SOURCE may be a
  framework interface; keep an edge when the TARGET is first-party).
- Add `DispatchFact` to `FactExtractionResult` and thread through `SolutionAnalyzer` / `AnalysisResult` /
  `Writes` into a new `dispatch_facts` table (Source TEXT, Target TEXT, Kind TEXT), indexed on Source.
  Mirror in `RigDbContext` (EnsureCreated) + `Writes.MigrateAsync`.

### 2. Domain
- `Facts.cs`: `public sealed record DispatchFact(string SourceMember, string TargetMember, string Kind);`
- `FactGraphData`: add `IReadOnlyList<DispatchFact>? MinedDispatch = null` (default null → behaves like
  today for callers/tests that don't supply it).
- Dispatch-edge provenance: the synthetic dispatch successor needs a `Basis` ("roslyn" | "heuristic").
  Add it to the `Successors` dispatch tuple and to `PathStep`, `TraceNode`, `ReachInfo` (a
  `string? DispatchBasis` or `bool HeuristicDispatch`), inherited like the existing `DispatchVia` tag so a
  reached node knows its reaching dispatch hop was heuristic.

### 3. FactPathFinder.DispatchTargets — facts first, heuristic fallback (flagged)
- When `MinedDispatch` is present, build `source DocID → [(target, kind)]`. Dispatch of method `M`:
  - **impl/override via mined facts** = forward closure following mined edges from `M` (override chain +
    interface impl). Basis="roslyn".
  - Apply the SAME receiver-type narrowing on top (trim to the receiver's subtree) — narrowing is about
    *which runtime type*, orthogonal to *which member*; keep it.
- **Heuristic fallback (Basis="heuristic"), fires only when:**
  - the call's interface/base type is an error type (`!:Name`) → the existing `ImplsByErrorInterfaceName`
    simple-name recovery (Roslyn never bound it, so there's no mined edge), AND/OR
  - `M` has NO mined dispatch edges but the type-hierarchy CHA (name+arity) finds candidates (covers any
    residual binding gaps). Keep the arity gate here.
  - Dedup against the mined set; only emit heuristic targets not already covered.
- `AllDispatchEdges` returns `(From, To, Kind, Basis)` — the mined edges (roslyn) ∪ the heuristic
  fallback. Receiver-blind (CHA superset) as today for the materializer.

### 4. Storage / SQL
- `GraphMaterializer`: `dispatch_edges` gains `Basis TEXT`; write it from `AllDispatchEdges`.
- `Reads.LoadFactGraphAsync`: load `dispatch_facts` into `FactGraphData.MinedDispatch`.
- `SqlReachability.LoadBoundedGraphAsync` / `LoadReachInputsAsync`: load the closure's `dispatch_facts`
  into the bounded `FactGraphData.MinedDispatch` (so the in-memory oracle over the bounded graph uses
  mined dispatch, matching the full-graph oracle). The CTE set queries keep traversing `dispatch_edges`
  (now mined+heuristic) — unchanged shape; `Basis` is render-only, not needed by the set walk.

### 5. CLI — tell the user when a hop is heuristic
- `tree`: a heuristic dispatch hop renders e.g. `«impl-dispatch ~heuristic»` (vs plain `«impl-dispatch»`).
- `path`: `[impl-dispatch (heuristic)]`.
- `reaches`: in the dispatch fan-out bucket, split or annotate heuristic vs roslyn (e.g. a `~heuristic`
  suffix or a trailing count "(N via heuristic dispatch)"). TSV: add a `dispatchBasis` column.
- A one-line legend where useful: "~heuristic = dispatch target inferred by name/arity (Roslyn couldn't
  bind the interface/base — net48 partial binding); ~99% correct, verify before relying on it."

## VALIDATION (fixture-first, then real data)
1. **Fixture (truth by construction, no DB):** extend `playgrounds/LegacyNet48Web` with:
   - an interface with **two overloads** of one method (the `Register(int,X)` vs `Register(Y)` shape) +
     an implementer — assert a call to one overload dispatches ONLY to the matching impl (the bug).
   - a generic interface `IFoo<T>` with `M(T)` + `Bar : IFoo<int>` — assert the mined edge maps
     `M(`0)`→`M(System.Int32)` (string/arity matching alone can't; proves Roslyn mining).
   - an override chain (A.M ← B.M ← C.M) — assert base call reaches all overrides via mined edges.
   Add tests in `tests/Rig.Tests/Analysis/FactDerivationTests.cs` (in-process: analyze → project →
   DispatchTargets/Reaches → assert) and keep `SqlReachabilityTests` green (SQL==oracle, both modes).
   `FactProjection.GraphData` must project `dispatch_facts` into `MinedDispatch` to mirror Reads.
2. **Real data:** re-index `meddbase-analysis` (extraction changed). Use the broad index command from
   `project_coderig_status.md` (X′): pre-build the entry project FIRST (DLL depletion), then
   `rig index <MedDBase.slnx> --from src/main/MedDBase.Site/MedDBase/MedDBase.csproj --rules rig.rules.json --parallelism 12`
   (standalone `index` atomically REPLACES — no append footgun). Then `rig graph`. Confirm:
   - `rig path "Home2ActionsBase.SendFollowUpReferral" "MedicalPersonHealthcodeSettings.#ctor"` = No path
     (the overload-collision bridge stays dead — now because the mined impl edge is exact, not arity luck).
   - `rig callers MedicalPersonHealthcodeSettings --entrypoints` ≈ 2 (sync); the IHealthcodeService
     interface ≈ 28.
   - Report dispatch_edges roslyn-vs-heuristic split (how much of the graph is still heuristic — expect
     the heuristic share to be the net48 `!:` residue).

## SHIP / CONVENTIONS (from project_coderig_status.md — heed these)
- Iterate: `dotnet build src/Rig.Cli/Rig.Cli.csproj -c Release -v q`; query via the global `rig` or the
  Release dll over `meddbase-analysis/.rig`.
- Before shipping: `dotnet tool run csharpier format .`.
- Ship: `pwsh -NoProfile -File scripts/mini-ci.ps1` (NOT `powershell -ExecutionPolicy Bypass …` — the
  harness blocks that). Confirm `rig --version` bumps. mini-ci runs the full suite; it must stay green.
- **`rig index`/`mine` need the GLOBAL (published) bin** — the Debug Rig.Cli.dll throws MEF
  `System.Composition` errors from AdhocWorkspace. So re-index with the freshly-installed global `rig`.
- Skill docs: `C:\Git\coderig\.claude\skills\rig\{SKILL,REFERENCE}.md` (also at `~/.claude/skills/rig/`).
  Document the `~heuristic` marker + the exact-dispatch model. Copy to `~/.claude/skills/rig/` after.
- Update `project_coderig_status.md` with a dated entry (files changed, mined-vs-heuristic numbers,
  shipped version, real-data validation). Don't commit/push (local-only, user-gated) unless asked.
- Don't drop scratch files in the coderig repo root.

## Definition of done
- `dispatch_facts` mined by Roslyn (override + interface-impl, exact incl. generics); loaded into the
  graph; `DispatchTargets` uses them first and falls back to name/arity CHA only for `!:`/unmined cases,
  marking those edges heuristic.
- dispatch_edges carries `Basis`; SQL==oracle holds both modes; receiver-narrowing intact.
- CLI shows `~heuristic` on inferred hops (tree/path/reaches + TSV column) with a one-line legend.
- Fixture cases (overload, generic-impl, override-chain) green; full suite green; csharpier-clean; shipped
  via mini-ci (version bump); meddbase-analysis re-indexed+graphed; the false path stays dead and the
  Healthcode `--entrypoints` numbers hold.
- Report: files changed, the mined-vs-heuristic dispatch-edge split on real data, before/after on the
  known false path, test results, shipped version, residual heuristic share + why.

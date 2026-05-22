# Progress

Living tracker for implementation progress.

Use this file for local coordination until an issue tracker exists. Keep entries
short, update them as slices move, and link commits once completed.

## Status Legend

- `todo`: not started
- `red`: failing contract/test exists
- `green`: behavior works
- `refactor`: cleanup in progress
- `verified`: relevant tests pass after refactor
- `committed`: slice committed

## Milestones

### Phase 0: Repository and Spec

Status: `committed`

- [x] MVP spec
- [x] ubiquitous language
- [x] implementation workflow
- [x] progress tracker

### Phase 1: Core Indexing Skeleton

Status: `in_progress`

- [x] .NET CLI project
- [ ] `.sln` input handling
- [x] `.slnx` input handling
- [x] immutable run creation
- [x] SQLite/EF Core storage
- [ ] profile loading and strict validation
- [ ] appsettings parser
- [x] source inventory and skip decisions
- [x] first playground solution

### Phase 2: Roslyn Observations and MS DI

Status: `in_progress`

- [x] Roslyn solution loading
- [x] compilation failure reporting
- [x] symbol/method/invocation observations
- [ ] string-template extraction
- [x] MS DI registration facts
- [ ] hosted service detection
- [ ] evidence metadata on facts

### Phase 3: Entry Points and Callgraph

Status: `in_progress`

- [x] Minimal API entrypoint detection
- [x] MVC entrypoint detection
- [x] application-only bounded callgraph
- [x] external boundary nodes
- [x] unresolved call nodes
- [x] edge confidence/basis/reason
- [ ] cycle detection

### Phase 4: HTTP, EF Core, and Redis Effects

Status: `in_progress`

- [x] HttpClient effects
- [x] simple host/path resolution
- [x] EF Core read effects
- [x] EF Core write/commit effects
- [x] Redis read/write effects
- [x] effects linked to callgraph nodes

### Phase 5: Effect Contexts and Observations

Status: `in_progress`

- [x] loop/foreach/while contexts
- [ ] LINQ effectful lambda contexts
- [x] `Task.WhenAll`
- [ ] `Parallel.ForEach`
- [ ] `Parallel.ForEachAsync`
- [x] `looped_effect`
- [x] `parallel_fanout`
- [ ] `unresolved_resource`
- [ ] `unresolved_call_target`

### Phase 6: Built-In Packs and Playground Expansion

Status: `in_progress`

- [x] current entrypoint/effect/file/DI rules externalized
- [x] Minimal API playground
- [x] MVC playground
- [x] deterministic regression tests

### Phase 7: Diff and Agent Projections

Status: `todo`

- [ ] `rig effects --changed`
- [ ] run-to-run effect diff
- [ ] `--json`
- [ ] `--terse`
- [ ] `--effect-paths`
- [ ] compact callgraph projections

## Current Slice

Slice: Solution analyzer responsibility split
Phase: refactor
Status: `verified`

Contract:
  - reduce `SolutionAnalyzer` to orchestration.
  - split Roslyn loading, rule loading, source inventory, extractors, observations, and callgraph building into focused files.
  - keep public `SolutionAnalyzer.AnalyzeAsync` contract unchanged.
  - scan for dead code after the move.
  - preserve current CLI output.

Verification:
  - `dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false` passes with 4 tests.
  - `dotnet build RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false -warnaserror` passes with 0 warnings.
  - `dotnet run --project src/Rig -- index playgrounds/EntryPointEffects/EntryPointEffects.slnx` reports 4 entrypoints and 8 effects.
  - `dotnet run --project src/Rig -- runs` lists persisted run metadata with `di=4`.
  - `dotnet run --project src/Rig -- callgraph "minapi GET /minapi/teams/{id}"` prints 5 compilation-backed nodes with inline effects, external boundaries, and an unresolved boundary.
  - dead-code scan found no stale moved helper methods or duplicate private model types.

Commit:
  - pending

## Next Suggested Slice

Slice: Use DI facts for constructor/interface call resolution
Phase: 2/3
Status: `todo`

Contract:
  - map constructor-injected fields/properties back to DI facts.
  - resolve interface/service calls to implementation calls where DI facts are exact.
  - label DI-derived edges with confidence, basis, reason, and evidence.
  - preserve current CLI output.

Notes:
  - use `/p:UseSharedCompilation=false` while compiler-server timeouts remain possible.
  - then add `rig di` or a general facts query surface so DI facts are visible without inspecting JSON/SQLite.

Use this template when starting one:

```text
Slice:
Phase:
Status:

Contract:
  -

Verification:
  -

Commit:
  -
```

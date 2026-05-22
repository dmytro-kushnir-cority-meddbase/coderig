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
- [ ] immutable run creation
- [ ] SQLite/EF Core storage
- [ ] profile loading and strict validation
- [ ] appsettings parser
- [ ] source inventory and skip decisions
- [x] first playground solution

### Phase 2: Roslyn Observations and MS DI

Status: `todo`

- [ ] Roslyn solution loading
- [ ] compilation failure reporting
- [ ] symbol/method/invocation observations
- [ ] string-template extraction
- [ ] MS DI registration facts
- [ ] hosted service detection
- [ ] evidence metadata on facts

### Phase 3: Entry Points and Callgraph

Status: `in_progress`

- [x] Minimal API entrypoint detection
- [x] MVC entrypoint detection
- [x] application-only bounded callgraph
- [ ] external boundary nodes
- [ ] unresolved call nodes
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

- [ ] Milestone 0 built-in profile packs
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

Slice: Shallow callgraph with inline effects
Phase: 3/4
Status: `committed`

Contract:
  - analyzer emits a callgraph per detected entrypoint.
  - Minimal API GET entrypoint reaches `TeamWorkflow.LoadTeamSummaryAsync`.
  - `TeamWorkflow.LoadTeamSummaryAsync` reaches both billing client methods.
  - callgraph nodes include inline EF Core, Redis, and HTTP effects.
  - `rig callgraph "minapi GET /minapi/teams/{id}"` prints calls, effects, and observations.

Verification:
  - `dotnet test RuntimeIntelligenceGraph.slnx` passes with 4 tests.
  - `dotnet build playgrounds/EntryPointEffects/EntryPointEffects.slnx` passes.
  - `dotnet run --project src/Rig -- index playgrounds/EntryPointEffects/EntryPointEffects.slnx` reports 4 entrypoints and 8 effects.
  - `dotnet run --project src/Rig -- callgraph "minapi GET /minapi/teams/{id}"` prints 4 nodes with inline effects.

Commit:
  - `08639b0 Add shallow callgraph output`

## Next Suggested Slice

Slice: Roslyn-backed observations and symbol resolution
Phase: 2/3
Status: `todo`

Contract:
  - load `.slnx` through Roslyn/MSBuild workspace.
  - fail loudly on compilation errors.
  - emit method and invocation observations as explicit records.
  - resolve `TeamWorkflow` and `BillingClient` calls by symbol rather than string names.
  - preserve current `rig entrypoints`, `rig effects`, and `rig callgraph` output.

Notes:
  - keep the current syntax analyzer as a temporary fallback only if it helps the transition.
  - use `/p:UseSharedCompilation=false` while compiler-server timeouts remain possible.

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

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

Status: `todo`

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

Status: `todo`

- [x] Minimal API entrypoint detection
- [x] MVC entrypoint detection
- [ ] application-only bounded callgraph
- [ ] external boundary nodes
- [ ] unresolved call nodes
- [ ] edge confidence/basis/reason
- [ ] cycle detection

### Phase 4: HTTP, EF Core, and Redis Effects

Status: `todo`

- [x] HttpClient effects
- [x] simple host/path resolution
- [x] EF Core read effects
- [x] EF Core write/commit effects
- [x] Redis read/write effects
- [ ] effects linked to callgraph nodes

### Phase 5: Effect Contexts and Observations

Status: `todo`

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

Status: `todo`

- [ ] Milestone 0 built-in profile packs
- [ ] Minimal API playground
- [ ] MVC playground
- [ ] deterministic regression tests

### Phase 7: Diff and Agent Projections

Status: `todo`

- [ ] `rig effects --changed`
- [ ] run-to-run effect diff
- [ ] `--json`
- [ ] `--terse`
- [ ] `--effect-paths`
- [ ] compact callgraph projections

## Current Slice

Slice: Effect contexts for looped and parallel fanout effects
Phase: 5
Status: `committed`

Contract:
  - playground solution contains a Redis read inside `foreach`.
  - playground solution contains an HttpClient call inside `Task.WhenAll`.
  - analyzer emits `looped_effect` on the Redis effect.
  - analyzer emits `parallel_fanout` on the HTTP effect.
  - `rig effects` prints observation lines under affected effects.

Verification:
  - `dotnet test RuntimeIntelligenceGraph.slnx` passes with 4 tests.
  - `dotnet build playgrounds/EntryPointEffects/EntryPointEffects.slnx` passes.
  - `dotnet run --project src/Rig -- index playgrounds/EntryPointEffects/EntryPointEffects.slnx` reports 4 entrypoints and 8 effects.
  - `dotnet run --project src/Rig -- effects` prints `OBS looped_effect` and `OBS parallel_fanout`.

Commit:
  - this commit

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

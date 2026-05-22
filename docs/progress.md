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

Slice: Generic class-inheritance entrypoint rules
Phase: 3/6
Status: `verified`

Contract:
  - model framework entrypoints as external JSON rules, not custom C# detectors.
  - allow solution-local `rig.rules.json` to extend entrypoint, effect, DI, and file rules.
  - match host classes by Roslyn base-type inheritance chain.
  - use configured route-provider, route-builder, handler method, and override requirements.
  - add declaring-type filters to effect rules so method-name-only matches do not produce false positives.
  - preserve current playground behavior except for the new fixture entrypoint.

Verification:
  - `dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false` passes with 5 tests.
  - `dotnet build RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false -warnaserror` passes with 0 warnings.
  - `playgrounds/CleanArchitecture` vendors the public CleanArchitecture target without `.git`, IDE folders, or build outputs.
  - `CleanArchitecturePlaygroundTests` restores the vendored solution and asserts the reconciled 5 entrypoints and 24 effects.
  - `dotnet run --project src/Rig -- index $env:TEMP\coderig-targets\CleanArchitecture\Clean.Architecture.slnx` reports 5 FastEndpoints entrypoints and 24 effects.
  - `dotnet run --project src/Rig -- entrypoints` lists CleanArchitecture `DELETE/GET/POST/PUT /Contributors...` plus `GET /Contributors`.
  - `dotnet run --project src/Rig -- effects` no longer reports `MimeMessage.*.Add` as EF Core pending writes.
  - independent source inspection reconciled current output against CleanArchitecture:
    current rules now detect `ExecuteAsync` and `HandleAsync` FastEndpoints endpoints, chained EF `ToListAsync`,
    startup migration/seed SQL, Ardalis repository effects, MailKit SMTP effects, and Mediator/MediatR send/publish.

Commit:
  - pending

## Next Suggested Slice

Slice: Expand generic entrypoint/rule matching coverage
Phase: 2/3
Status: `todo`

Contract:
  - support additional handler methods such as FastEndpoints `HandleAsync` through JSON only.
  - improve EF query-chain resource extraction so `DbSet...ToListAsync()` chains resolve back to the root DbSet.
  - add declarative effect rules for EF migration/raw SQL methods and MailKit SMTP.
  - add optional type/namespace receiver filters for DI registration rules.
  - expose class-inheritance rule docs/examples for solution-local `rig.rules.json`.
  - preserve current CLI output.

Notes:
  - use `/p:UseSharedCompilation=false` while compiler-server timeouts remain possible.
  - after this, use DI facts for constructor/interface call resolution.

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

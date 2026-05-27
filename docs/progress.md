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
- [x] `.sln` input handling
- [x] `.slnx` input handling
- [x] immutable run creation
- [x] SQLite/EF Core storage
- [x] profile loading and strict validation
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
- [x] minAPI method-ref handler body resolution (fluent chain start-line fix)
- [x] cycle detection

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
- [x] `Parallel.ForEach`
- [x] `Parallel.ForEachAsync`
- [x] `looped_effect`
- [x] `parallel_fanout`
- [x] `resilience_retry`
- [x] `read_before_commit` (EF commit preceded by EF read in same method — potential lost update)
- [x] `concurrency_handled` (SaveChangesAsync inside catch for DbUpdateConcurrencyException)
- [ ] `unresolved_resource`
- [ ] `unresolved_call_target`

### Phase 6: Built-In Packs and Playground Expansion

Status: `in_progress`

- [x] current entrypoint/effect/file/DI rules externalized
- [x] Minimal API playground
- [x] MVC playground
- [x] deterministic regression tests
- [x] OrchardCore playground indexed (296 EPs, 1007+ effects)
- [x] YesSql IQuery/ExecuteQuery/SessionExtensions rules for OrchardCore
- [x] tree callgraph rendering with box-drawing characters
- [x] `--focus` mode (backward BFS, effect-reachable nodes only)
- [x] parallel compilation + MSBuild progress reporting for `rig index`
- [x] eShop playground indexed (61 EPs, 109 effects: EF Core, Redis, EventBus, RabbitMQ, DB lifecycle/reader, Npgsql, resilience, AI embeddings)
- [x] `GenerateVectorAsync` extension method rule for AI embeddings
- [x] eShop TOCTOU/concurrency review: `read_before_commit` and `concurrency_handled` observations on `efcore commit` effects
- [x] 5 IdentityServer read rules (IIdentityServerInteractionService, IDeviceFlowInteractionService, IClientStore, IClientStoreExtensions, IResourceStoreExtensions)
- [x] Sort all effects, boundary calls, and application calls by method-name-token line (source reading order)
- [x] Default callgraph mode is focused; `--full` flag for verbose tree; `--summary` for flat effect inventory

### Phase 7: Diff and Agent Projections

Status: `todo`

- [ ] `rig effects --changed`
- [ ] run-to-run effect diff
- [ ] `--json`
- [ ] `--terse`
- [ ] `--effect-paths`
- [ ] compact callgraph projections

## Completed Slices (recent)

### Callgraph cycle detection

Status: `verified`

- Added deterministic EntryPointEffects playground samples for self-recursion, two-method mutual recursion, and a three-method cycle.
- Added `CallGraphCycleDetector` and `CallGraphCycleInfo`; callgraphs now carry detected cycles derived from persisted node calls.
- `rig callgraph --full` prints `Cycles: N`, `CYCLE ...` summaries, and marks repeated cycle edges as `[cycle]`.
- Verification: `dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false`.

### Classified effectful boundaries

Status: `verified`

- Added built-in RabbitMQ publish classification for `RabbitMQ.Client.IChannel.BasicPublishAsync`.
- Added high/medium boundary classifiers for RabbitMQ channel open, RabbitMQ exchange declare, DB connection open, DB reader row read, and resilience pipeline execution.
- Trace/callgraph renderers now replace matching `BOUNDARY` lines with the detected `EFFECT` at the same invocation position, preserving source-order reading for effectful external calls.
- Rebuilt local eShop index: 61 entrypoints, 109 effects.
- Verification: `dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false`; `dotnet run --project src/Rig -- trace --contains GracePeriodManagerService.ExecuteAsync --paths`.

### Mini CI and local tool install

Status: `verified`

- Added `scripts/mini-ci.ps1` to restore, Release build with warnings as errors, run Release tests, pack `src/Rig` as a dotnet tool, and reinstall the global `rig` tool from `.rig-nupkg`.
- Script generates a unique local prerelease tool version from `Rig.csproj` version plus timestamp to avoid stale NuGet cache when iterating locally.
- Installed global tool: `rig 0.1.1-ci.20260526110212`.
- Verification: `powershell -ExecutionPolicy Bypass -File .\scripts\mini-ci.ps1`; `powershell -ExecutionPolicy Bypass -File .\scripts\mini-ci.ps1 -SkipTests`.

### Background worker and integration event entrypoints

Status: `verified`

- Added built-in `dotnet.backgroundservice.execute` rule for `BackgroundService.ExecuteAsync`.
- Extended class-inheritance matching to implemented interfaces, then added `eshop.integration_event_handler` for `IIntegrationEventHandler<T>.Handle`.
- eShop now indexes background/event-handler entrypoints including `GracePeriodManagerService.ExecuteAsync` and payment/order/webhook integration handlers.
- Rebuilt local eShop index: 61 entrypoints, 100 effects before RabbitMQ concrete publish classification.
- Verification: `dotnet build RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false -warnaserror`; `dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false`.

### gRPC service entrypoint detection

Status: `verified`

- Extended class-inheritance entrypoint rules with wildcard handler methods, default method labels, and optional handler parameter type predicates.
- Added built-in `dotnet.grpc.service` rule for override methods with `Grpc.Core.ServerCallContext`.
- eShop Basket gRPC service methods now index as `grpc RPC` entrypoints and their callgraphs reach Redis effects.
- Rebuilt local eShop index: 44 entrypoints, 100 effects.
- Verification: `dotnet build RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false -warnaserror`; `dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false`.

### Reverse symbol trace

Status: `verified`

- Added `rig trace <symbol> [--paths]` and `rig trace --contains <text> [--paths]`.
- Default trace output lists entrypoints whose persisted callgraph contains the target symbol.
- `--paths` builds reverse edges in memory per invocation, prints upstream paths to the target, then prints downstream calls, boundaries, and effects from the target.
- No reverse graph or transitive reachability table was added; trace is derived from persisted forward callgraph rows.
- Documented trace complexity and future indexing thresholds in `docs/sqlite-persistence-notes.md`.
- Verification: `dotnet build RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false -warnaserror`; `dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false`.

### Focused fast test expansion

Status: `verified`

- Added small tests for `RuleTypeMatcher`, `GlobMatcher`, `CallGraphRenderer`, and CLI validation errors.
- Added small tests for source-file classification precedence without loading a Roslyn workspace.
- Added small tests for profile validation and solution/extra rule merging.
- Kept the Roslyn/MSBuild coverage as integration smoke tests.
- Fixed glob semantics so leading `**/` matches zero or more directories, which makes generated-file profile rules work at solution root as well as nested paths.
- Verification: `dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false` — 21 tests green.

### Roslyn integration test isolation

Status: `verified`

- Removed the vendored CleanArchitecture playground and its direct test fixture.
- Roslyn-backed tests now copy the owned `EntryPointEffects` playground to a fresh temp directory and restore it before analysis.
- `EntryPointEffects` now carries local `Directory.Packages.props` and `NuGet.config`, so temp copies do not depend on repo-root package metadata or user-level NuGet sources.
- MSBuild/Roslyn integration tests share a non-parallel xUnit collection to avoid global MSBuild/tooling contention.
- Package check: test packages are already current stable on NuGet (`xunit` 2.9.3, `Microsoft.NET.Test.Sdk` 18.5.1, `xunit.runner.visualstudio` 3.1.5).

### Refactoring pass: CLI rendering, effect observations, callgraph indexes

Status: `verified`

- `CliApplication` now delegates text projection to focused renderers under `src/Rig/Cli/Rendering`.
- `EffectObservationExtractor` owns contextual effect observations; `EffectExtractor` stays focused on rule matching and resource extraction.
- `RuleTypeMatcher` centralizes repeated rule type-name matching.
- `CallGraphIndexes` owns dispatch and single-implementation DI index construction; `CallGraphBuilder` keeps traversal and node construction.
- Stale CLI test assertion updated to use `rig callgraph 6 --full` for boundary rendering; default callgraph mode remains focused.
- Verification: `dotnet build RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false`; `dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false --no-build`.

### EF versioning observations: read_before_commit, concurrency_handled

Status: `committed`

- `FindReadBeforeCommitContext`: when `SaveChangesAsync` fires in a method that already has an EF read (`ToListAsync`, `FindAsync`, etc.) earlier in the same body, attaches `[read_before_commit:before_commit]`. Signals a potential lost-update / TOCTOU site where no concurrency guard is visible.
- `FindConcurrencyHandlingContext`: when `SaveChangesAsync` is inside a `try` whose `catch` clauses catch `DbUpdateConcurrencyException` or `DbUpdateException`, attaches `[concurrency_handled:DbUpdateConcurrencyException]`. Confirms that optimistic concurrency is explicitly handled.
- Both observations are in `EffectExtractor.AttachObservations`. Medium-confidence, Roslyn-compilation basis.
- eShop EP [12] `PUT /items` and EP [40] `DELETE /webhook` now show `[read_before_commit:before_commit]` — the two genuine TOCTOU candidates in that codebase.


### .sln support, multi-file profile loading, Parallel.ForEach/ForEachAsync fanout

Status: `committed` (9dcd8c7)

- `SolutionSourceLoader` filters to `LanguageNames.CSharp` projects only; enables .sln files with non-C# projects.
- `AnalysisRuleSet.LoadForSolution`: cascade merge — built-in → global (`~/.rig/rig.rules.json`) → solution-level → per-project.
- `SolutionSourceSet` exposes `ProjectDirectories`; `SolutionAnalyzer` calls `MergeWithProjectDirectories` post-load.
- `Parallel.ForEach` and `Parallel.ForEachAsync` fanout detection exercised by `TeamWorkflow.ProcessBatchAsync`.
- `PlaygroundAnalysisTests` and `CliApplicationTests` updated (19 effects, two new `parallel_fanout` observations).

### Focused read queries per CLI command

Status: `committed` (93acf8e)

- Each CLI command queries only the tables it needs; `LoadLatestOrErrorAsync` removed from `CliApplication`.
- `GetLatestRunIdAsync`: shared primitive; returns `null` if DB is empty or missing.
- `LoadSkippedSourceFilesAsync`: WHERE pushed to SQL (`status='skipped'`).
- `LoadCallGraphAsync(runId, entryPoint)`: single graph + its nodes/calls/boundary-calls/effects.
- `BuildEffects`: private helper shared across full and focused load paths.
- Timings (R2R, win-x64): `entrypoints` ~300ms | `effects` ~315ms | `callgraph` ~350ms.

### MediatR dispatch resolution

Status: `committed` (2a49a20)

- `treatAsDispatch` rule flag routes `mediator.Send`/`Publish` to handler implementations.
- CallGraphBuilder builds a dispatch index from DI facts and resolves handler types.
- EntryPointEffects playground: 17 effects (previously 19 before deduplication).

### Read/write decoupling + new CLI commands

Status: `committed` (6fba295)

- `LoadLatestAsync` replaced: reads from ~10 normalized tables instead of JSON blob.
- New `callgraph_node_effects` join table links per-node effects to global effects table.
- `rig di` command — lists DI registrations from DB.
- `rig profile validate` — validates solution-local rules file.

### Performance: R2R binary + EF compiled model

Status: `committed` (d35847d / 3e3fa13)

- R2R published binary at `.rig-bin/Rig.exe` (gitignored).
- EF compiled model auto-generated via `Microsoft.EntityFrameworkCore.Tasks` MSBuild package.
  `EFScaffoldModelStage=build` regenerates `Storage/Compiled/*.g.cs` on every build; directory gitignored.
- EF query precompilation disabled (`EFPrecompileQueriesStage=never`) — conflicts with MSBuild.Framework
  version pulled in by Roslyn workspace loading; TODO comment left in csproj.
- Timings (R2R + compiled model): baseline 67ms | `rig effects` 372ms | `rig callgraph` 370ms.
  Previously with `dotnet run`: ~2800ms.

## Current Slice

Slice: Trace ergonomics from random code locations
Phase: 7
Status: `todo`

Contract:

- map `file:line` to containing indexed method symbol.
- store method declaration start/end lines or equivalent span metadata.
- route `rig trace --file <path> --line <line>` through the same trace query.
- preserve snapshot semantics: answer from latest completed run only.

Notes:

- Current `rig trace` works when the target callgraph symbol is known, or can be uniquely found by `--contains`.
- Existing method observations store declaration line only, not body span, so arbitrary cursor-line lookup needs new method span metadata.
- MCP server work is deferred; avoiding live state and cache invalidation is intentional.

## Next Suggested Slice

Slice: Trace from file/line
Phase: 7
Status: `todo`

Contract:

- map `file:line` to containing indexed method symbol.
- store method declaration start/end lines or equivalent span metadata.
- route `rig trace --file <path> --line <line>` through the same trace query.
- preserve snapshot semantics: answer from latest completed run only.

Notes:

- Current `rig trace` works when the target callgraph symbol is known, or can be uniquely found by `--contains`.
- Existing method observations store declaration line only, not body span, so arbitrary cursor-line lookup needs new method span metadata.
- use `/p:UseSharedCompilation=false` while compiler-server timeouts remain possible.
- Parked metadata idea: persist the `rig` tool/package version with each run so future DB reads can explain which analyzer version produced a run.

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

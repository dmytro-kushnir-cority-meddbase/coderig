# Handover

Current handover after background/event-handler entrypoints, gRPC entrypoint detection, reverse symbol trace, classified effectful boundaries, eShop playground, parallel index progress reporting, and minAPI method-ref fix.

## Current State

The repo contains a CLI-first .NET 10 prototype split across three projects:

- **`src/Rig.Cli`** — CLI exe and human-readable renderers
- **`src/Rig.Analysis`** — Roslyn/MSBuild loading, rule loading, extraction, and callgraph construction
- **`src/Rig.Domain`** — domain model records (`AnalysisResult`, `EntryPointInfo`, `EffectInfo`, etc.)
- **`src/Rig.Storage`** — EF Core + SQLite; `RigDbContext`, `Reads.cs`, `Writes.cs`

Published R2R binary at `.rig-bin/Rig.exe` (gitignored). Build with:

```powershell
dotnet publish src/Rig.Cli/Rig.Cli.csproj -c Release -r win-x64 --self-contained -o .rig-bin `
  -p:PublishReadyToRun=true -p:DebugSymbols=false -p:DebugType=none `
  /p:TreatWarningsAsErrors=false
```

Working commands:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\mini-ci.ps1
.\.rig-bin\Rig.exe index playgrounds/EntryPointEffects/EntryPointEffects.slnx
.\.rig-bin\Rig.exe runs
.\.rig-bin\Rig.exe entrypoints
.\.rig-bin\Rig.exe effects [--entrypoint <index>]
.\.rig-bin\Rig.exe trace <symbol> [--paths]
.\.rig-bin\Rig.exe trace --contains <text> [--paths]
.\.rig-bin\Rig.exe callgraph <index> [--focus]
.\.rig-bin\Rig.exe di
.\.rig-bin\Rig.exe files --skipped
.\.rig-bin\Rig.exe profile validate
```

Timings (R2R, win-x64): `entrypoints`/`di`/`files` ~295ms | `effects` ~315ms | `callgraph` ~350ms.

## Architecture

**Three-project structure**:

- `Rig.Domain` has no dependencies — pure C# records.
- `Rig.Storage` references `Rig.Domain`; contains `RigDbContext` and all EF entity classes.
  `Reads.cs` exposes focused per-command queries (no JSON blob load).
  `Writes.cs` writes to ~12 normalized tables.
- `Rig` references both; holds Roslyn analysis, rules, CLI routing.

**Focused read queries** (`Rig.Storage/Queries/Reads.cs`): each CLI command hits only the tables it needs.
`GetLatestRunIdAsync` is the shared primitive; the old full `LoadLatestAsync` path has been removed.

**Multi-file profile loading** (`AnalysisRuleSet.LoadForSolution`): cascades
built-in → global (`~/.rig/rig.rules.json`) → solution-level (`rig.rules.json`) → per-project `rig.rules.json`.
`SolutionSourceSet.ProjectDirectories` enables the per-project merge after workspace load.

**Callgraph construction**: `CallGraphBuilder` owns orchestration and traversal.
`EntryNodeResolver` builds entry nodes, `CallResolver` resolves invocation and method-group calls,
`CallGraphIndexes` owns dispatch and single-implementation DI indexes, and `CallGraphNodeFactory`
owns the final node projection shape.

**.sln support**: `SolutionSourceLoader` filters `solution.Projects` to `LanguageNames.CSharp` only.
Non-C# projects (e.g. F#, VB) in a `.sln` are silently skipped.

**EF compiled model**: auto-generated at build time by `Microsoft.EntityFrameworkCore.Tasks`.
`EFScaffoldModelStage=build` triggers regeneration on model change.
Generated files in `Storage/Compiled/*.g.cs` (gitignored).
Query precompilation disabled (`EFPrecompileQueriesStage=never`) — conflicts with MSBuild.Framework
version pulled in by Roslyn; TODO in `Rig.Storage.csproj`.

**MCP opportunity**: ~350ms per CLI call is fine for humans but too slow for agentic loops.
An MCP server would open `RigDbContext` once and serve `Reads.cs` queries in <5ms.
`ModelContextProtocol` NuGet package (Microsoft) is the SDK.
This is intentionally deferred for now: avoiding live state and cache invalidation
is more important than shaving subprocess cost until measured workflows demand it.

## Playgrounds

- `playgrounds/EntryPointEffects/EntryPointEffects.slnx` — primary fast-iteration target.
  11 entry points, 23 effects. Index in ~10s. Exercises MVC, MinAPI, FastEndpoints,
  EF Core, Redis, HTTP client, single-impl DI dispatch, method-group delegates.
  It also includes cycle fixtures for self-recursion, two-method mutual recursion,
  and a three-method cycle.
- `playgrounds/OrchardCore/OrchardCore.slnx` — large real-world CMS.
  296 entry points, 1007+ effects across 190/296 EPs. Index in ~5 minutes.
  Has `rig.rules.json` with rules for: IMemoryCache, IDistributedCache, IMessageBus,
  ISignal, IFileStore/IMediaFileStore, OpenIddict managers, IDocumentManager,
  IShellSettingsManager, IDeploymentTargetHandler, IDisplayManager,
  YesSql IQuery terminators (FirstOrDefaultAsync/ListAsync), ExecuteQuery, SessionExtensions.GetAsync.
- `playgrounds/eShop/eShop.slnx` — dotnet/eShop multi-service e-commerce sample.
  61 entry points (Basket.API gRPC, background/event handlers, Catalog.API MinAPI, Identity.API MVC, Ordering.API MinAPI, Webhooks.API MinAPI),
  109 effects: EF Core (CatalogContext, OrderingContext, WebhooksContext), Redis (StringGetLeaseAsync,
  StringSetAsync, KeyDeleteAsync), EventBus (RabbitMQ PublishAsync with argument_type resolution),
  RabbitMQ concrete publish/channel/exchange effects, DB connection/reader effects, resilience pipeline execution,
  Npgsql raw SQL, AI embeddings (GenerateAsync + GenerateVectorAsync extension method). Index in ~20s locally after restore.
  Note: Basket.API gRPC entrypoints expose Redis read/write/delete paths; background/event-handler entrypoints expose OrderProcessor and PaymentProcessor RabbitMQ publish paths. WebApp/HybridApp/WebAppComponents excluded
  (Blazor SSR and MAUI — MSBuildWorkspace can't compile Razor/XAML codegen).
  MediatR dispatch in Ordering.API is not traversed (dynamic dispatch).
CleanArchitecture is no longer vendored in this repo or used as a unit/integration
test fixture. Roslyn-backed tests should use owned playgrounds copied to a fresh
temp directory per test run.

## Verification

```text
dotnet test                    # 4 tests, all green
.\.rig-bin\Rig.exe effects     # ~315ms with R2R binary
```

## Callgraph Rendering

`rig callgraph <index>` renders a tree using box-drawing characters (├─ / └─ / │).
Detected cycles print as `Cycles: N` plus `CYCLE ...` summaries. Repeated cycle
edges render as `[cycle]`; non-cycle repeated nodes render as `[^]`.

`--focus` mode: backward BFS from effect nodes, keeps only effect-reachable ancestors.
Drops all BOUNDARY lines; CALL edges trimmed to reachable targets only.
Header shows `(focused)` and `Nodes: X / Y on effect paths`.

## Reverse Trace

`rig trace <symbol>` lists entrypoints whose persisted callgraph reaches the
target symbol. `rig trace --contains <text>` resolves a unique callgraph symbol
substring first and errors on ambiguous matches. `--paths` prints upstream paths
from entrypoint roots to the target and downstream calls, boundaries, and effects
from the target.

Trace derives reverse edges in memory from `callgraph_node_calls`; no reverse
edge table or transitive reachability cache is persisted. Complexity is `O(N+E)`
over loaded matching graphs. See `docs/sqlite-persistence-notes.md`.
Callgraph node symbols and node-call targets store full Roslyn method keys when
available; CLI visuals shorten those keys at render time.

Effectful external boundary calls render in source position as `EFFECT` when a
boundary and effect share the same file/line/method. Unmatched external calls
still render as `BOUNDARY` in full trace/callgraph views.

## Important Caveats

- Callgraph traversal uses single-impl DI dispatch (1 concrete registration → resolves to it).
  Multi-impl interfaces leave an empty interface node — add a rule for the interface to capture effects.
- Constructor injection resolution, cycles, and richer dynamic dispatch are not fully implemented.
- EF query precompilation conflict: `EFPrecompileQueriesStage=never` in `Rig.Storage.csproj`.
- `runs.AnalysisResultJson` column still exists in the schema (written as empty string, never read); can be dropped in a future migration.
- `string_argument` resource returns null for non-literal first arguments — use `receiver_type` instead.

## Recommended Next Slice

**Trace ergonomics from random code locations** — map `file:line` to the
containing indexed method symbol, then route it through `rig trace`.
This needs method declaration start/end lines or equivalent span metadata in
method observations. Keep the answer tied to the latest completed run; do not
rebuild or inspect live source behind the user's back.

Other candidates:
- **Cycle detection** — annotate back-edges in callgraph traversal and expose in CLI.
- **More OrchardCore rules** — query top boundary calls in the DB, add coverage for uncaught effects.
- **`unresolved_resource` observation** — flag effects where the resource string couldn't be resolved.

## Useful Files

- `docs/mvp-spec.md`
- `docs/ubiquitous-language.md`
- `docs/progress.md`
- `docs/sqlite-persistence-notes.md`
- `src/Rig.Analysis/Rules/builtin-rules.json`
- `src/Rig.Analysis/Analysis/SolutionAnalyzer.cs`
- `src/Rig.Domain/AnalysisResult.cs`
- `src/Rig.Analysis/Analysis/CallGraph/CallGraphBuilder.cs`
- `src/Rig.Analysis/Analysis/CallGraph/CallGraphIndexes.cs`
- `src/Rig.Analysis/Analysis/Extraction/`
- `src/Rig.Analysis/Analysis/Rules/RuleTypeMatcher.cs`
- `src/Rig.Analysis/Analysis/Inventory/SolutionSourceLoader.cs`
- `src/Rig.Analysis/Analysis/Rules/AnalysisRuleSet.cs`
- `src/Rig.Cli/Cli/CliApplication.cs`
- `src/Rig.Cli/Cli/Rendering/`
- `src/Rig.Storage/RigDbContext.cs`
- `src/Rig.Storage/Queries/Reads.cs`
- `src/Rig.Storage/Queries/Writes.cs`
- `scripts/mini-ci.ps1`
- `playgrounds/EntryPointEffects/EntryPointEffects.slnx`
- `playgrounds/EntryPointEffects/rig.rules.json`
- `tests/Rig.Tests/Analysis/PlaygroundAnalysisTests.cs`
- `tests/Rig.Tests/Cli/CliApplicationTests.cs`

- `playgrounds/OrchardCore/OrchardCore.slnx`
- `playgrounds/OrchardCore/rig.rules.json`

Working commands:

```text
dotnet run --project src/Rig.Cli -- index playgrounds/EntryPointEffects/EntryPointEffects.slnx
dotnet run --project src/Rig.Cli -- runs
dotnet run --project src/Rig.Cli -- entrypoints
dotnet run --project src/Rig.Cli -- effects
dotnet run --project src/Rig.Cli -- trace --contains TeamWorkflow.LoadTeamSummaryAsync --paths
dotnet run --project src/Rig.Cli -- trace --contains RedisBasketRepository.GetBasketAsync --paths
dotnet run --project src/Rig.Cli -- trace --contains GracePeriodManagerService.ExecuteAsync --paths
dotnet run --project src/Rig.Cli -- files --skipped
dotnet run --project src/Rig.Cli -- callgraph "minapi GET /minapi/teams/{id}"
```

# Handover

Current handover after eShop playground, parallel index progress reporting, and minAPI method-ref fix.

## Current State

The repo contains a CLI-first .NET 10 prototype split across three projects:

- **`src/Rig`** — CLI exe; Roslyn workspace loading and analysis lives here
- **`src/Rig.Domain`** — domain model records (`AnalysisResult`, `EntryPointInfo`, `EffectInfo`, etc.)
- **`src/Rig.Storage`** — EF Core + SQLite; `RigDbContext`, `Reads.cs`, `Writes.cs`

Published R2R binary at `.rig-bin/Rig.exe` (gitignored). Build with:

```powershell
dotnet publish src/Rig/Rig.csproj -c Release -r win-x64 --self-contained -o .rig-bin `
  -p:PublishReadyToRun=true -p:DebugSymbols=false -p:DebugType=none `
  /p:TreatWarningsAsErrors=false
```

Working commands:

```powershell
.\.rig-bin\Rig.exe index playgrounds/EntryPointEffects/EntryPointEffects.slnx
.\.rig-bin\Rig.exe runs
.\.rig-bin\Rig.exe entrypoints
.\.rig-bin\Rig.exe effects [--entrypoint <index>]
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
`GetLatestRunIdAsync` is the shared primitive. `LoadLatestAsync` (full load) is preserved for future use.

**Multi-file profile loading** (`AnalysisRuleSet.LoadForSolution`): cascades
built-in → global (`~/.rig/rig.rules.json`) → solution-level (`rig.rules.json`) → per-project `rig.rules.json`.
`SolutionSourceSet.ProjectDirectories` enables the per-project merge after workspace load.

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

## Playgrounds

- `playgrounds/EntryPointEffects/EntryPointEffects.slnx` — primary fast-iteration target.
  8 entry points, 23 effects. Index in ~10s. Exercises MVC, MinAPI, FastEndpoints,
  EF Core, Redis, HTTP client, single-impl DI dispatch, method-group delegates.
- `playgrounds/OrchardCore/OrchardCore.slnx` — large real-world CMS.
  296 entry points, 1007+ effects across 190/296 EPs. Index in ~5 minutes.
  Has `rig.rules.json` with rules for: IMemoryCache, IDistributedCache, IMessageBus,
  ISignal, IFileStore/IMediaFileStore, OpenIddict managers, IDocumentManager,
  IShellSettingsManager, IDeploymentTargetHandler, IDisplayManager,
  YesSql IQuery terminators (FirstOrDefaultAsync/ListAsync), ExecuteQuery, SessionExtensions.GetAsync.
- `playgrounds/eShop/eShop.slnx` — dotnet/eShop multi-service e-commerce sample.
  41 entry points (Catalog.API MinAPI, Identity.API MVC, Ordering.API MinAPI, Webhooks.API MinAPI),
  56 effects: EF Core (CatalogContext, OrderingContext, WebhooksContext), Redis (StringGetLeaseAsync,
  StringSetAsync, KeyDeleteAsync), EventBus (RabbitMQ PublishAsync with argument_type resolution),
  Npgsql raw SQL, AI embeddings (GenerateAsync + GenerateVectorAsync extension method). Index in ~2 min.
  Note: Basket.API has no HTTP entry points (gRPC only); WebApp/HybridApp/WebAppComponents excluded
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
Already-visited nodes (cycles) print with `[^]` and are not expanded again.

`--focus` mode: backward BFS from effect nodes, keeps only effect-reachable ancestors.
Drops all BOUNDARY lines; CALL edges trimmed to reachable targets only.
Header shows `(focused)` and `Nodes: X / Y on effect paths`.

## Important Caveats

- Callgraph traversal uses single-impl DI dispatch (1 concrete registration → resolves to it).
  Multi-impl interfaces leave an empty interface node — add a rule for the interface to capture effects.
- Constructor injection resolution, cycles, and richer dynamic dispatch are not fully implemented.
- EF query precompilation conflict: `EFPrecompileQueriesStage=never` in `Rig.Storage.csproj`.
- `runs.AnalysisResultJson` column still exists in the schema (written as empty string, never read); can be dropped in a future migration.
- `string_argument` resource returns null for non-literal first arguments — use `receiver_type` instead.

## Recommended Next Slice

**MCP server** — wrap `Reads.cs` queries behind MCP tools.
Tools: `rig_effects`, `rig_entrypoints`, `rig_callgraph`, `rig_di`, `rig_files`.
Server starts once, opens `RigDbContext`, drops per-call cost to <5ms.
Unblocks agent-driven analysis (Copilot, Claude) without subprocess overhead.

Other candidates:
- **Cycle detection** — annotate back-edges in callgraph traversal and expose in CLI.
- **More OrchardCore rules** — query top boundary calls in the DB, add coverage for uncaught effects.
- **`unresolved_resource` observation** — flag effects where the resource string couldn't be resolved.

## Useful Files

- `docs/mvp-spec.md`
- `docs/ubiquitous-language.md`
- `docs/progress.md`
- `docs/sqlite-persistence-notes.md`
- `src/Rig/Rules/builtin-rules.json`
- `src/Rig/Analysis/SolutionAnalyzer.cs`
- `src/Rig.Domain/AnalysisResult.cs`
- `src/Rig/Analysis/CallGraph/CallGraphBuilder.cs`
- `src/Rig/Analysis/CallGraph/CallGraphIndexes.cs`
- `src/Rig/Analysis/Extraction/`
- `src/Rig/Analysis/Rules/RuleTypeMatcher.cs`
- `src/Rig/Analysis/Inventory/SolutionSourceLoader.cs`
- `src/Rig/Analysis/Rules/AnalysisRuleSet.cs`
- `src/Rig/Cli/CliApplication.cs`
- `src/Rig/Cli/Rendering/`
- `src/Rig.Storage/RigDbContext.cs`
- `src/Rig.Storage/Queries/Reads.cs`
- `src/Rig.Storage/Queries/Writes.cs`
- `playgrounds/EntryPointEffects/EntryPointEffects.slnx`
- `playgrounds/EntryPointEffects/rig.rules.json`
- `tests/Rig.Tests/Analysis/PlaygroundAnalysisTests.cs`
- `tests/Rig.Tests/Cli/CliApplicationTests.cs`

- `playgrounds/OrchardCore/OrchardCore.slnx`
- `playgrounds/OrchardCore/rig.rules.json`

Working commands:

```text
dotnet run --project src/Rig -- index playgrounds/EntryPointEffects/EntryPointEffects.slnx
dotnet run --project src/Rig -- runs
dotnet run --project src/Rig -- entrypoints
dotnet run --project src/Rig -- effects
dotnet run --project src/Rig -- files --skipped
dotnet run --project src/Rig -- callgraph "minapi GET /minapi/teams/{id}"
```

The playground at `playgrounds/EntryPointEffects` builds and includes:

- Minimal API entrypoints
- MVC controller entrypoints
- a config-driven class-inheritance entrypoint fixture matching a
  `FastEndpoints.Endpoint<TRequest,TResponse>`-style base type
- HttpClient effects
- EF Core read, pending write, and commit effects
- Redis read/write effects
- `foreach` looped effect observation
- `Task.WhenAll` parallel fanout observation

Roslyn-backed tests copy this playground to a fresh temp directory before
restore/analyze. Keep that pattern for integration coverage so MSBuild `obj/bin`
outputs and `.rig` databases do not contend on shared repo paths. The playground
has local `Directory.Packages.props` and `NuGet.config` files so it can restore
without depending on repo-root package management or user-level NuGet sources.

The analyzer currently emits:

- entrypoints
- effects
- effect observations
- source file inventory with indexed/skipped classifications
- DI registration facts
- method observations backed by Roslyn symbols
- invocation observations backed by Roslyn symbols
- shallow application callgraphs with inline effects, direct symbol edges,
  external boundaries, and unresolved boundaries

Persistence now writes immutable runs to `.rig/rig.db` through EF Core/SQLite.
The store keeps the full `AnalysisResult` projection as JSON for stable CLI
reads and also writes queryable tables for source files, entrypoints, effects,
effect observations, DI registrations, method observations, invocation
observations, callgraphs, callgraph nodes, node calls, and boundary calls.
Source file rows include status, confidence, basis, reason, and evidence.

Current built-in detection rules live in `src/Rig/Rules/builtin-rules.json`.
That file externalizes the implemented Minimal API entrypoint rules, MVC HTTP
attribute rules, generic class-inheritance entrypoint rules, HTTP/EF Core/Redis
effect rules, DI registration rules, and built-in file rules. The
class-inheritance rule path is generic C#: rules provide base types, route
provider methods, route-builder methods, handler method names, and whether the
handler must be an override. The built-in FastEndpoints rule is now just JSON
data. Effect rules can also carry declaring-type filters, which avoids
method-name-only matches such as treating `MimeMessage.To.Add` as an EF Core
pending write. Effect predicates compose with `AND`: every optional predicate
present on the rule must match; `OR` is represented as parallel rules with the
same output shape. `rig.rules.json` beside the solution can extend entrypoint,
effect, DI, and file rules; the playground currently uses it to exclude a
generated fixture.

`SolutionAnalyzer` is now orchestration only. The old 1,135-line file was split
into focused components:

- `Analysis/Inventory/SolutionSourceLoader.cs`
- `Analysis/Rules/AnalysisRuleSet.cs`
- `Analysis/Extraction/EntryPointExtractor.cs`
- `Analysis/Extraction/EffectExtractor.cs`
- `Analysis/Extraction/EffectObservationExtractor.cs`
- `Analysis/Extraction/DiRegistrationExtractor.cs`
- `Analysis/Extraction/RoslynObservationExtractor.cs`
- `Analysis/CallGraph/CallGraphBuilder.cs`
- `Analysis/CallGraph/CallGraphIndexes.cs`
- `Analysis/Rules/RuleTypeMatcher.cs`
- `Analysis/RoslynSymbolHelpers.cs`
- `Analysis/RoslynAnalysisModels.cs`

## Verification

Last verified with:

```text
dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false
dotnet build RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false -warnaserror
dotnet run --project src/Rig -- index playgrounds/EntryPointEffects/EntryPointEffects.slnx
dotnet run --project src/Rig -- runs
dotnet run --project src/Rig -- callgraph "minapi GET /minapi/teams/{id}"
```

Dead-code scan after the refactor found no stale moved helper methods or
duplicate private model types.

The `/p:UseSharedCompilation=false` flag avoids intermittent compiler-server
timeouts observed during this slice.

## Important Caveats

This is still a prototype, but solution loading and local call resolution now
use Roslyn/MSBuild workspace compilations and `SemanticModel` symbol lookup.

Current callgraph resolution is intentionally shallow:

- Minimal API inline lambdas are traversed directly from the endpoint handler.
- MVC actions are matched by file and start line, then traversed by symbol.
- Direct calls to in-solution methods resolve by Roslyn method symbols.
- External and unresolved calls are now explicit boundary calls on callgraph
  nodes.
- MS DI registration facts are emitted, but callgraph traversal does not yet use
  them to resolve interface/service calls.
- Constructor injection resolution, interfaces, cycles, and richer dynamic
  dispatch modeling are not fully implemented yet.

See `docs/sqlite-persistence-notes.md` for the current decision on not storing
the full AST as the primary index. Short version: persist compact
symbol/reference observations first; keep full AST dumps as possible future
diagnostic artifacts.

Profiles are not implemented yet. Current detection is rule-driven where
implemented, and solution-local `rig.rules.json` can extend those rule lists.

## Recommended Next Slice

Expand generic rule coverage, then use emitted MS DI registration facts for
constructor/interface callgraph resolution.

Suggested contract:

- support additional handler methods such as FastEndpoints `HandleAsync` through
  JSON only
- add optional type/namespace receiver filters for DI registration rules
- expose class-inheritance rule docs/examples for solution-local `rig.rules.json`
- map constructor-injected fields/properties back to DI facts
- resolve interface/service calls to implementation calls where DI facts are
  exact
- label DI-derived edges with confidence, basis, reason, and evidence
- preserve current CLI output

After that, add `rig di` or a general facts query surface so DI facts are
visible without inspecting JSON/SQLite.

## Useful Files

- `docs/mvp-spec.md`
- `docs/ubiquitous-language.md`
- `docs/progress.md`
- `docs/sqlite-persistence-notes.md`
- `src/Rig/Rules/builtin-rules.json`
- `src/Rig/Analysis/SolutionAnalyzer.cs`
- `src/Rig/Analysis/AnalysisResult.cs`
- `src/Rig/Analysis/CallGraph/CallGraphBuilder.cs`
- `src/Rig/Analysis/Extraction/`
- `src/Rig/Analysis/Inventory/SolutionSourceLoader.cs`
- `src/Rig/Analysis/Rules/AnalysisRuleSet.cs`
- `src/Rig/Cli/CliApplication.cs`
- `src/Rig/Cli/RunStore.cs`
- `src/Rig/Storage/RigDbContext.cs`
- `playgrounds/EntryPointEffects/EntryPointEffects.slnx`
- `playgrounds/EntryPointEffects/rig.rules.json`
- `tests/Rig.Tests/Analysis/PlaygroundAnalysisTests.cs`
- `tests/Rig.Tests/Cli/CliApplicationTests.cs`

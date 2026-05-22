# Handover

Current handover after read/write decoupling, new CLI commands, EF compiled model, and R2R performance work.

## Current State

The repo contains a CLI-first .NET 10 prototype.

Working commands:

```text
dotnet run --project src/Rig -- index playgrounds/EntryPointEffects/EntryPointEffects.slnx
dotnet run --project src/Rig -- runs
dotnet run --project src/Rig -- entrypoints
dotnet run --project src/Rig -- effects
dotnet run --project src/Rig -- di
dotnet run --project src/Rig -- files --skipped
dotnet run --project src/Rig -- callgraph "minapi GET /minapi/teams/{id}"
dotnet run --project src/Rig -- profile validate
```

Published R2R binary at `.rig-bin/Rig.exe` (gitignored). Build with:
```
dotnet publish src/Rig -c Release -r win-x64 /p:PublishReadyToRun=true -o .rig-bin
```
Timings: baseline 67ms | `rig effects` ~370ms | `rig callgraph` ~370ms (vs ~2800ms with `dotnet run`).

## Architecture

**Read/write decoupling**: `RunStore.SaveAsync` writes to ~12 normalized tables.
`LoadLatestAsync` reads from those tables and reconstructs `AnalysisResult` in memory —
the `runs.AnalysisResultJson` blob is still written but never read.

**`callgraph_node_effects` join table**: links per-node effects (`CallGraphNodeInfo.Effects`)
to the global `effects` table via `(RunId, GraphIndex, NodeIndex, LinkIndex) → EffectIndex`.

**EF compiled model**: auto-generated at build time by `Microsoft.EntityFrameworkCore.Tasks`.
`EFScaffoldModelStage=build` triggers regeneration whenever the model changes.
Generated files land in `Storage/Compiled/*.g.cs` (gitignored). No manual `dotnet ef` needed.
EF query precompilation is disabled (`EFPrecompileQueriesStage=never`) — conflicts with
MSBuild.Framework version pulled in by Roslyn; TODO in `Rig.csproj` tracks the fix.

**MCP opportunity**: 370ms per CLI call is fine for humans but too slow for agentic loops.
An MCP server would load DB once and serve tool calls in <5ms. `ModelContextProtocol`
NuGet package (Microsoft) is the SDK. Index (`rig index`) stays a separate CLI step.

## Playgrounds

- `playgrounds/EntryPointEffects/EntryPointEffects.slnx` — primary indexed target.
  5 entrypoints, 17 effects (after MediatR dispatch resolution).
- `playgrounds/CleanArchitecture/` — vendored public CleanArchitecture solution.
  5 FastEndpoints entrypoints, 24 effects including EF/repository/SMTP/MediatR.

## Verification

```text
dotnet test                    # 5 tests, all green
.\.rig-bin\Rig.exe effects     # ~370ms with R2R binary
```

## Important Caveats

- Callgraph traversal does not yet use DI facts to resolve interface/service calls.
- Constructor injection resolution, cycles, and richer dynamic dispatch are not fully implemented.
- EF query precompilation conflict: fix requires splitting Roslyn analysis into `Rig.Analysis.csproj`.
- Profiles not implemented. Detection is rule-driven; `rig.rules.json` beside the solution extends rules.

## Recommended Next Slice

**MCP server** — wrap `LoadLatestAsync` + `AnalysisResult` behind MCP tools.
Tools: `rig_effects`, `rig_entrypoints`, `rig_callgraph`, `rig_di`, `rig_files`.
Server starts once, holds DB state in memory, drops per-call cost to <5ms.
Unblocks agent-driven analysis (Copilot, Claude) without subprocess overhead.

After that: **cycle detection** — annotate back-edges in callgraph traversal and expose in CLI.

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
- `playgrounds/CleanArchitecture/Clean.Architecture.slnx`
- `tests/Rig.Tests/Analysis/PlaygroundAnalysisTests.cs`
- `tests/Rig.Tests/Analysis/CleanArchitecturePlaygroundTests.cs`
- `tests/Rig.Tests/Cli/CliApplicationTests.cs`


## Current State

The repo contains a CLI-first .NET 10 prototype.

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

The playground at `playgrounds/CleanArchitecture` vendors the public
CleanArchitecture target used for reconciliation. `CleanArchitecturePlaygroundTests`
restores it and asserts the currently expected 5 FastEndpoints entrypoints and
24 effects, including EF query/raw SQL/schema operations, repository effects,
SMTP effects, and Mediator/MediatR dispatch.

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
- `Analysis/Extraction/DiRegistrationExtractor.cs`
- `Analysis/Extraction/RoslynObservationExtractor.cs`
- `Analysis/CallGraph/CallGraphBuilder.cs`
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
dotnet run --project src/Rig -- index %TEMP%\coderig-targets\CleanArchitecture\Clean.Architecture.slnx
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
- `playgrounds/CleanArchitecture/Clean.Architecture.slnx`
- `tests/Rig.Tests/Analysis/PlaygroundAnalysisTests.cs`
- `tests/Rig.Tests/Analysis/CleanArchitecturePlaygroundTests.cs`
- `tests/Rig.Tests/Cli/CliApplicationTests.cs`

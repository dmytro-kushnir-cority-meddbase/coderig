# Handover

Current handover after refactoring `SolutionAnalyzer` into focused analysis
components.

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
- HttpClient effects
- EF Core read, pending write, and commit effects
- Redis read/write effects
- `foreach` looped effect observation
- `Task.WhenAll` parallel fanout observation

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
attribute rules, HTTP/EF Core/Redis effect rules, DI registration rules, and
built-in file rules. `rig.rules.json` beside the solution currently extends
file include/exclude globs; the playground uses it to exclude a generated
fixture.

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

Profiles are not implemented yet. Current effect detection is hard-coded.

## Recommended Next Slice

Use the emitted MS DI registration facts for constructor/interface callgraph
resolution.

Suggested contract:

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

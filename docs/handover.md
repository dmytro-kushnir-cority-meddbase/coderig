# Handover

Current handover after the shallow callgraph slice.

## Current State

The repo contains a CLI-first .NET 10 prototype.

Working commands:

```text
dotnet run --project src/Rig -- index playgrounds/EntryPointEffects/EntryPointEffects.slnx
dotnet run --project src/Rig -- entrypoints
dotnet run --project src/Rig -- effects
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
- shallow application callgraphs with inline effects

## Verification

Last verified with:

```text
dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false
dotnet build playgrounds/EntryPointEffects/EntryPointEffects.slnx /p:UseSharedCompilation=false
dotnet run --project src/Rig -- index playgrounds/EntryPointEffects/EntryPointEffects.slnx
dotnet run --project src/Rig -- callgraph "minapi GET /minapi/teams/{id}"
```

The `/p:UseSharedCompilation=false` flag avoids intermittent compiler-server
timeouts observed during this slice.

## Important Caveats

This is still a syntax-based prototype.

Current callgraph resolution is intentionally shallow:

- Minimal API lambda parameters are mapped by syntax.
- MVC actions are matched by file and start line.
- Method calls resolve through visible parameter and field type names.
- Constructor injection, MS DI registration facts, interfaces, dynamic dispatch,
  external boundary nodes, cycles, and unresolved call nodes are not fully
  modeled yet.

Persistence is still `.rig/latest.json`, not SQLite immutable runs.

Profiles are not implemented yet. Current effect detection is hard-coded.

## Recommended Next Slice

Move from syntax-only callgraph to Roslyn-backed symbols for the same playground
contract.

Suggested contract:

- load `.slnx` through Roslyn/MSBuild workspace
- fail loudly on compilation errors
- emit method/invocation observations
- resolve `TeamWorkflow` and `BillingClient` calls by symbol, not string names
- preserve current CLI output

Keep the current syntax analyzer as a temporary fallback only if useful. The
product spec wants ground truth from Roslyn AST/symbol observations, so this is
the next architectural pressure point.

## Useful Files

- `docs/mvp-spec.md`
- `docs/ubiquitous-language.md`
- `docs/progress.md`
- `src/Rig/Analysis/SolutionAnalyzer.cs`
- `src/Rig/Analysis/AnalysisResult.cs`
- `src/Rig/Cli/CliApplication.cs`
- `playgrounds/EntryPointEffects/EntryPointEffects.slnx`
- `tests/Rig.Tests/Analysis/PlaygroundAnalysisTests.cs`
- `tests/Rig.Tests/Cli/CliApplicationTests.cs`

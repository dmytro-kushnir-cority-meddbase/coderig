---
name: codegraph
description: Work on the codegraph Runtime Intelligence Graph repo. Use when modifying this repository's .NET 10 CLI, Roslyn/MSBuild analysis, declarative rules, SQLite storage, playground fixtures, tests, docs/progress, or handover notes.
---

# Codegraph

## Core Direction

Keep this repo CLI-first. The product is `rig`, a .NET 10 command-line analyzer
that indexes .NET solutions into immutable SQLite runs and reports entrypoints,
effects, DI facts, source inventory, and bounded callgraphs.

Do not drift into MCP servers, dashboards, or alternate projections unless the
user explicitly asks. Human-readable output is the development default; add JSON
or terse modes only as separate projections.

Preserve the architecture split:

- Roslyn AST and config parser output are ground truth.
- Callgraphs, effects, observations, and stored rows are derived state.
- Detection should be rule-first and composable. Avoid bespoke detector creep
  when a declarative rule or small shared primitive will work.

## Repo Map

- `src/Rig` - CLI executable, Roslyn/MSBuild loading, extraction, callgraph logic.
- `src/Rig.Domain` - dependency-free records such as `AnalysisResult`, `EffectInfo`, and `CallGraphInfo`.
- `src/Rig.Storage` - EF Core/SQLite context, entities, focused read queries, writes.
- `tests/Rig.Tests` - fast unit tests plus a small number of Roslyn integration smoke tests.
- `playgrounds/EntryPointEffects` - owned, fast playground fixture with local package metadata.
- `playgrounds/OrchardCore/rig.rules.json` and `playgrounds/eShop/rig.rules.json` - large external playground rules only; source trees are intentionally ignored.
- `docs/progress.md` and `docs/handover.md` - living coordination artifacts.

## Commands

Use explicit solution paths. The VS Code setting disables default solution
selection; do not rely on editor or extension auto-discovery.

The packed app may be installed as a global .NET tool and accessible as `rig`.
When it is available, use it for quick manual checks:

```powershell
rig index playgrounds/EntryPointEffects/EntryPointEffects.slnx
rig entrypoints
rig effects
rig callgraph 6 --full
rig files --skipped
rig profile validate
```

When validating source changes, prefer build/test commands and `dotnet run`
from the working tree so the current checkout is exercised:

```powershell
dotnet test RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false
dotnet build RuntimeIntelligenceGraph.slnx /p:UseSharedCompilation=false
dotnet run --project src/Rig -- index playgrounds/EntryPointEffects/EntryPointEffects.slnx
dotnet run --project src/Rig -- entrypoints
dotnet run --project src/Rig -- effects
dotnet run --project src/Rig -- callgraph 6 --full
dotnet run --project src/Rig -- files --skipped
dotnet run --project src/Rig -- profile validate
```

Use `/p:UseSharedCompilation=false` for build/test verification in this repo;
it avoids intermittent compiler-server and Roslyn test hangs seen locally.

## Testing Policy

Prefer many small focused tests for pure behavior:

- rule matching and glob matching
- source-file classification
- profile validation and rule merging
- CLI argument errors
- renderers and projections

Keep Roslyn/MSBuild integration tests few and explicit. They should copy the
owned `EntryPointEffects` playground to a fresh temp directory, restore there,
and avoid sharing `obj`, `bin`, `.rig`, or SQLite files between tests.

Do not use vendored public repos such as CleanArchitecture as unit or integration
fixtures. Large real-world repos can be used manually or through ignored local
playgrounds, with only their `rig.rules.json` files tracked.

Current expected suite shape is small fast tests plus integration smoke tests,
not one giant assertion file.

## Rule Model

Effect rules compose with AND across present predicates. OR is represented by
parallel rules with the same output shape. Preserve overlap when it is useful
evidence.

Common rule fields:

- `methods` - method names to match.
- `declaringTypes` - method declaring type filter.
- `receiverTypes` - receiver type/interface/base type filter.
- `containingNamespaces`, `containingTypes`, `containingMethods` - contextual filters.
- `resource` - resolver strategy such as `http_argument`, `string_argument`,
  `ef_dbset_receiver`, `ef_query_root`, `ef_context_receiver`,
  `ef_database_facade`, `receiver_type`, or `argument_type`.
- `treatAsDispatch` - dispatch traversal rule, not an emitted effect.

When adding a provider or framework, start with targeted JSON rules and shared
matcher/resource primitives. Add custom extractor logic only when the rule model
cannot represent the signal.

## CLI Behavior

Default callgraph mode is focused: only effect-reachable paths are printed, and
BOUNDARY lines are hidden. Use `--full` when tests or debugging need the complete
tree, including external and unresolved boundaries. Use `--summary` for a flat
effect inventory.

Keep output ASCII-terse and stable unless the user requests a presentation
change. Existing tests often encode CLI text as a contract.

## Storage

Storage uses normalized EF Core/SQLite tables plus focused read queries. Preserve
the pattern where CLI read commands load only the data they need. Avoid reviving
full JSON blob reads for command paths.

`src/Rig.Storage` should not gain Roslyn dependencies.

## Docs And Handover

For coherent slices, update `docs/progress.md`. Update `docs/handover.md` when
the next agent needs new caveats, commands, architecture boundaries, or fixture
policy. Keep these docs short and factual.

## Git Hygiene

The worktree may contain unrelated local files such as `.vscode/` or helper
scripts. Do not stage or revert them unless the user explicitly asks. Before
committing, inspect `git status --short` and stage only the intended slice.

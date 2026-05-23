# Runtime Intelligence Graph — Copilot Instructions

This workspace is a CLI-first .NET 10 tool (`rig`) that indexes .NET solutions into
SQLite runs and exposes entry-point call graphs annotated with external effects
(DB queries, cache reads, file writes, etc.).

---

## Project Layout

```
src/Rig/              CLI exe — Roslyn analysis, entry-point extraction, callgraph builder
src/Rig.Domain/       Pure C# domain records (no dependencies)
src/Rig.Storage/      EF Core + SQLite — RigDbContext, Reads.cs, Writes.cs
tests/Rig.Tests/      Contract tests for playground fixtures
playgrounds/
  EntryPointEffects/  Primary dev playground (small, fast to index)
  OrchardCore/        Large real-world codebase (heavy, ~5 min to index)
docs/                 handover.md, mvp-spec.md, progress.md, ubiquitous-language.md
```

---

## Build

```powershell
dotnet publish src/Rig/Rig.csproj -c Release -r win-x64 --self-contained -o .rig-bin `
  -p:PublishReadyToRun=true -p:DebugSymbols=false -p:DebugType=none `
  /p:TreatWarningsAsErrors=false
```

Binary lands at `.rig-bin/Rig.exe` (gitignored). R2R win-x64, ~295–350 ms per command.

---

## CLI Commands

### `rig index <solution>`

Index a solution into a new immutable run stored in `.rig/rig.db`.

```powershell
.\.rig-bin\Rig.exe index playgrounds/EntryPointEffects/EntryPointEffects.slnx
.\.rig-bin\Rig.exe index playgrounds/OrchardCore/OrchardCore.slnx
```

Output: `Indexed: <path>`, `Run: <runId>`, `EntryPoints: N`, `Effects: N`

Accepts both `.slnx` (VS 2022 new format) and `.sln`.

### `rig runs`

List all indexed runs in chronological order.

### `rig entrypoints`

List all entry points in the latest run.

```
[  0] mvc GET api/teams/{id}  TeamsController.cs:20
[  4] mvc GET api/teams/via-method-group  TeamsController.cs:53
[  6] minapi GET /minapi/teams/{id}  Program.cs:19
```

Entry point kinds: `mvc`, `minapi`, `fastendpoint`

### `rig effects [--entrypoint <index>]`

List all detected effects, optionally filtered to a single entry point.

```
efcore read  ToListAsync  AppDbContext.Teams  TeamRepository.cs:16
yessql write  SaveAsync  YesSql.ISession  DefaultContentManager.cs:646
filestore write  CreateFileFromStreamAsync  OrchardCore.Media.IMediaFileStore  ...
memory_cache read  TryGetValue  Microsoft.Extensions.Caching.Memory.IMemoryCache  ...
```

Append `[looped_effect:foreach]` or `[looped_effect:parallel]` when the effect site
sits inside a loop or Parallel.ForEach.

### `rig callgraph <index> [--focus]`

Print the call graph for entry point `<index>`.

**Default** (verbose): all nodes, all CALL/BOUNDARY/EFFECT lines.
**`--focus`**: only nodes on a path to an EFFECT; all BOUNDARY lines dropped;
CALL edges trimmed to effect-reachable targets only.

```powershell
.\.rig-bin\Rig.exe callgraph 4
.\.rig-bin\Rig.exe callgraph 295 --focus
```

Output format:

```
Callgraph: [4] mvc GET api/teams/via-method-group (focused)
Nodes: 2 / 8 on effect paths
  TeamsController.cs:53  mvc GET api/teams/via-method-group
    CALL TeamRepository.GetAllAsync
  TeamRepository.cs:15  TeamRepository.GetAllAsync
    EFFECT efcore read  ToListAsync  AppDbContext.Teams
```

Line format per node:

- `CALL <Symbol>` — resolved application-internal call (will appear as its own node)
- `BOUNDARY external <Method>` — call to an external/framework symbol (not traversed)
- `EFFECT <provider> <operation>  <method>  <resource>  [observations]`

### `rig di`

List all MS DI registrations found in the solution.

### `rig files --skipped`

List files excluded from analysis and the rule that excluded them.

### `rig profile validate`

Validate the `rig.rules.json` profile for the current directory.

---

## Effect Rules (`rig.rules.json`)

Placed next to the `.slnx`/`.sln` file (solution-level) or in a project directory.
Cascades: built-in → `~/.rig/rig.rules.json` → solution → per-project.

### Rule schema

```json
{
  "effects": [
    {
      "provider": "efcore",
      "operation": "read",
      "methods": ["ToListAsync", "FirstOrDefaultAsync"],
      "receiverTypes": ["Microsoft.EntityFrameworkCore.DbSet"],
      "declaringTypes": [],
      "resource": "ef_dbset_receiver",
      "confidence": "high",
      "basis": "compilation+profile",
      "reason": "efcore_read_method"
    }
  ]
}
```

**`receiverTypes`** — match against the type of `this` (the receiver). Uses substring/contains matching and walks `AllInterfaces`, so an interface rule fires for any type that implements it.

**`declaringTypes`** — match against the type that declares the method (for static/extension methods).

**`resource` values**:

| Value                 | Resolved as                                                                     |
| --------------------- | ------------------------------------------------------------------------------- |
| `ef_dbset_receiver`   | `DbContext.DbSetName`                                                           |
| `ef_context_receiver` | The DbContext type name                                                         |
| `ef_query_root`       | Root entity type of the query                                                   |
| `http_argument`       | First argument (URL string)                                                     |
| `string_argument`     | First argument — **only works for string literals**, returns null for variables |
| `argument_type`       | Type of the first argument                                                      |
| `receiver_type`       | Fully-qualified type of the receiver                                            |

Use `receiver_type` when arguments are objects or variables (most OrchardCore-style interfaces).
Use `string_argument` only when the first argument is always a literal (e.g. HTTP client base URLs).

### File/project exclusions

```json
{
  "files": {
    "exclude": [
      {
        "id": "my.migrations",
        "glob": "**/Migrations/**",
        "reason": "db_migration"
      }
    ]
  },
  "projects": {
    "exclude": ["*.Tests", "*.IntegrationTests"]
  }
}
```

---

## Key Analysis Concepts

### Entry-point kinds

| Kind           | Detection                                                     |
| -------------- | ------------------------------------------------------------- |
| `mvc`          | Controller methods with `[HttpGet]` / `[HttpPost]` / etc.     |
| `minapi`       | `app.MapGet(...)`, `app.MapPost(...)`, etc.                   |
| `fastendpoint` | Classes extending `Endpoint<TReq>` / `EndpointWithoutRequest` |

### Call graph traversal

- **Application calls** (`CALL`): resolved via Roslyn `GetSymbolInfo`. Traversed recursively.
- **Single-impl dispatch**: when a call is to an interface method and exactly one concrete implementation is registered in DI, the call is resolved to that implementation.
- **Method group resolution**: references like `Select(repo.GetAsync)` or `fn = repo.GetAsync` — not `InvocationExpressionSyntax` — are resolved by scanning `DescendantNodes()` for `IdentifierNameSyntax`/`MemberAccessExpressionSyntax` that resolve to `IMethodSymbol`.
- **Lambda bodies**: traversed automatically because `DescendantNodes()` is recursive across lambda boundaries. No special handling needed.
- **Boundary calls** (`BOUNDARY`): calls to external/framework symbols that are not traversed further. Annotated with `external`.

### `--focus` algorithm

Backward BFS from all nodes that have at least one EFFECT, propagating through reverse
call edges to collect all ancestor nodes. Nodes outside this set are dropped. BOUNDARY
lines are never shown in focus mode. CALL edges to non-reachable nodes are dropped.

### Observations on effects

- `[looped_effect:foreach]` — effect is inside a `foreach` loop
- `[looped_effect:parallel]` — effect is inside `Parallel.ForEach` / `Parallel.ForEachAsync`

---

## Playground: EntryPointEffects

Fast iteration target. 8 entry points, ~23 effects. Index in ~10s.

```
[0] mvc GET api/teams/{id}            — simple workflow call
[1] mvc POST api/teams                — EF write via workflow
[2] mvc GET api/teams/via-interface   — single-impl dispatch → EF read
[3] mvc POST api/teams/via-interface  — single-impl dispatch → EF write
[4] mvc GET api/teams/via-method-group — method group delegate → EF read
[5] fastendpoint POST /fastendpoints/teams
[6] minapi GET /minapi/teams/{id}
[7] minapi POST /minapi/teams
```

## Playground: OrchardCore

Large real-world CMS. 296 entry points, 788 effects across 188 EPs.
Index takes ~5 minutes. Has a `rig.rules.json` with rules for:
`IMemoryCache`, `IDistributedCache`, `IMessageBus`, `ISignal`,
`IFileStore`/`IMediaFileStore`, OpenIddict managers,
`IDocumentManager`, `IShellSettingsManager`, `IDeploymentTargetHandler`, `IDisplayManager`.

---

## Common Workflows

### Investigate an effectless entry point

```powershell
.\.rig-bin\Rig.exe callgraph <index>   # check node count
# 1-node graph → handler not traversed (min API method-ref bug)
# multi-node, no EFFECT lines → add rules or check interface resolution
```

### Add a new effect rule and re-index

1. Edit `playgrounds/<target>/rig.rules.json`
2. `.\.rig-bin\Rig.exe index <solution>`
3. Check effect count in output; query DB for coverage delta

### Check coverage

```powershell
$run = "<runId>"
sqlite3 .rig/rig.db "SELECT COUNT(DISTINCT GraphIndex) FROM callgraph_node_effects WHERE RunId='$run'"
```

### Find top EPs by effect count

```powershell
$run = "<runId>"
sqlite3 .rig/rig.db "SELECT e.EntryPointIndex, e.DisplayName, COUNT(ef.EffectIndex) as fx FROM entrypoints e JOIN callgraph_node_effects ef ON ef.RunId=e.RunId AND ef.GraphIndex=e.EntryPointIndex WHERE e.RunId='$run' GROUP BY e.EntryPointIndex ORDER BY fx DESC LIMIT 15"
```

---

## Known Limitations

- Min API method-reference handlers (e.g. `app.MapPost("/x", HandleAsync)`) produce
  1-node call graphs — the handler body is not traversed. ~10 EPs affected in OrchardCore.
- `string_argument` resource returns null for non-literal first arguments (use `receiver_type` instead).
- EF query precompilation conflict: `EFPrecompileQueriesStage=never` in `Rig.Storage.csproj`.
- Multi-implementation dispatch (interfaces with >1 concrete impl) leaves an empty interface
  node in the call graph — add a rule for the interface to get effects from that path.

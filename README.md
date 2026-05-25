# Runtime Intelligence Graph (`rig`)

A CLI-first .NET 10 static analysis tool that indexes .NET solutions into immutable
SQLite runs and exposes per-entry-point call graphs annotated with external effects
(EF Core reads/writes/commits, Redis, HTTP, file I/O, message bus, etc.) and
execution-context observations (loops, parallel fanout, resilience retries,
concurrency hazards).

The current product direction is captured in [docs/mvp-spec.md](docs/mvp-spec.md).
Shared project vocabulary lives in
[docs/ubiquitous-language.md](docs/ubiquitous-language.md).
Current implementation handover notes live in
[docs/handover.md](docs/handover.md).

## Quick Start

```powershell
# build
dotnet publish src/Rig/Rig.csproj -c Release -r win-x64 --self-contained -o .rig-bin `
  -p:PublishReadyToRun=true -p:DebugSymbols=false -p:DebugType=none `
  /p:TreatWarningsAsErrors=false

# index a solution
.\.rig-bin\Rig.exe index playgrounds/EntryPointEffects/EntryPointEffects.slnx

# explore
.\.rig-bin\Rig.exe entrypoints
.\.rig-bin\Rig.exe effects
.\.rig-bin\Rig.exe callgraph 0 --focus
```

## CLI Commands

| Command | Description |
|---|---|
| `rig index <solution>` | Index a `.sln` / `.slnx` into a new immutable run in `.rig/rig.db` |
| `rig runs` | List all runs in chronological order |
| `rig entrypoints` | List all entry points in the latest run |
| `rig effects [--entrypoint N]` | List all effects, optionally filtered to one entry point |
| `rig callgraph <N> [--focus]` | Print the call graph; `--focus` trims to effect-reachable nodes only |
| `rig di` | List MS DI registrations found in the solution |
| `rig files --skipped` | List files excluded from analysis and the rule that excluded them |
| `rig profile validate` | Validate the `rig.rules.json` profile for the current directory |

Entry-point kinds detected: `mvc` (controller actions), `minapi` (`app.Map*`), `fastendpoint`.

## Effect Observations

Observations are appended to effect lines in brackets when a structural pattern
is detected around the effect site:

| Observation | Trigger |
|---|---|
| `[looped_effect:foreach]` | Effect inside a `foreach` loop |
| `[looped_effect:parallel]` | Effect inside `Parallel.ForEach` / `Parallel.ForEachAsync` |
| `[parallel_fanout:Task.WhenAll]` | Effect inside a `Task.WhenAll` call |
| `[resilience_retry:ExecutionStrategy]` | Effect inside an EF Core resilience `ExecuteAsync` |
| `[resilience_retry:ResiliencePipeline]` | Effect inside a Polly `ResiliencePipeline.ExecuteAsync` |
| `[read_before_commit:before_commit]` | `SaveChangesAsync` preceded by an EF read in the same method — potential lost-update / TOCTOU site |
| `[concurrency_handled:DbUpdateConcurrencyException]` | `SaveChangesAsync` inside a `catch(DbUpdateConcurrencyException)` — optimistic concurrency IS handled |

## Playgrounds

| Playground | Entry points | Effects | Index time |
|---|---|---|---|
| `EntryPointEffects` | 8 | ~23 | ~10 s |
| `eShop` | 41 | 100 | ~30 s |
| `OrchardCore` | 296 | 788 | ~5 min |

## Example: eShop

The `eShop` playground is a real-world microservices reference app (41 entry points, 100 effects).

### Focused call graph — `PUT /items`

```
$ rig callgraph 12 --focus

Callgraph: [12] minapi PUT /items (focused)
Nodes: 8 / 18 on effect paths
  CatalogApi.cs:93  minapi PUT /items
  └─ CatalogApi.cs:324  CatalogApi.UpdateItem
     ├─ CatalogAI.cs:28  CatalogAI.GetEmbeddingAsync
     │  └─ EFFECT ai_embeddings read  GenerateVectorAsync  IEmbeddingGenerator
     ├─ CatalogIntegrationEventService.cs:28  ...SaveEventAndCatalogContextChangesAsync
     │  ├─ ResilientTransaction.cs:11  ResilientTransaction.ExecuteAsync
     │  │  ├─ EFFECT db_transaction begin  BeginTransactionAsync  DbContext.Database  [resilience_retry:ExecutionStrategy]
     │  │  └─ EFFECT db_transaction commit  CommitAsync  IDbContextTransaction  [resilience_retry:ExecutionStrategy]
     │  └─ EFFECT efcore commit  SaveChangesAsync  CatalogContext
     ├─ CatalogIntegrationEventService.cs:11  ...PublishThroughEventBusAsync
     │  └─ EFFECT eventbus publish  PublishAsync  eShop.EventBus.Events.IntegrationEvent
     ├─ EFFECT efcore read  SingleOrDefaultAsync  CatalogContext.CatalogItems
     └─ EFFECT efcore commit  SaveChangesAsync  CatalogContext  [read_before_commit:before_commit]
```

The `[read_before_commit:before_commit]` flag on the second `SaveChangesAsync` signals that the
item is read with `SingleOrDefaultAsync` earlier in the same method and then updated without any
concurrency token — a potential lost-update site.

### Flat effects summary — `PUT /items`

```
$ rig effects --entrypoint 12

  efcore           read            CatalogContext.CatalogItems
  efcore           commit          CatalogContext  [x2]  [read_before_commit:before_commit]
  ai_embeddings    read            IEmbeddingGenerator<TInput, TEmbedding>
  db_transaction   begin           DbContext.Database  [resilience_retry:ExecutionStrategy]
  db_transaction   commit          IDbContextTransaction  [resilience_retry:ExecutionStrategy]
  eventbus         publish         eShop.EventBus.Events.IntegrationEvent
```

### Sample `rig.rules.json`

Place next to the `.slnx`/`.sln` file to teach `rig` about framework-specific effects:

```json
{
  "effects": [
    {
      "provider": "redis",
      "operation": "read",
      "methods": ["StringGetLeaseAsync"],
      "receiverTypes": ["StackExchange.Redis.IDatabase"],
      "resource": "receiver_type",
      "confidence": "high",
      "basis": "compilation+profile",
      "reason": "redis_lease_read"
    },
    {
      "provider": "redis",
      "operation": "write",
      "methods": ["StringSetAsync", "KeyDeleteAsync"],
      "receiverTypes": ["StackExchange.Redis.IDatabase"],
      "resource": "receiver_type",
      "confidence": "high",
      "basis": "compilation+profile",
      "reason": "redis_write"
    },
    {
      "provider": "eventbus",
      "operation": "publish",
      "methods": ["PublishAsync"],
      "receiverTypes": ["eShop.EventBus.Abstractions.IEventBus"],
      "resource": "argument_type",
      "confidence": "high",
      "basis": "compilation+profile",
      "reason": "rabbitmq_publish"
    }
  ],
  "files": {
    "exclude": [
      { "id": "migrations", "glob": "**/Migrations/**", "reason": "db_migration" }
    ]
  }
}
```

**`resource` values** control what appears in the effect's resource column:

| Value | Resolved as |
|---|---|
| `ef_dbset_receiver` | `DbContext.DbSetName` |
| `ef_context_receiver` | The `DbContext` type name |
| `argument_type` | Type of the first argument |
| `receiver_type` | Fully-qualified type of the receiver |
| `http_argument` | First argument (URL string) |
| `string_argument` | First argument when it is a string literal |

`receiverTypes` uses substring matching and walks `AllInterfaces`, so an interface rule fires for any implementing type. `declaringTypes` matches the type that declares the method (for static/extension methods).

---

## Implementation Workflow

Default to contract-first TDD.

For each behavior slice:

1. Add or extend a playground fixture.
2. Hand-author the expected semantic output.
3. Run the test and see it fail.
4. Implement the smallest useful miner/rule/projection change.
5. Make the test green.
6. Do an explicit refactor pass.
7. Re-run relevant tests.
8. Commit the slice.

Tests should protect semantic contracts, not implementation details. Prefer
expected observations, facts, effects, callgraph edges, and CLI output over
tests that mirror internal algorithms.

Do not generate expected results from the same code path being tested. Expected
fixtures should be hand-authored and normalized for unstable values such as
absolute paths, timestamps, run IDs, generated IDs, and line endings.

Use short spikes for unfamiliar Roslyn/MSBuild behavior, but either delete spike
code or turn the learning into a failing fixture test before productizing it.

## Rule-First Extraction

Prefer simple targeted rules and composition over bespoke detector code.

The scalable path is to express framework knowledge as data whenever the shape
can be described with existing primitives: type/namespace filters, inheritance
filters, invocation filters, attributes, route-builder calls, declaring types,
receiver types, file/project filters, and small composed predicates.

Custom C# extraction logic is acceptable only when the pattern cannot be
expressed cleanly by extending the rule model. In that case, first ask whether a
small reusable matcher primitive would make the rule declarative. Avoid
framework-specific one-off walkers; they are quick locally but do not scale
across packs, local conventions, or user profiles.

Rule predicates compose with `AND`: every optional predicate present on a rule
must match before the rule emits. Leave a predicate absent to avoid constraining
that dimension. Express `OR` as parallel rules with the same output shape; if
multiple rules fire for the same code location, keep that overlap visible as
evidence rather than hiding it inside detector code.

## Progress Tracking

Track progress in three layers:

1. Milestone checklist in [docs/mvp-spec.md](docs/mvp-spec.md).
2. Slice checklist in [docs/progress.md](docs/progress.md) or an issue tracker.
3. Git commits that each correspond to one green tested behavior slice.

Recommended slice template:

```text
Slice: HttpClient absolute URL effect
Phase: 4
Status: red | green | refactor | verified | committed

Contract:
  - playground code contains HttpClient.GetAsync("https://billing.test/invoices")
  - expected effect is http GET billing.test /invoices
  - confidence=high basis=compilation+profile

Verification:
  - test name or command
  - commit hash when done
```

Keep the current slice small enough that a fresh-context agent can understand
the failing contract, make it green, refactor, and commit without rediscovering
the whole project.

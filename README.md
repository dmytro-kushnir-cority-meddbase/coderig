# Runtime Intelligence Graph (`rig`)

A CLI-first .NET 10 static analysis tool that indexes .NET solutions into immutable
SQLite runs and exposes per-entry-point call graphs annotated with external effects
(EF Core reads/writes/commits, Redis, HTTP, file I/O, message bus, etc.) and
execution-context observations (loops, parallel fanout, resilience retries,
concurrency hazards).

## Quick Start

> **Source builds temporarily require the pinned `UseNamedArgs` submodule.** Clone with
> `git clone --recurse-submodules ...`, or run `git submodule update --init` in an existing checkout.
> Remove this note and return to the published package after
> [`mykolav/use-named-args-fs#1`](https://github.com/mykolav/use-named-args-fs/pull/1) is merged and released.

```powershell
# index a solution (pre-build it first so cross-assembly metadata binds)
rig index playgrounds/EntryPointEffects/EntryPointEffects.slnx

# build the derived call-graph + FTS views (idempotent; speeds up reaches/callers/tree/dead)
rig graph

# explore (run from the directory that holds .rig/)
rig derive                              # effects + entry points inventory
rig reaches "Type.Method"               # effects reachable from an entry point
rig tree    "Type.Method" --view effects # compact call tree: only effectful methods
rig callers "Type.Method" --entrypoints # reverse: which entry points reach this
rig path    "From.Method" "To.Method"   # one concrete path between two symbols
```

## Formatting

```powershell
.\scripts\format.ps1
```

### Tests + local piblish as tool

```powershell
.\scripts\mini-ci.ps1
```

## CLI Commands

All commands except `index`/`mine` are read-only and run from the directory that holds `.rig/`.
`--rules <path>` (repeatable) cascades extra rule files over the builtin set.

| Command | Description |
|---|---|
| `rig index <solution\|project> [--rules <p>...] [--from <entry.csproj>] [--framework <tfm>] [--parallelism <n>]` | Index a `.sln`/`.slnx`/`.csproj` into `.rig/rig.db`. `--from` indexes only the entry project's non-test closure; `--framework` selects one TFM from multi-targeted projects |
| `rig mine <solution> --from <project.csproj> [--rules <p>...] [--parallelism <n>]` | BFS the project dependency graph from an entry project, indexing each project |
| `rig graph` | Rebuild the derived call-graph views (`call_edges` + `dispatch_edges` + `nodes` + FTS) from facts — idempotent, no rescan |
| `rig runs` | List indexed runs (entry-point / effect / symbol counts) |
| `rig derive [--rules <p>...] [--only <p,..>] [--exclude <p,..>]` | Stage-2 pass over facts: re-derive effects + entry points + classified handoffs. `--exclude throw` drops exceptions |
| `rig reaches <pat> [--async] [--only/--exclude <p,..>] [--maxdepth <n>] [--format tsv]` | Effects reachable from an entry point (synchronous by default; `--async` also walks handoffs into a separate ⚡ bucket) |
| `rig tree <pat> [--view paths\|full\|effects\|summary\|hazards] [--async] [--only/--exclude <p,..>] [--raw] [--maxdepth <n>] [--format llm\|llm-ids\|tsv]` | Call tree from an entry point. Default (`--view paths`) prunes to effect-reaching paths; `--view effects` lists only effectful methods; `--view hazards` marks pattern hazards (race_window/dual_write/…) inline + a summary section; `--raw` bypasses render rules; `--format llm` emits a compact 6/7-column TSV; `--format llm-ids` adds explicit surrogate-id linkage (8-column TSV with `id`/`parent_id`) |
| `rig callers <pat> [--roots\|--entrypoints] [--async] [--raw] [--maxdepth <n>]` | Reverse reachability: who reaches this method. `--entrypoints` = the rule-detected entry points; `--roots` = no-predecessor candidates |
| `rig path <fromPat> <toPat> [--async] [--raw] [--maxdepth <n>]` | One concrete path between two symbols |
| `rig dead [--lib] [--include-dispatch] [--all] [--root <pat>...] [--format tsv]` | Unreachable first-party methods (report-only — compiler-confirm before removing) |
| `rig symbols <pat> [--kind <k>] [--limit <n>]` | Substring symbol search (FTS-accelerated) |
| `rig refs <pat> [--first-party] [--kind <refkind>] [--limit <n>]` | References to a symbol |
| `rig di` | List MS DI registrations (service → implementation, lifetime, source) |
| `rig files --skipped` | List files excluded from analysis and the rule that excluded them |
| `rig profile validate` | Validate the `rig.rules.json` profile for the current directory |

Patterns are case-insensitive substring matches over DocIDs (`M:Ns.Type.Method(args)`). Entry-point and
effect detectors are **rule data** (`rig.rules.json`), not baked-in code, so the detected kinds depend on
the active profile — common kinds include `mvc`/`minapi`/`page`/`action` plus classified async-handoff
origins (`background`/`timer`/`actor`/`event`).

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

## Deployment Attribution (`deployments.json`)

Optional and opt-in. Drop a `deployments.json` next to `.rig/` and every command that renders an entry
point annotates it with the deployed **service(s)** whose process runs it — a ▶ marker, the EP kind, the
route, and a deployment chip. Without the file, output is unchanged.

```jsonc
{
  "services": [
    { "name": "MedDBase", "host": "src/main/MedDBase.Site/MedDBase/MedDBase.csproj", "kind": "iis", "provides": ["FrontEnd"] },
    { "name": "DataServer", "host": "src/data-server/MedDBase.DataServer/MedDBase.DataServer.csproj", "kind": "iis", "provides": ["DataServer"] },
    { "name": "PdfService2", "host": "src/pdf2/PdfService2/PdfService2.csproj", "kind": "kube" }
  ]
}
```

- `host` is the entry csproj, relative to the indexed solution's directory. JSONC (comments + trailing
  commas allowed). Seed it from the build's own artifact manifest, then curate `kind`/topology by hand.
- **Mechanism** (query-side, no re-index): each service's entry csproj → transitive `<ProjectReference>`
  closure; an EP's source file → its owning csproj → the service(s) whose closure contains it.
- **loaded-in vs active-in** — closure membership is "code is *loaded* in service X", an upper bound:
  shared libraries fan out to every referencing host, so a background/actor EP is *linked* into many
  services even when only one *runs* it. A **capability gate** refines this to **active-in**. A service
  declares the opaque tokens it `provides`; an EP rule declares the tokens it `requires` (on
  `handoffDispatchers` and entry-point rules — see `rig.rules.json`). An EP is **active-in** a loaded
  service iff `provides ∩ requires ≠ ∅` (ANY semantics). A rule with no `requires` is ungated — active
  wherever loaded — so the gate is strictly opt-in and ungated output is byte-identical to before.
- **Rendering**: chips show the **active-in** service(s) plus the linked-but-inactive count as a dim
  delta, e.g. `▶ echoactor SomeActor.Inbox  ⟦MedDBase (iis) · 1 linked-inactive⟧`, or `⟦N svcs: …⟧` for a
  multi-host fan-out. Appears in `derive` (+ a per-service active-in summary and trailing `service` =
  loaded / `activeService` = active TSV columns), `callers --entrypoints`/`--roots`, `tree`, and the
  `reaches`/`path` From line.
- Tokens are opaque to rig (a deployment convention, e.g. a startup-set id); a single rule gates all the
  EPs it produces the same way. Sub-rule runtime branches (an `if` inside one registrar that starts some
  actors only on one host) and cluster routing / lazy spawn are out of scope.

## Playgrounds

| Playground | Entry points | Effects | Index time |
|---|---|---|---|
| `EntryPointEffects` | 8 | ~23 | ~10 s |
| `eShop` | 41 | 100 | ~30 s |
| `OrchardCore` | 296 | 788 | ~5 min |

## Example: eShop

The `eShop` playground is a real-world microservices reference app (41 entry points, 100 effects).

> The snippets below are illustrative of the focused-tree and effects-inventory output. The numeric
> `callgraph N` / `effects --entrypoint N` commands were replaced by pattern-addressed `rig tree <pattern>`
> and `rig reaches <pattern>`; current output renders with box-drawing connectors and a per-effect emoji.

### Focused call tree — `PUT /items`

```
$ rig tree "CatalogApi.UpdateItem"

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
$ rig reaches "CatalogApi.UpdateItem"

  efcore           read            CatalogContext.CatalogItems
  efcore           commit          CatalogContext  [x2]  [read_before_commit:before_commit]
  ai_embeddings    read            IEmbeddingGenerator<TInput, TEmbedding>
  db_transaction   begin           DbContext.Database  [resilience_retry:ExecutionStrategy]
  db_transaction   commit          IDbContextTransaction  [resilience_retry:ExecutionStrategy]
  eventbus         publish         eShop.EventBus.Events.IntegrationEvent
```

### Sample `rig.rules.json`

Place next to the `.slnx`/`.sln` file to teach `rig` about framework-specific effects.

Rules are loaded in cascade order (each layer merges on top of the previous):
1. Built-in rules (shipped with the tool)
2. `~/.rig/rig.rules.json` — user-global overrides
3. `<solution-dir>/rig.rules.json` — solution-level rules
4. `<project-dir>/rig.rules.json` — per-project rules (one per project directory)
5. `--rules <path>` passed to `rig index` — explicit extra files, merged last (repeatable)

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

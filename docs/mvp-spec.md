# Runtime Intelligence Graph MVP Specification

## Status

Draft v0, derived from product design grilling on 2026-05-22.

This document replaces the broader initial PDR with a narrower implementation
plan for a CLI-first .NET code-mining product. The long-term vision remains a
runtime-aware distributed systems intelligence platform, but the first product
must prove value through static code/config mining and effect observability.

Shared terminology is maintained in
[ubiquitous-language.md](ubiquitous-language.md). Keep that document updated
when introducing or changing core product terms.

## Working Names

- Product concept: Runtime Intelligence Graph
- CLI working name: `rig`
- Naming is provisional. Avoid baking the brand into internal domain objects.

Preferred internal nouns:

- Observation
- Fact
- Run
- Profile
- EntryPoint
- CallGraph
- Effect
- EffectContext
- Resource

## Product Wedge

The MVP is not a chat product, MCP server, dashboard, or observability platform.

The first useful workflow is:

1. Index a .NET solution.
2. Detect application entrypoints.
3. Build a bounded application callgraph from an entrypoint.
4. Annotate that callgraph with interesting effects.
5. Label loop/parallel contexts and uncertain resolution clearly.
6. Persist everything in an immutable SQLite run.
7. Output human-readable raw structure that humans and LLM agents can inspect.

Example target command flow:

```text
rig index App.slnx
rig runs
rig entrypoints
rig callgraph ep:abc
rig effects --entrypoint ep:abc
rig files --skipped
rig profile validate
```

Human-readable output is the first priority because it makes extractor errors
easy to debug during development. Terse and JSON projections can be added later.

## Core Principles

### Ground Truth vs Derived State

Ground truth consists of immutable observations emitted by source/config
parsers:

- Roslyn AST/symbol observations
- config parser observations
- source inventory and skip decisions
- profile snapshot and built-in pack versions

Everything else is derived:

- facts
- effects
- resources
- callgraph edges
- observations/diagnostics
- projections

Each run must preserve enough evidence to answer why a fact exists.

### Run-Based Indexing

Every `rig index` creates an immutable run.

A run is:

```text
source snapshot
+ config snapshot
+ resolved profile snapshot
+ built-in pack versions
+ extractor versions
+ generated observations/facts/projections
```

Runs enable future diffing, CI analysis, profile comparisons, and reproducible
debugging. Keeping only the latest run can be a retention policy, not a data
model assumption.

### Solution Boundary

The input unit is an explicit .NET solution:

```text
rig index App.sln
rig index App.slnx
```

Application code is all non-skipped projects in the solution. NuGet packages,
BCL, and referenced projects outside the solution are external boundaries.

The indexer should fail loudly on unresolved compilation. Degraded best-effort
analysis can be added later behind an explicit flag.

### Customization From Day One

Every organization has local ceremony. Profiles are therefore an MVP feature,
not an extension afterthought.

Profiles define:

- entrypoint detection rules
- resource/effect detection rules
- config extraction rules
- link rules
- importance rules
- heuristic/convention rules
- project/file include/exclude rules

Profiles should be declarative first. Compiled plugin APIs are deferred.

Prefer simple targeted rules and composition over custom detector code. If a
framework pattern looks tempting to solve with a bespoke Roslyn walker, first
try to express it by composing reusable matcher primitives such as
type/namespace filters, inheritance filters, invocation filters, declaring or
receiver type filters, attributes, route-provider methods, file/project filters,
and exact symbol matches. Add a small reusable rule primitive before adding a
framework-specific detector whenever that keeps the behavior declarative.

Custom code-backed extractors are reserved for patterns that cannot be modeled
coherently as data. They should be the exception, because one-off detector code
does not scale across built-in packs, local conventions, or solution-specific
profiles.

Rule predicates compose with `AND`: every optional predicate present on a rule
must match before the rule emits. Express `OR` as parallel rules with the same
output shape. Multiple matches at the same code location are evidence worth
preserving, not a reason to bury framework-specific branching in detector code.

## Evidence Metadata

Facts, edges, effects, and observations must carry explicit evidence metadata:

```text
confidence: high | medium | low
basis: compilation | config | profile | msdi | heuristic | convention
reason: direct_symbol_match | appsettings_baseurl | single_impl | ...
evidence_ids: [...]
```

Confidence and basis are distinct. A fact can be high confidence but profile
derived, or medium confidence because it uses a heuristic.

Heuristics are enabled, but visibly labeled. They are allowed for callgraph
resolution and profile-defined conventions. Resource/effect detection should
prefer exact compilation/profile evidence in the first milestone.

## Architecture

Start as one .NET project and split later when boundaries harden.

Suggested layout:

```text
src/Rig/
  Cli/
  Core/
  Roslyn/
  Config/
  Profiles/
  Storage/
  Output/

tests/Rig.Tests/
playgrounds/
  MinimalApiEffects/
  MvcEffects/
```

Internal namespaces should anticipate future split:

```text
Rig.Core
Rig.Roslyn
Rig.Config
Rig.Profiles
Rig.Storage
Rig.Output
```

## Storage

Use SQLite with EF Core from the start.

EF Core owns persistence boilerplate. Roslyn walkers, config parsers, profile
evaluators, and projection builders should remain plain C# services that emit
records. Batch persistence happens at boundaries.

Initial storage concepts:

```text
runs
source_files
observations
facts
fact_evidence
fact_edges
profile_snapshots
projections
```

`facts.payload_json` should remain flexible while the rule engine stabilizes.

## Profiles

### Format

Prefer YAML for human editing, with schema validation and an internal normalized
JSON representation.

Invalid profiles fail indexing completely.

Validation must catch:

- unknown fields
- invalid enum values
- bad glob syntax
- duplicate rule IDs
- missing required emit fields
- unsupported extractor names
- unknown built-in packs

### Built-In Packs

Built-ins should be composable packs, not one monolithic `.NET` profile.

Milestone 0 built-ins:

- `dotnet.msdi`
- `dotnet.aspnet.minimalapi`
- `dotnet.aspnet.mvc`
- `dotnet.hostedservice`
- `config.appsettings`
- `effect.httpclient`
- `effect.efcore`
- `effect.redis`

Later packs:

- `data.mongo`
- `data.sqlclient`
- `data.dapper`
- `data.postgres`
- `cloud.azure.storage`
- `cloud.azure.servicebus`
- `cloud.azure.functions`
- `cloud.aws.s3`
- `cloud.aws.sqs`
- `cloud.aws.lambda`
- `cloud.gcp.pubsub`
- `observability.otel`

Built-ins should be declarative by default. Code-backed named extractors are
acceptable only when reusable matcher primitives cannot express a framework
pattern cleanly.

### Matcher Scope

MVP matchers cover:

- types
- methods
- invocations
- config paths

Invocation matching must support target and callsite filters.

Target filters:

- namespace
- receiver type
- declaring type
- method name
- generic arity where useful

Callsite filters:

- containing namespace
- containing type
- containing method
- file glob
- project name

Prefer exact resolved symbol matches first. Fallback matching for unresolved
symbols is deferred.

## Config Mining

Support `appsettings*.json` from day one.

Config parser output is ground truth observation data. Profiles then classify
config paths as atoms such as:

- base URLs
- connection strings
- resource names
- key prefixes
- option values

Custom config parsing should be profile-driven where possible.

## Roslyn Mining

### Compilation

The indexer must load `.sln` and `.slnx` inputs and require successful
compilation context for indexed projects.

Default behavior:

- verify solution is restorable/compilable
- do not run restore automatically
- support `--restore` later or immediately if cheap

### Source Inventory

Persist indexed and skipped files/projects.

Skip by default:

- test projects/files
- generated/build artifacts
- EF migrations for execution analysis

Test indexing can be opt-in later with `--include-tests`.

Skipped files must be queryable:

```text
rig files --skipped
```

### String Template Extraction

Provide a reusable string-template extractor in Roslyn/Core.

Examples:

```text
$"/users/{userId}/profile" -> /users/{userId}/profile
$"user:{userId}" -> user:{userId}
$"/users/{Normalize(id)}" -> /users/{expr}
```

Profiles consume this output for HTTP paths, Redis keys, queue names, and other
resource identifiers.

## Entry Points

Milestone 0 entrypoint families:

- ASP.NET Minimal API
- ASP.NET MVC controllers/actions
- Hosted services / background services

Tests are excluded by default.

### Minimal API

Support simple patterns:

- direct `MapGet`, `MapPost`, `MapPut`, `MapDelete`, `MapPatch`
- simple `MapGroup` prefix plus chained `MapX`
- inline lambdas
- method group handlers

Defer:

- deep nested groups
- custom endpoint builder wrappers
- complex endpoint filters/conventions

### MVC

Extract:

- controller type
- action method
- HTTP method attributes
- route attributes when unambiguous
- compiled symbol fallback when route cannot be resolved
- raw/decorative metadata where cheap

Use route attributes when clear. Otherwise fall back to compiled names such as:

```text
TeamsController.Get
```

## Dependency Injection

Microsoft DI is built in from the start.

Mine:

- `AddScoped<TService,TImpl>`
- `AddTransient<TService,TImpl>`
- `AddSingleton<TService,TImpl>`
- `TryAdd*`
- `AddHostedService<T>`
- simple open generics
- factory/instance registrations as partially resolved facts

DI facts:

```text
di_registration
  service_type
  implementation_type | factory | instance
  lifetime
  registration_kind
  confidence
  basis
  evidence
```

DI improves callgraph resolution and provides future scope/lifetime diagnostics.

Scope/lifetime facts belong in the model from day one, even if Milestone 0 only
uses them lightly.

## Callgraph

Default output is a full bounded application callgraph, not only effect paths.

Traversal rules:

- traverse application code only
- stop at NuGet/BCL/external project boundaries
- annotate boundary calls when they are known effects
- include unresolved/dynamic nodes explicitly
- label every edge with confidence/basis/reason
- detect cycles
- use a default depth limit

Opt-in compaction:

```text
rig callgraph ep:abc --effect-paths
rig callgraph ep:abc --compact
rig callgraph ep:abc --depth 10
```

Resolution sources:

- direct symbol calls
- MS DI registrations
- single implementation heuristic
- visible inline lambdas/delegates
- visible method groups where local resolution is clear

Double dispatch, delegate passing, mediator/framework dispatch, and complex
dataflow should be shown honestly rather than hidden.

Example:

```text
CALL Handler.Handle -> IProfileClient.GetProfile
  resolved=ProfileClient.GetProfile
  confidence=medium
  basis=heuristic
  reason=single_impl
```

## Effects

An effect is an interesting external or stateful operation. Milestone 0 tracks:

- HttpClient
- EF Core
- Redis

Effects are facts/projections derived from profile rules and Roslyn observations.

### HTTP

Detect:

- `HttpClient.GetAsync/PostAsync/PutAsync/DeleteAsync/SendAsync`
- `IHttpClientFactory.CreateClient`
- typed/named clients registered with `AddHttpClient`
- simple `BaseAddress`
- config-bound base URLs from appsettings
- absolute literal URLs
- relative literal/interpolated paths

Target identity aims for host + path:

```text
EFFECT http GET billing.internal /invoices/{id}
```

But output may include client identity when host/path is unresolved:

```text
EFFECT http GET host=? path=? client=BillingClient
```

Support only simple local URL construction in Milestone 0.

### EF Core

EF Core resource identity is `DbContext`, `DbSet`, and entity, not SQL table.

Detect reads and writes, while distinguishing guaranteed effects from heuristics.

High-confidence effects:

- `SaveChanges`
- `SaveChangesAsync`
- transaction begin/commit/rollback
- `ExecuteUpdate`
- `ExecuteUpdateAsync`
- `ExecuteDelete`
- `ExecuteDeleteAsync`

Materialized reads:

- `ToListAsync`
- `FirstAsync`
- `FirstOrDefaultAsync`
- `SingleAsync`
- `AnyAsync`
- `CountAsync`
- `FindAsync`

Pending mutations:

- `Add`
- `Update`
- `Remove`
- related change-tracker mutations

Pending mutations are useful but should be labeled heuristic/change-tracker
rather than durable writes.

Example:

```text
EFFECT efcore.commit AppDbContext SaveChangesAsync
  confidence=high basis=compilation

EFFECT efcore.pending_mutation AppDbContext.Teams Add
  confidence=medium basis=compilation+heuristic reason=change_tracker
```

### Redis

Initial target: StackExchange.Redis.

Detect:

- `StringGet/StringGetAsync`
- `StringSet/StringSetAsync`
- `KeyDelete/KeyDeleteAsync`
- `HashGet/HashSet`
- list/set operations where straightforward

Resource identity:

- key/template when visible
- key prefix when inferable
- Redis database/client otherwise

Interpolated strings should become key templates.

## Effect Contexts

Effect context is a first-class fact, not a hard-coded N+1 detector.

Built-in Roslyn structural contexts:

- `for`
- `foreach`
- `while`
- `await foreach`
- LINQ lambdas with effectful bodies
- `Task.WhenAll`
- `Parallel.ForEach`
- `Parallel.ForEachAsync`
- visible fire-and-forget-ish calls later

Profiles classify invocations as effects. Roslyn provides structural context.
Projections combine them.

Derived observations:

- `looped_effect`
- `parallel_fanout`
- `unresolved_resource`
- `unresolved_call_target`

Use the term "observations" in user-facing output, not "risks" initially.

## CLI Output

Default output should be human-readable, regular, and grep-friendly.

Callgraph header example:

```text
Callgraph: ep:minapi.GET./teams/{id}
Depth: 6
Nodes: 42
Edges: 51
Effects: 9
Unresolved: 4
Heuristic edges: 7
```

Inline effect example:

```text
-> BillingClient.GetInvoiceAsync
   EFFECT http GET billing.internal /invoices/{id}
     confidence=high basis=compilation+profile+config
```

Context example:

```text
LOOP foreach member in team.Members
  -> ProfileClient.GetProfileAsync
     EFFECT http GET profiles.internal /profiles/{memberId}
       OBS looped_effect confidence=high basis=compilation
```

## Milestones

### Phase 0: Repository and Spec

Deliver:

- local git repo
- MVP spec
- skeletal README

### Phase 1: Core Indexing Skeleton

Deliver:

- .NET CLI project
- `.sln` and `.slnx` input handling
- immutable run creation
- SQLite/EF Core storage
- profile loading and strict validation
- appsettings parser
- source inventory and skip decisions
- first playground solution

Acceptance:

```text
rig index Playground.slnx
rig runs
rig files --skipped
rig profile validate
```

### Phase 2: Roslyn Observations and MS DI

Deliver:

- Roslyn solution loading with compilation failure reporting
- symbol/method/invocation observations
- string-template extraction
- MS DI registration facts
- hosted service detection
- evidence metadata on facts

Acceptance:

- failed compilation fails loudly
- DI registrations are queryable
- skipped tests/generated files are persisted

### Phase 3: Entry Points and Callgraph

Deliver:

- Minimal API entrypoint detection
- MVC entrypoint detection
- application-only bounded callgraph
- external boundary nodes
- unresolved call nodes
- edge confidence/basis/reason
- cycle detection

Acceptance:

```text
rig entrypoints
rig callgraph ep:...
```

for both Minimal API and MVC playground entrypoints.

### Phase 4: HTTP, EF Core, and Redis Effects

Deliver:

- profile-driven HttpClient effects
- host/path resolution for simple URL construction
- EF Core read/write/commit/transaction effects
- Redis read/write/delete effects
- effect facts linked to callgraph nodes

Acceptance:

- callgraph output shows inline HTTP, EF Core, and Redis effects
- EF pending mutations are labeled heuristic
- HTTP and Redis string templates are shown when extractable

### Phase 5: Effect Contexts and Observations

Deliver:

- loop/foreach/while contexts
- LINQ effectful lambda contexts
- `Task.WhenAll`
- `Parallel.ForEach`
- `Parallel.ForEachAsync`
- derived observations:
  - `looped_effect`
  - `parallel_fanout`
  - `unresolved_resource`
  - `unresolved_call_target`

Acceptance:

- playground includes looped HTTP/Redis/EF reads
- callgraph output labels contexts and observations inline

### Phase 6: Built-In Packs and Playground Expansion

Deliver:

- built-in profile packs for Milestone 0
- Minimal API playground
- MVC playground
- tests asserting expected entrypoints, effects, DI facts, and contexts

Acceptance:

- deterministic regression tests against playgrounds
- profiles can be edited without changing code for simple matcher changes

### Phase 7: Diff and Agent Projections

Deliver later:

- `rig effects --changed`
- run-to-run effect diff
- `--json`
- `--terse`
- `--effect-paths`
- compact callgraph projections

This is intentionally after the raw index/callgraph/effect model works.

## Explicit Non-Goals for Milestone 0

- MCP server
- chat-first UX
- web dashboard
- runtime telemetry ingestion
- OpenTelemetry graph merge
- aggregate semantics
- SQL table truth
- EF migrations/schema mining
- public repo smoke suite
- compiled plugin API
- generic code assistant behavior
- full observability platform replacement
- formal verification
- perfect delegate/dataflow analysis

## Later Direction

After the static MVP proves useful, the same model can grow into:

- effect diffs in PRs
- architecture policy observations
- runtime trace ingestion
- observed vs static topology drift
- retry/failure topology
- cloud resource profiles
- MCP adapter wrapping existing CLI/query capabilities

MCP should be a transport over proven query primitives, not the first product
surface.

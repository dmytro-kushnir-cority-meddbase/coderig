# Ubiquitous Language

Shared vocabulary for the Runtime Intelligence Graph project.

Keep this document current when product, storage, profile, or CLI terms change.
It exists to reduce repeated explanation in specs, implementation notes, issues,
and future agent context.

## Product Surface

### `rig`

Working CLI name. Short for Runtime Intelligence Graph.

Use sparingly in code. Prefer domain names such as `Run`, `Fact`, and `Effect`
over branded names such as `RigFact`.

### Index

The act of analyzing a solution and producing a new immutable run.

`rig index App.slnx` should not mutate a previous run. It creates a new one.

### Run

An immutable analysis snapshot.

A run includes:

- solution input
- source/config snapshot
- resolved profile snapshot
- built-in profile pack versions
- extractor versions
- observations
- derived facts/projections

### Projection

A derived read model built from observations and facts.

Examples:

- effects by entrypoint
- callgraph output
- skipped files view
- future run-to-run diffs

Projections are disposable and rebuildable.

## Truth Model

### Observation

Ground-truth parser output.

Observations come from Roslyn, config parsers, source inventory, or profile
loading. They are evidence, not interpretation.

Examples:

- method symbol observed
- invocation observed
- config key/value observed
- source file skipped
- profile rule loaded

### Fact

A derived statement emitted by a rule, miner, or projection step.

Facts must link back to observations where possible and carry confidence,
basis, and reason.

Examples:

- entrypoint detected
- DI registration found
- HTTP effect detected
- Redis key template extracted
- call edge resolved

### Evidence

The observations and derivation metadata that explain why a fact exists.

Evidence must be inspectable. If a fact cannot explain itself, it is too opaque.

### Confidence

How strongly the tool believes a fact or edge.

Allowed values:

- `high`
- `medium`
- `low`

Confidence is not the same as evidence basis.

### Basis

The kind of evidence used to derive a fact.

Common values:

- `compilation`
- `config`
- `profile`
- `msdi`
- `heuristic`
- `convention`

Examples:

```text
confidence=high basis=compilation+profile reason=direct_symbol_match
confidence=medium basis=heuristic reason=single_impl
```

### Reason

A short machine-stable explanation for confidence and basis.

Examples:

- `direct_symbol_match`
- `appsettings_baseurl`
- `single_msdi_registration`
- `single_impl`
- `change_tracker`
- `ambiguous_route_composition`

## Source Boundary

### Solution

The explicit workspace boundary.

Milestone 0 supports:

- `.sln`
- `.slnx`

### Application Code

Non-skipped projects and source files inside the input solution.

The application callgraph traverses this code only.

### External Boundary

Code outside the application boundary.

Examples:

- NuGet packages
- BCL/framework assemblies
- project references not included in the input solution

Boundary calls can still be annotated as effects.

### Skipped Source

Source known to the indexer but not analyzed for execution behavior.

Default skipped source includes:

- test projects/files
- generated/build artifacts
- EF migrations for execution analysis

Skip decisions are persisted so missing facts can be explained.

## Profiles

### Profile

Declarative customization for detection and classification.

Profiles define:

- entrypoint rules
- effect/resource rules
- config extraction rules
- link rules
- importance rules
- heuristic/convention rules
- include/exclude rules

### Built-In Pack

A shipped profile pack for common frameworks or libraries.

Examples:

- `dotnet.msdi`
- `dotnet.aspnet.minimalapi`
- `dotnet.aspnet.mvc`
- `effect.httpclient`
- `effect.efcore`
- `effect.redis`

### Rule

A profile-defined matcher and emitter.

Rules should have stable IDs and explicit evidence metadata.

### Matcher

The part of a rule that decides whether code/config matches.

Milestone 0 matcher families:

- type
- method
- invocation
- config path

Invocation matchers distinguish target filters from callsite filters.

### Extractor

A reusable helper that converts matched code/config into structured values.

Examples:

- string template extractor
- HTTP URL extractor
- Minimal API endpoint extractor
- config value classifier

Compiled extension APIs are deferred. Named built-in extractors are allowed.

## Execution Model

### EntryPoint

An application execution origin.

Milestone 0 entrypoint families:

- ASP.NET Minimal API endpoint
- ASP.NET MVC action
- hosted/background service

Tests are excluded by default.

### Callgraph

A bounded application-code graph rooted at an entrypoint.

The default callgraph is full application code, not only paths to effects.
External libraries are boundary nodes.

### Call Edge

A directed relationship from caller to callee.

Edges must carry confidence, basis, and reason.

### Unresolved Call

A visible callgraph node or edge whose target cannot be confidently resolved.

Unresolved calls should be shown, not hidden.

### Dynamic Dispatch

Call resolution through interfaces, virtual dispatch, delegates, framework
dispatchers, mediator patterns, or similar mechanisms.

Milestone 0 resolves simple cases with direct symbols, MS DI, single
implementation heuristics, visible lambdas, and visible method groups.

### Single-Implementation Dispatch

A Dynamic Dispatch resolution strategy that resolves an interface call to a
concrete method when exactly one implementation of the interface is registered
in the MS DI container.

Resolution succeeds only when:
1. The declared receiver type is an interface.
2. Exactly one DI registration maps that interface to a concrete type.
3. The concrete type's matching method is in application code (not a boundary).

When resolution succeeds the call edge carries:

```text
confidence=medium basis=msdi reason=single_impl
```

When the interface has zero or multiple registrations the call edge remains
`BOUNDARY external`. Ambiguous dispatch is never silently collapsed.

This is a heuristic. If the concrete implementation is in a NuGet package
(not indexed source) the call still becomes `BOUNDARY external`.

## Effects

### Effect

An interesting external or stateful operation.

Milestone 0 tracks:

- HTTP calls
- EF Core reads/writes/commits/transactions
- Redis reads/writes/deletes

### Resource

The target of an effect, at the best available resolution.

Examples:

- HTTP host/path
- typed or named HTTP client
- EF Core `DbContext`/`DbSet`/entity
- Redis key/template/client/database

### Interesting Effect

An effect worth showing because it crosses process/state boundaries, mutates
state, depends on external infrastructure, or appears in a notable context.

Importance can be profile-driven.

### HTTP Effect

An HTTP operation, usually from `HttpClient` or a typed/named client.

Preferred identity is host + path when available.

### EF Core Effect

An EF Core operation.

Important distinctions:

- commit/transaction effects are high-confidence
- materialized reads are tracked
- pending mutations are useful but heuristic/change-tracker based
- SQL table truth is out of Milestone 0 scope

### Redis Effect

A Redis operation, initially targeting StackExchange.Redis.

Prefer key/template identity when statically visible.

## Effect Contexts

### EffectContext

The structural execution context around an effect.

Examples:

- `foreach`
- `for`
- `while`
- `await foreach`
- LINQ lambda
- `Task.WhenAll`
- `Parallel.ForEach`
- `Parallel.ForEachAsync`

Effect context is a first-class fact.

### Looped Effect

An effect executed inside a loop-like context.

This is an observation, not automatically a defect.

### Parallel Fanout

An effect executed through a parallel or fanout context such as `Task.WhenAll`
or `Parallel.ForEach`.

This is an observation, not automatically a defect.

## User-Facing Findings

### Observation

User-facing term for notable derived findings.

Use "observation" instead of "risk" or "diagnostic" until policies and severity
models mature.

Examples:

- `looped_effect`
- `parallel_fanout`
- `unresolved_resource`
- `unresolved_call_target`

### Risk

Reserved for later policy/scoring layers.

Do not use as the default Milestone 0 term.

### Diagnostic

Internal or future term for policy/tooling output.

Avoid using it as the default human-facing term in Milestone 0.

## Out-of-Scope Terms for Milestone 0

These are part of the long-term vision but should not drive early
implementation:

- aggregate
- runtime trace
- OpenTelemetry span
- drift
- retry topology
- failure topology
- architecture policy
- MCP tool
- dashboard

They can appear in roadmap discussions, but not as required Milestone 0
deliverables.

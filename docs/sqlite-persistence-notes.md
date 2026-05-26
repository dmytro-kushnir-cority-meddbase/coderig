# SQLite Persistence Notes

Current status after the first SQLite slice:

- `.rig/rig.db` stores immutable runs.
- each run stores the full `AnalysisResult` projection as JSON for stable CLI
  reads while the query model is still changing.
- first-class tables also store source files, entrypoints, effects, effect
  observations, method observations, invocation observations, callgraphs,
  callgraph nodes, and node calls.
- `rig runs` lists persisted runs.
- source files now include indexed/skipped status plus confidence, basis,
  reason, and evidence.
- `rig files --skipped` lists skipped files from the latest run.
- DI registration facts are persisted in `di_registrations`.
- external and unresolved callgraph boundaries are persisted in
  `callgraph_boundary_calls`.
- current built-in entrypoint, effect, DI, and file rules live in
  `src/Rig/Rules/builtin-rules.json`.

## Full AST Index Decision

Do not persist the full Roslyn AST as the default index yet.

Reasons:

- Roslyn syntax trees are large, version-shaped, and cheap to recreate from the
  source snapshot.
- A full AST dump would make the database noisy before the product knows which
  queries matter.
- F12-style navigation needs symbols, spans, declarations, and references more
  than it needs every syntax node.
- Profile rules should match against curated observations first, not a raw tree
  serialization format that becomes a second Roslyn API to maintain.

Instead, persist a compact code navigation index:

- source files with project, status, skip reason, hash, and future snapshot
  metadata.
- type declarations with symbol, display name, file, span, base types, and
  implemented interfaces.
- method declarations with symbol, display name, containing type, file, span,
  parameters, and return type.
- invocation observations with containing method, resolved target symbol when
  known, receiver type, file, span, confidence, basis, and reason.
- member/type reference observations for go-to-usages style queries.

This gives ReSharper-like questions a durable substrate:

```text
go to declaration: symbol -> declaration span
find usages: symbol -> references/invocations
show callers: target symbol -> containing methods
show callees: containing method -> invocation targets
```

Raw AST persistence can still be useful later as an opt-in diagnostic artifact
for extractor debugging, but it should not be the main product index.

## Reverse Trace Queries

`rig trace` answers "which entrypoints reach this method symbol?" from the
persisted forward callgraph rows. It does not store reverse edges or transitive
reachability as separate state.

Callgraph node symbols and node-call targets store canonical Roslyn method keys
where a method symbol exists. Human-readable CLI output derives short names from
those keys at render time, so the database remains the symbol lookup surface.

Current query model:

```text
callgraph_nodes       graph_index + node_index + symbol
callgraph_node_calls  graph_index + node_index + target_symbol
```

Trace execution:

```text
1. find graph indexes whose nodes contain the target symbol
2. load only those graph rows
3. build reverse edges in memory for this command invocation
4. walk target -> roots for upstream paths
5. walk target -> leaves/boundaries/effects for downstream context
```

Complexity for a trace over matching graphs is `O(N + E)` time and memory, where
`N` is loaded callgraph nodes and `E` is loaded call edges. The first filter is
indexed by `(run_id, symbol)` on `callgraph_nodes`, so non-matching entrypoints
are not loaded.

Do not add a materialized reverse graph or transitive closure yet. Reconsider
only after measurement shows repeated trace queries over large graphs dominate
runtime. Candidate future indexes:

```text
callgraph_reverse_edges(run_id, graph_index, target_symbol, caller_symbol)
entrypoint_reaches_symbol(run_id, graph_index, symbol)
```

`callgraph_reverse_edges` is low-risk but mostly duplicates forward edges.
`entrypoint_reaches_symbol` is a true reachability cache: faster for lookup, but
larger and easier to make stale or inconsistent with callgraph semantics. Treat
it as a measured optimization, not a default schema shape.

## File-Based Rules

File rules need source inventory before they need complex rule evaluation.

Implemented first pass:

- `source_files.status`: `indexed`, `skipped`, `generated`, `test`, etc.
- `source_files.reason`: profile rule or built-in reason.
- `source_files.confidence`, `basis`, and `evidence` explain the decision.
- `rig.rules.json` next to the solution can exclude or include files.

Current rule format:

```json
{
  "files": {
    "exclude": [
      {
        "id": "generated-fixtures",
        "glob": "**/Generated/*.g.cs",
        "reason": "generated_fixture"
      }
    ],
    "include": []
  }
}
```

Rules are intentionally small and JSON-based for now to avoid taking a YAML
dependency before profile validation exists. Exclude rules win over include
rules. Include rules can force-index files that built-in conventions would skip.

Remaining schema pressure:

- future columns: content hash, language, project path, relative path.

Profiles can then attach include/exclude facts to source files and keep the
rest of analysis honest about what was not indexed.

## MS DI Facts

DI should write facts rather than only improving callgraph traversal in memory.

Implemented first fact shape:

```text
di_registration
  service_type
  implementation_type
  lifetime
  registration_kind
  confidence
  basis
  reason
  evidence
```

The existing invocation observations are enough to detect registration calls.
The next slice should use DI facts for constructor/interface resolution.

## External And Unresolved Boundaries

The callgraph should eventually persist boundary nodes, not silently drop calls
outside application code.

Implemented boundary call rows:

- external calls with package/BCL/framework target symbols.
- unresolved calls with receiver expression, method name, file, span, and reason.
- dynamic/delegate calls where the target is intentionally uncertain.

This belongs next to invocation observations because the evidence is the same
callsite, just classified differently.

When an external boundary call also matches an effect rule, the stored facts
remain separate: boundary rows preserve the structural call edge and effect rows
preserve provider/operation/resource classification. CLI renderers join them at
projection time by file/line/method and print the `EFFECT` in the boundary's
source-order position, suppressing the duplicate boundary line.

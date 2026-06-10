# rig — reference

## Command reference

| Command | Purpose |
|---|---|
| `rig index <sln\|csproj> [--rules p…] [--identity id] [--from entry.csproj] [--parallelism n] [--durable]` | Extract facts. Solution = all projects' source in one run (callgraph crosses boundaries in-process). Single `.csproj` = that project's source only (refs become metadata DLLs). **`--from entry.csproj`** = index only the entry project's transitive ProjectReference closure (minus test projects) — still ONE cross-project workspace; writes the closure to `relevant-projects.json`. Skips all out-of-closure test/tool projects before their design-time build. **`--parallelism n`** parallelises the (independent, no-binary) design-time builds. **Save is fast + crash-safe by default**: durability-off pragmas + write-to-temp + atomic rename over `rig.db` (so a fresh `index` cleanly REPLACES — the old "index APPENDS" footgun is gone; that now only applies to `mine`/`--identity`). **`--durable`** opts into a journaled write. |
| `rig mine <sln> --from <csproj> [--rules p…] [--identity id] [--parallelism n]` | BFS DOWN the dep graph from `--from` (toward what it references). Each project indexed as its own run under one `--identity`, stitched at query time. Direction matters: `--from Pages` reaches Workflows; `--from Workflows` does NOT reach Pages. |
| `rig runs` | List runs + symbol/reference/di counts (provenance / health check). |
| `rig derive [--rules p…] [--limit n] [--format tsv]` | Stage-2 over facts: re-derive effects + page/action/background/wcf entry points + delegate/method-group handoffs. One DB open, one rule load. |
| `rig reaches <pat> [--maxdepth n] [--format tsv]` | Effects reachable forward from an entry point. Annotates loop-fanout (`🔁xN`). |
| `rig tree <pat> [--full\|--summary] [--maxdepth n]` | Full first-party call TREE. Default prunes to effect-bearing paths; `--full` = all; `--summary` = effect rollup. `↺seen` marks cycle/shared-callee re-entry. |
| `rig callers <pat> [--roots] [--maxdepth n]` | Reverse reachability. `--roots` = entry-point candidates (reachable methods with no predecessor) = "which entry points touch X". |
| `rig path <from> <to>` | One concrete path (BFS-shortest), with per-hop file:line + loop context. |
| `rig dead [--lib] [--include-dispatch] [--all] [--root pat…] [--rules p…] [--format tsv]` | Unreachable first-party methods. Report-only. See SKILL.md. |
| `rig refs <pat> [--first-party] [--kind <refkind>] [--limit n]` | Reference facts to a symbol (invocation/methodGroup/ctor/typeUse/throw/attributeUse). |
| `rig symbols <pat> [--kind <k>] [--limit n]` | Declared symbols (method/type/property/field/event). |
| `rig di` | DI registrations (code + XML service descriptors + static rule mappings), run-agnostic. |
| `rig files --skipped` | Source files skipped during indexing + why (diagnostic). |
| `rig profile validate` | Validate the rules config for the working solution. |

There is no longer a separate "legacy" command model: everything is computed from the fact tables
(`symbol_facts`/`reference_facts`/`type_relation_facts` + run-agnostic `di_registrations`/`source_files`),
DocID-joined, with no per-run stitching. The old `entrypoints`/`effects`/`trace`/`callgraph(s)` commands
were removed — use `derive` (effects + entry points), `reaches`/`tree`/`callers`/`path` (call graph).

DocID shapes: `M:Ns.Type.Method(ArgTypes)`, `T:Ns.Type`, `!:Name` (error type — a reference that
failed to bind). Generic: open form `Foo\`1`, instantiated `Foo{T:Ns.X}`.

## The rule / detector model — "detectors are data"

All entry-point and effect detection is JSON rules (cascaded: builtin `builtin-rules.json` +
`--rules`), consumed by data-driven derivers. **No detector logic is hardcoded in C#** — adding a rule
gives new answers with no re-extraction. Rule knobs that exist:
- **Effect rules**: `provider`/`operation`, `methods`, `declaringTypes` / `declaringTypeNameEndsWith` /
  `declaringTypeBaseTypes`, `receiverTypes`, containing-namespace/type/method gates, `matchConstructor`
  + `minArguments` (ctor-fetch, e.g. `new XxxEntity(pk)`), `matchThrow` (thrown exception type), resource-resolution strategy.
- **Entry-point rules**: `pageModel` (base-type BFS closure → ctors), `[Attribute]` action rules,
  `classInheritance` (base + interface closure, `requireOverride`, `handlerMethodAttributes`,
  `handlerParameterTypes`).
- **File rules**: `files.exclude` globs (default excludes `**/*.g.cs`, `**/*.generated.cs`,
  `**/*.designer.cs`, `**/Generated/**`, **and `**/tests/**`**), `testProjectPatterns`.

Detector families seen in practice (MedDBase rules): `llblgen` (entity read/write/delete, ctor-fetch),
`entity_cache` (`*Cache.New(pk)` source-of-truth reads), `object_store` (IObjectStore Create/Update/Delete),
`throw` (incl. LanguageExt `failwith`/`raise` via `declaring_type` strategy), `clientpage_nav/event/proxy`,
`chamber_msg` / `eventbus` (messaging), `soap`/`http`/`queue`/`llm` (external providers).

## Reachability / dispatch behaviour

The graph follows: direct calls + method-group + ctor edges, **interface→impl** dispatch (single-impl
DI hop), and **base-virtual/abstract→override** dispatch (transitive, generic-stripped, IsOverride-gated).
Recall recoveries already built in:
- **`!:` error-edge fallback** — under partial binding an implementer's interface edge can be an error
  type (`!:IFoo`) while the call resolved the real type; dispatch recovers via interface simple-name. (Highest-impact recall fix.)
- **generic-stripped lookup** — a base edge stores instantiated `Foo{A,B}` while methods are on open
  `Foo\`2`; lookups strip generics so dispatch still lands.
- **transitive in-set project references** — the index workspace wires the full transitive closure of
  in-set project refs as live Roslyn ProjectReferences (not just direct), so a project using a
  transitively-referenced project's types binds them as ONE identity. Without this, calls whose
  signature mentions such a type silently drop (was the cause of massive `rig dead` false positives).

## Fundamental static-analysis limits (NOT bugs — interpret results with these in mind)

- **Actor / message-passing boundaries** (e.g. Echo.Process `.tell`/`.ask`/Router) are NOT call edges —
  effects inside actor inboxes are invisible; the boundary is uncrossable statically.
- **Reflection dispatch** (`GetMethod(name).Invoke`, `[ClientAction]` request handlers) has no caller
  edge. rig models such handlers as INDEPENDENT entry points where a rule exists, but won't link caller→callee.
- **`Activator.CreateInstance` / DI-by-name / serialization** — instantiation/use not visible as a call.
- **Cross-process calls** (HTTP to another service, payment gateways) end the trace.
- **Property/event use isn't an invocation fact** — lazy nav-property loads, operator use, accessor
  calls don't appear as edges (why `rig dead` excludes accessors/operators).
- Runtime cardinalities (×N loop fanout beyond the static nesting count, "called 8 times") are estimates, not facts.

## Env gotchas

- **Pre-build the target** (MSBuild, not design-time) before `index`/`mine` so bins have DLLs; net48 web
  projects use a FLAT `bin/`, not `bin/Debug/net48`.
- **`mine` at `--parallelism 1`**; never run two mines at once; a large mine depletes shared net48 DLLs —
  rebuild the `--from` project before any subsequent single-project index, or it binds against a near-empty bin.
- **`rig index` now REPLACES atomically** (temp + rename) — re-running a standalone `index` cleanly overwrites; no need to delete `.rig` first. `mine` (and `index --identity`) still APPENDS in place — delete `.rig` before re-mining the same target.
- **Rules load at INDEX time vs QUERY time are asymmetric** — `rig index` resolves rules relative to the SOLUTION dir (+ builtin/global), NOT the analysis cwd. **DI registrations + the XML DI miner (`xmlDiFiles`) are captured at INDEX time**, so if your ruleset lives in the analysis cwd you MUST pass `--rules <that>.json` to `rig index` or `rig di` comes back empty (effects/entry points are derived at QUERY time from cwd rules, so they look fine — the asymmetry is silent). Symptom: `DiRegistrations: 0` despite XML service descriptors existing.
- **Index with the published/global `rig`**; the plain Debug bin throws `System.Composition.TypedParts`
  not found from `AdhocWorkspace` (missing MEF deps). Read-only queries work from any build.
- **Generated/source-gen types**: only indexed if generators actually run during the build (Buildalyzer
  design-time builds may not run them) — a known gap for generator-produced types (e.g. proxy base types).
- **Base-type-chain binding flake**: a run can show entrypoints/effects ≈0 with healthy symbols/refs when
  a cross-assembly base type (in metadata) didn't bind that run — net48 reference-resolution flakiness, not a logic bug. Re-index / broaden scope.

## Building & shipping the tool (coderig repo)

- Fast iterate (read-only queries): `dotnet build src/Rig.Cli/Rig.Cli.csproj -c Debug -p:UseSharedCompilation=false`
  then `dotnet <…>/bin/Debug/net10.0/Rig.Cli.dll <args>` from the dir holding `.rig`.
- Ship to global (needed for `index`/`mine`): `dotnet pack src/Rig.Cli/Rig.Cli.csproj -c Release -o .rig-nupkg /p:PackageVersion=$v /p:Version=$v` → `dotnet tool uninstall --global rig; dotnet tool install --global rig --add-source .rig-nupkg --version $v`. (The repo's `mini-ci.ps1` does this but the `-ExecutionPolicy Bypass` invocation may be blocked by the harness; run the pack/install steps directly.)
- Tests: `dotnet test tests/Rig.Tests/Rig.Tests.csproj -c Debug -p:UseSharedCompilation=false`. Fact-layer derivers are fixture-tested (no SQLite) in `FactDerivationTests`; pure-domain graph logic in `Domain/*Tests.cs`.

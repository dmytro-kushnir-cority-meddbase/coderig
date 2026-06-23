# rig — reference

## Command reference

| Command | Purpose |
|---|---|
| `rig index <sln\|csproj> [--rules p…] [--identity id] [--from entry.csproj] [--parallelism n] [--durable]` | Extract facts. Solution = all projects' source in one run (callgraph crosses boundaries in-process). Single `.csproj` = that project's source only (refs become metadata DLLs). **`--from entry.csproj`** = index only the entry project's transitive ProjectReference closure (minus test projects) — still ONE cross-project workspace; writes the closure to `relevant-projects.json`. Skips all out-of-closure test/tool projects before their design-time build. **`--parallelism n`** parallelises the (independent, no-binary) design-time builds. **Save is fast + crash-safe by default**: durability-off pragmas + write-to-temp + atomic rename over `rig.db` (so a fresh `index` cleanly REPLACES — the old "index APPENDS" footgun is gone; that now only applies to `--identity`). **`--durable`** opts into a journaled write. |
| `rig mine <sln> --from <csproj>` *(legacy/superseded)* | BFS-down-the-dep-graph indexer, separate run per project under one `--identity`. **Superseded by `rig index --from <csproj>`** (one cross-project workspace, internal parallel build) — use `index --from`. The `mine` subcommand still ships in the binary but is no longer the documented path. |
| `rig runs` | List runs + symbol/reference/di counts (provenance / health check). |
| `rig derive [--rules p…] [--limit n] [--only p,…] [--exclude p,…] [--exclude-namespace pfx…] [--list-providers] [--format tsv]` | Stage-2 over facts: re-derive effects + page/action/background/wcf entry points + **classified** handoffs (background/timer/actor/event, by dispatcher + registration site, with an `async_handoff` note) + promoted handoff origins; unclassified-methodGroup residual collapsed to a count. One DB open, one rule load. **`--list-providers`** prints the valid `--only`/`--exclude` token set (an unknown token WARNS on stderr, no longer silent). **`--exclude-namespace <prefix>`** (repeatable) drops hazard findings whose enclosing namespace starts with the prefix (hazards only; effects unaffected). Hazard rollup is deduped per `(type, method)` with a `×N` site count; header reads "N site(s) across M method(s)". |
| `rig entrypoints [--rules p…] [--store ref] [--limit n] [--format tsv]` | The rule-detected entry-point set (page/action/class-inheritance + promoted async-handoff origins — the SAME set `derive`/`callers --entrypoints`/`impact` build), grouped by kind, with service attribution when `deployments.json` is present. A focused listing without `derive`'s effect + classified-handoff sections. `--format tsv` = one row per EP (kind, route, file, line, requires, loaded services, active services). |
| `rig reaches <pat> [--async] [--maxdepth n] [--only p,…] [--exclude p,…] [--format tsv]` | Effects reachable forward. **Synchronous by default** (async handoffs NOT crossed). **`--async`** walks handoffs → an extra **async (scheduled)** `⚡cross_thread` bucket (`⤳ via <dispatcher>`) alongside direct + dispatch-fan-out. Annotates loop-fanout (`🔁xN`) + per-effect emoji. TSV trailing `handoffVia` column. |
| `rig tree <pat> [--view paths\|full\|effects\|summary\|hazards] [--format llm\|llm-ids] [--suppress ctors,lambdas\|none] [--async] [--only p,…] [--exclude p,…] [--exclude-namespace pfx…] [--maxdepth n]` | Call TREE (box-drawing, source-ordered, emoji per effect). **`--view`** (default `paths` = effectful paths, spine-kept): `full` = all nodes; `effects` = flat effectful-method list; `summary` = effect-count rollup; `hazards` = hazard overlay. **Synchronous by default**; **`--async`** crosses handoffs (`⤳handoff via <dispatcher> [cross_thread]`). `↺seen` marks cycle/shared-callee re-entry. **`--format llm`** = compact LLM TSV (`depth name arity calls effects flags`; ASCII `effect*N`, arity-stripped names, parent implicit via depth+DFS order; truncation flags split by cause — `seen` = already-expanded-elsewhere (the redundancy signal), `depth-capped` = hit `--maxdepth`, `budget-capped` = hit the node-budget cap); **`--format llm-ids`** = 8-col with explicit `id`/`parent_id` + `seen:<canonicalId>` O(1) back-ref (back-ref only on `seen`; `depth-capped`/`budget-capped` carry no id). `--format llm*` composes with paths/full/effects (not summary/hazards); **`--suppress`** (default `ctors,lambdas` for llm) drops ctor/lambda rows, rolling their effects up. |
| `rig callers <pat> [--roots\|--entrypoints] [--async] [--rules p…] [--maxdepth n]` | Reverse reachability. **Synchronous by default**, so a background callback has no synchronous predecessor → it surfaces as its own `--roots` origin; **`--async`** counts the registrar as reaching it via the handoff. `--roots` = no-predecessor candidates (heuristic — also surfaces unbound interface members). **`--entrypoints` = the RULE-DETECTED entry points (the `derive` set: page/action/handler + promoted handoff origins) that reach the target** — the precise "which of my real entry points touch this code", joined by declaration site. The matched TARGET (and its own lambdas) is listed under a separate "Matched nodes" section, NOT counted as a caller. `--roots` is a SUPERSET of `--entrypoints` (heuristic vs rule-detected). |
| `rig path <from> <to> [--async]` | One concrete path (BFS-shortest), with per-hop file:line + loop context. Synchronous by default; **`--async`** crosses + renders the `⤳ handoff via <dispatcher>` hop. |
| `rig impact --base <ref> --head <ref> [--repo p] [--structural] [--async] [--expect-no-effect-change] [--format tsv]` | Blast radius + behavioral diff of a git change vs another commit. **BOTH `--base` and `--head` REQUIRED** (each resolves a ref→sha→store via `--store`-style refs; no LATEST defaulting). (1) changed methods (FILE-granular over-approx), (2) affected entry points by deployed service, (3) behavioral delta = effects/observations reachable from the changed set, head vs base (`+`/`-effect`, `+/-observation`; param-free keys → formatting/signature-immune). The **per-entry-point effect-set diff is the DEFAULT** (surfaces path-masked deltas + relocations); **`--structural`** expands the structural-only summary into the full reach-changed EP list; **`--expect-no-effect-change`** = CI gate (exit 1 on any effect-set change). Needs BOTH commits indexed; `--repo` = source repo for the diff when it's a separate tree. See SKILL.md. |
| `rig graph` | Rebuild the derived call-graph views (`call_edges`+`dispatch_edges`) from facts — the fast SQL traversal path. Idempotent, no rescan. **Now run automatically at the tail of `index`** (opt out: `index --no-graph`); run standalone after editing `handoffDispatchers`/factory rules (no re-index needed). |
| `rig dead [--lib] [--include-dispatch] [--all] [--root pat…] [--rules p…] [--format tsv]` | Unreachable first-party methods. Report-only. ⚠️ **Currently DISABLED** (unwired in Root.cs — errors "not matched"). See SKILL.md. |
| `rig refs <pat> [--first-party] [--kind <refkind>] [--limit n]` | Reference facts to a symbol (invocation/methodGroup/ctor/typeUse/throw/attributeUse). |
| `rig symbols <pat> [--kind <k>] [--limit n] [--no-lambdas]` | Declared symbols (method/type/property/field/event). `--no-lambdas` drops compiler `~λ`/`<>c` noise; when truncated prints "showing N of TOTAL". |
| `rig di` | DI registrations (code + XML service descriptors + static rule mappings), run-agnostic. |
| `rig files --skipped` | Source files skipped during indexing + why (diagnostic). |
| `rig profile validate` | Validate the rules config for the working solution. |

There is no longer a separate "legacy" command model: everything is computed from the fact tables
(`symbol_facts`/`reference_facts`/`type_relation_facts` + run-agnostic `di_registrations`/`source_files`),
DocID-joined, with no per-run stitching. The old `entrypoints`/`effects`/`trace`/`callgraph(s)` commands
were removed — use `derive` (effects + entry points), `reaches`/`tree`/`callers`/`path` (call graph).

### `--format llm` / `llm-ids` — exact column contract (read before parsing it programmatically)

The column SCHEMA depends on `--view`, which trips up a parser told a fixed width:

| `--view` | `--format llm` columns (TSV) | `--format llm-ids` columns (TSV) |
|---|---|---|
| `paths` (default), `full` | `depth name arity calls effects flags` (6) | `id parent_id depth name arity calls effects flags` (8) |
| `effects` | `depth parent name arity calls effects flags` (7 — adds `parent`) | `id parent_id depth name arity calls effects flags` (8) |

- **`parent_id` MEANS DIFFERENT THINGS per view (correctness trap):** in `paths`/`full` it is the
  CALL-GRAPH parent (your direct caller); in `effects` it is the NEAREST EFFECTFUL ANCESTOR, which may be
  several call hops above the real caller. Don't reason about "who called this" from `effects`-view
  `parent_id`.
- **`depth` in `--view effects` is the ORIGINAL call-tree depth** — non-contiguous down the flat list
  (e.g. 1,6,10,5,…). Reconstruct structure from `parent`/`parent_id`, NOT from `depth`. In `paths`/`full`,
  rows are DFS pre-order so a row's parent is the nearest preceding row at `depth-1` (and `parent_id` confirms it).
- **`flags` truncation token differs by format:** bare `seen` in `--format llm`; `seen:<canonicalId>` in
  `llm-ids` (the id of the first full expansion). `depth-capped`/`budget-capped` are the same in both and
  never carry an id. A regex over `flags` must accept both `seen` and `seen:<n>`.
- The root row's `calls` is a sentinel (`1`), not a caller count — meaningless for an entry point.
- Token budget: ~6–10× smaller than `--format tsv`. `--suppress ctors,lambdas` (the llm default) rolls
  suppressed nodes' effects onto the nearest kept ancestor (no effect lost); `--suppress none` to see them.

**Commit-scoped stores + `--store`.** `index` writes a per-commit store `.rig/<short-commit>/` (`-dirty`
when the work tree is dirty, `ts-<stamp>` off-git) + a `.rig/LATEST` pointer; reads default to LATEST. Every
read command (`reaches`/`tree`/`path`/`callers`/`derive`/`graph`) and `impact --base` accepts **`--store
<ref>`** (aliases `--commit`/`--at`) to target a specific store by store-id or commit-sha prefix — hold two
commits side by side and diff. A pre-layout flat `.rig/rig.db` is still read (moved to `.legacy.bak` on the
next index).

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

**Filtering effects** (`reaches`/`tree`/`derive`): `--only <list>` keeps just those, `--exclude <list>`
drops them (exclude wins on overlap). The list is **comma- OR whitespace-separated and repeatable**; tokens
match `provider` (`throw`) or `provider:operation` (`llblgen:read`). Headline: **`--exclude throw`** to drop
exception noise. Per-effect emoji glyphs are overridable per-repo via `rig.effect-emoji.json` (or
`.rig/effect-emoji.json`): a flat `{"llblgen:write":"💾","soap":"☎️",…}`, looked up `provider:operation` →
`provider` → `•`.

## Reachability / dispatch behaviour

The graph follows: direct calls + method-group + ctor edges, **interface→impl** dispatch (single-impl
DI hop), and **base-virtual/abstract→override** dispatch (transitive, generic-stripped, IsOverride-gated).

**Exact dispatch facts (mined-first model).** The member-level interface→impl / base→override
correspondence is MINED by Roslyn at extraction into `dispatch_facts`
(`IMethodSymbol.OverriddenMethod` for overrides, `FindImplementationForInterfaceMember` for interface
impls — signature-exact and generic-correct, incl. explicit interface impls and same-arity overloads
that name matching cross-contaminates). Query-time dispatch uses these FIRST (`Basis="roslyn"`,
forward closure over the immediate mined hops); the old name+arity CHA scan remains ONLY as a
fallback for members with no mined edge, plus the always-on `!:` error-edge simple-name recovery —
both flagged **`Basis="heuristic"`** and rendered with a `~heuristic` marker (tree
`«impl-dispatch ~heuristic»`, path `[impl-dispatch (heuristic)]`, reaches `~heuristic` suffix, TSV
`dispatchBasis` column) — meaning: the dispatch target was inferred by name/arity because Roslyn
couldn't bind that interface/base (net48 partial binding); high-but-not-perfect, verify before relying
on it. `rig graph` prints the roslyn-vs-heuristic split; `dispatch_edges` carries
`Basis`. Heuristic share on a healthy single-workspace index is small (the `!:` residue); a store
indexed BEFORE dispatch facts existed derives all-heuristic until re-indexed (extraction change →
re-`rig index` + `rig graph` to benefit).

**Async handoff edges (`Kind="handoff"`).** A method-group consumed by a curated `handoffDispatchers`
dispatcher (background/timer/actor/event scheduler) is reclassified to a `handoff` edge at graph-build
time (`HandoffClassifier`, co-location on the registration site; written to `call_edges.Kind` +
`HandoffDispatcher` by `rig graph`). Traversal **cuts these by default** (sync — a registration doesn't
look like it runs its callback) and walks them under **`--async`**, carrying a `HandoffVia` provenance tag
(cloned from the `DispatchVia` machinery: inherited through the scheduled subtree, dropped where a node is
also reachable synchronously). `--async` is uniform across `reaches`/`tree`/`path`/`callers`. Invariants
the SQL path holds per mode: `CHA-oracle(sync) == SQL(Kind<>'handoff')`, `CHA-oracle(async) == SQL(unfiltered)`,
`narrowed ⊆ SQL`, `sync ⊆ async`, and the bounded SQL load stays the (async) superset of both. **`dead`
keeps ALL method-group AND handoff targets as roots** regardless of classification (recall rail), so a
scheduled-only callback is never falsely flagged dead.

**Receiver-type narrowing (precision — the signal/noise fix).** `tree`/`reaches`/`path`/`callers` resolve
dispatch **edge-aware**: a virtual/base/interface call narrows to the *static receiver type* mined onto the
call edge (`CallEdge.ReceiverType`), so `company.Save()` reaches `CompanyEntity.Save` (+ Company subtypes),
NOT all ~114 `CommonEntityBase.Save` overrides. It falls back to **full CHA** whenever the receiver is
unreliable (null / an interface / an error-type `!:` / the declaring base itself / not a first-party CHA
target) — so no real target is dropped. Effect of this on the MedDBase monolith: `ProcessHealthcodeQueue
--effects` went 604→405 and the 114-way Save god-seam vanished. Two notes:
- The precomputed `dispatch_edges` table (which BOUNDS the SQL load) stays **CHA (the sound superset)**;
  narrowing lives only in the in-memory traversal. So SQL set-reachability ⊇ the narrowed in-memory set.
- Reverse (`callers`) narrowing is **dispatch-hop-precise, not path-precise**: once a shared base method
  (e.g. `CommonEntityBase.Save`) enters the reverse closure, all its direct callers rejoin (set-based BFS
  can't attribute which caller's receiver matched). Recall-preserving; some reverse precision is left on
  the table. Residual forward fan-outs (×2–×13) are genuinely base/interface-typed receivers
  (controllers/`IWorkflowMaster`, actor dispatch, loggers) correctly falling back to CHA — "all bets off
  past these god-seams".

**Reading effect counts:** an effect listed N times = **N static call-sites** that reach it (branches
included), NOT N runtime writes — an insert-vs-update method shows the same write twice; one fires per call.
`reaches` also splits genuine reach from a **dispatch fan-out** bucket (`reach is … NOT a real call`); read
the direct-effects / depth-shallow surface as the real contract.

Recall recoveries already built in:
- **`!:` error-edge fallback** — under partial binding an implementer's interface edge can be an error
  type (`!:IFoo`) while the call resolved the real type; dispatch recovers via interface simple-name,
  marked `~heuristic`. (Highest-impact recall fix — never removed by the exact-dispatch model.)
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

- **`rig index` builds internally — no external pre-build.** One `rig index <sln> --parallelism 16
  --reuse-build-cache` call runs a parallel, cached design-time build itself then extracts; the old
  "MSBuild first" step is obsolete. `index` is the sole extraction command; `index --from <csproj>` covers
  the entry-scoped closure (the old `mine`). Never run two `index` commands against one clone at once.
- **`rig index` REPLACES atomically** (temp + rename) — re-running a standalone `index` cleanly overwrites;
  no need to delete `.rig` first. Only `index --identity` (multi-solution accumulate) APPENDS in place.
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
- Ship to global (needed for `index`): `dotnet pack src/Rig.Cli/Rig.Cli.csproj -c Release -o .rig-nupkg /p:PackageVersion=$v /p:Version=$v` → `dotnet tool uninstall --global rig; dotnet tool install --global rig --add-source .rig-nupkg --version $v`. (The repo's `mini-ci.ps1` does this but the `-ExecutionPolicy Bypass` invocation may be blocked by the harness; run the pack/install steps directly.)
- Tests: `dotnet test tests/Rig.Tests/Rig.Tests.csproj -c Debug -p:UseSharedCompilation=false`. Fact-layer derivers are fixture-tested (no SQLite) in `FactDerivationTests`; pure-domain graph logic in `Domain/*Tests.cs`.

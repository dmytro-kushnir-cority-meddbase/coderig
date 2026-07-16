# rig — reference

## Command reference

| Command | Purpose |
|---|---|
| `rig index <sln\|csproj> [--rules p…] [--identity id] [--from entry.csproj] [--framework tfm] [--parallelism n] [--merge] [--include-tests] [--no-graph]` | Extract facts. Solution = all projects' source in one run (callgraph crosses boundaries in-process). Single `.csproj` = that project's source only (refs become metadata DLLs). **`--from entry.csproj`** = index only the entry project's transitive ProjectReference closure (minus test projects) — still ONE cross-project workspace; writes the closure to `relevant-projects.json`. **`--framework tfm`** selects that TFM for multi-targeted projects; without it the first declared TFM is used. **`--parallelism n`** parallelises the (independent, no-binary) design-time builds. **Save is fast + crash-safe by default**: durability-off pragmas + write-to-temp + atomic rename over `rig.db` (so a fresh `index` cleanly REPLACES — the old "index APPENDS" footgun is gone; that now only applies to `--identity`). **`--include-tests`** keeps test projects (excluded by default); **`--no-build-cache`** / **`--verify-build-cache`** control the design-time-build cache. (There is NO `--durable`/journaled-write option — removed.) |
| `rig runs` | List runs + symbol/reference/di counts (provenance / health check). |
| `rig derive [--rules p…] [--limit n] [--only p,…] [--exclude p,…] [--exclude-namespace pfx…] [--list-providers] [--format tsv]` | Stage-2 over facts: re-derive effects + page/action/background/wcf entry points + **classified** handoffs (background/timer/actor/event, by dispatcher + registration site, with an `async_handoff` note) + promoted handoff origins; unclassified-methodGroup residual collapsed to a count. One DB open, one rule load. **`--list-providers`** prints the valid `--only`/`--exclude` token set (an unknown token WARNS on stderr, no longer silent). **`--exclude-namespace <prefix>`** (repeatable) drops hazard findings whose enclosing namespace starts with the prefix (hazards only; effects unaffected). Hazard rollup is deduped per `(type, method)` with a `×N` site count; header reads "N site(s) across M method(s)". |
| `rig entrypoints [--rules p…] [--store ref] [--limit n] [--format tsv]` | The rule-detected entry-point set (page/action/class-inheritance + promoted async-handoff origins — the SAME set `derive`/`callers --entrypoints`/`impact` build), grouped by kind, with service attribution when `deployments.json` is present. A focused listing without `derive`'s effect + classified-handoff sections. Each EP also carries its queryable FQN — a `↪ <fqn>` line in human output (suppressed when it equals the route) and a trailing `fqn` column under `--format tsv` (one row per EP: kind, route, file, line, requires, loaded services, active services, **fqn**). |
| `rig reaches <pat> [--async] [--include-delivery] [--maxdepth n] [--only p,…] [--exclude p,…] [--format tsv]` | Effects reachable forward. **Synchronous by default** (async handoffs NOT crossed). **`--async`** walks handoffs → an extra **async (scheduled)** `⚡cross_thread` bucket (`⤳ via <dispatcher>`) alongside direct + dispatch-fan-out, but EXCLUDES imprecise delivery fan-out (multi-subscriber `event_raise`/`actor_tell`); **`--include-delivery`** adds that over-approximate superset back. Annotates loop-fanout (`🔁xN`) + per-effect emoji. TSV trailing `handoffVia` column. |
| `rig tree <pat> [--view paths\|full\|effects\|summary\|hazards] [--format llm\|llm-ids] [--guards] [--plain] [--files] [--sig] [--suppress ctors,lambdas\|none] [--async] [--include-delivery] [--only p,…] [--exclude p,…] [--exclude-namespace pfx…] [--maxdepth n] [--limit n]` | Call TREE (box-drawing, source-ordered, emoji per effect). **`--view`** (default `paths` = effectful paths, spine-kept): `full` = all nodes; `effects` = flat effectful-method list; `summary` = effect-count rollup; `hazards` = hazard overlay. **Synchronous by default**; **`--async`** crosses handoffs (`⤳handoff via <dispatcher> [cross_thread]`). `↺seen` marks cycle/shared-callee re-entry. **`--guards`** marks branch-gated edges with `⎇ [condition]` (control dependence; see the guards section below); composes with every view AND with `--format` (a trailing `guards` column). **`--format llm`** = compact LLM TSV (`depth name arity calls effects flags`; ASCII `effect*N`, arity-stripped names, parent implicit via depth+DFS order; truncation flags split by cause — `seen` = already-expanded-elsewhere (the redundancy signal), `depth-capped` = hit `--maxdepth`, `budget-capped` = hit the node budget (`--limit n`, default 50000)); **`--format llm-ids`** = 8-col with explicit `id`/`parent_id` + `seen:<canonicalId>` O(1) back-ref (back-ref only on `seen`; `depth-capped`/`budget-capped` carry no id). `--format llm*` composes with paths/full/effects (not summary/hazards); **`--suppress`** (default `ctors,lambdas` for llm) drops ctor/lambda rows, rolling their effects up. **Presentation:** **`--plain`** drops the box-drawing connectors (`├─ └─ │`) for pure indentation (diff-friendly, pipe-to-editor); **`--files`** appends each node's source location; **`--sig`/`--signatures`** adds compact parameter signatures (disambiguates overloads). |
| `rig callers <pat> [--roots\|--entrypoints] [--async] [--include-delivery] [--rules p…] [--maxdepth n]` | Reverse reachability. **Synchronous by default**, so a background callback has no synchronous predecessor → it surfaces as its own `--roots` origin; **`--async`** counts the registrar as reaching it via the handoff. `--roots` = no-predecessor candidates (heuristic — also surfaces unbound interface members). **`--entrypoints` = the RULE-DETECTED entry points (the `derive` set: page/action/handler + promoted handoff origins) that reach the target** — the precise "which of my real entry points touch this code", joined by declaration site. The matched TARGET (and its own lambdas) is listed under a separate "Matched nodes" section, NOT counted as a caller. `--roots` is a SUPERSET of `--entrypoints` (heuristic vs rule-detected). Under `--entrypoints` each EP carries its queryable FQN (a `↪ <fqn>` human line + trailing `fqn` tsv column) — paste that into `tree`/`reaches`, not the slash route. |
| `rig path <from> <to> [--async] [--include-delivery]` | One concrete path (BFS-shortest), with per-hop file:line + loop context. Synchronous by default; **`--async`** crosses + renders the `⤳ handoff via <dispatcher>` hop. |
| `rig impact --base <ref> --head <ref> [--structural] [--async] [--include-delivery] [--expect-no-effect-change] [--no-cache] [--no-gate] [--limit n] [--format tsv]` | **Two-store diff**: the entry-point + per-EP effect/reach changes between two indexed commits (it diffs the two STORES — no git working-tree diff, no `--repo`). **BOTH `--base` and `--head` REQUIRED** (each resolves a ref→sha→store via `--store`-style refs; no LATEST defaulting). (1) changed methods (FILE-granular over-approx), (2) affected entry points by deployed service, (3) behavioral delta = effects/observations reachable from the changed set, head vs base (`+`/`-effect`, `+/-observation`; param-free keys → formatting/signature-immune). The **per-entry-point effect-set diff is the DEFAULT** (surfaces path-masked deltas + relocations); **`--structural`** expands the structural-only summary into the full reach-changed EP list; **`--expect-no-effect-change`** = CI gate (exit 1 on any effect-set change). Needs BOTH commits indexed. See SKILL.md. |
| `rig effects-diff <a> <b> [--only p…] [--label s] [--format tsv]` | Symmetric difference of the forward-reachable EFFECT-SETS of two entry points — what one touches that the other doesn't. `--only <provider[:op]>` (repeatable) scopes it (e.g. write-set divergence `--only llblgen:write --only llblgen:bulk_write --only llblgen:delete`); `--label` names the pair in output. EP-vs-EP within one store (vs `impact`, which is commit-vs-commit). |
| `rig dispatch-fans [--top n] [--cause absent-receiver\|base-typed-receiver\|external-or-unbound\|type-parameter] [--format tsv]` | Diagnostic: dispatch hubs whose receiver did NOT narrow the CHA fan-out, ranked + classified by cause — the residual over-approximation surface (a calibration aid for receiver narrowing, not an everyday query). |
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
- **`--guards` appends ONE trailing `guards` column** to every format (`tsv`, `llm`, `llm-ids`, all views):
  the control-dependence condition gating that node's reaching edge (e.g. `result == Yes || result == No`),
  empty for a must-run edge. A DEDICATED last column, not a `flags` token, because a condition can contain
  `||` which would collide with the `|`-joined flags. Schema-stable when the flag is absent (no column added).

**Commit-scoped stores + `--store`.** `index` writes a per-commit store `.rig/<short-commit>/` (`-dirty`
when the work tree is dirty, `ts-<stamp>` off-git) + a `.rig/LATEST` pointer; reads default to LATEST. Every
read command (`reaches`/`tree`/`path`/`callers`/`derive`/`graph`) and `impact --base` accepts **`--store
<ref>`** (aliases `--commit`/`--at`) to target a specific store by store-id or commit-sha prefix — hold two
commits side by side and diff. A pre-layout flat `.rig/rig.db` is still read (moved to `.legacy.bak` on the
next index).

DocID shapes: `M:Ns.Type.Method(ArgTypes)`, `T:Ns.Type`, `!:Name` (error type — a reference that
failed to bind). Generic: open form `Foo\`1`, instantiated `Foo{T:Ns.X}`.

### Pattern matching — substring, but EXACT MATCH WINS

The `<pat>`/`<from>`/`<to>` argument resolves to call-graph nodes via the SAME matcher for every traversal
(`tree`/`reaches`/`callers`/`callers --roots`/`path` roots **and** the `path` target):

- **Default = case-insensitive SUBSTRING** over node DocIDs — use the DOTTED form (`Type.Method`), the
  convenient way to name a method without its namespace or signature.
- **EXACT MATCH WINS.** If the pattern exactly equals a node's **full DocID** (`M:Ns.Type.Method`) **or its
  `M:`-stripped, param-free FQN** (`Ns.Type.Method` — the form `rig` renders, see below), ONLY the
  exact-matching node(s) are seeded; the substring set is used **only as a fallback** when there is no exact
  hit. Generic arity is kept (`Ns.Type.WorkflowPaneBase\`1.Save`), so paste the FQN verbatim. Case-insensitive,
  same as the substring pass; multiple exact hits (overloads sharing a param-free FQN) all seed.
- **Why it matters — prefix twins.** Substring alone dragged in every member a name is a PREFIX of: the full
  FQN `MedDBase.Pages.Appointment.Search.Search.Proceed` substring-matched BOTH `…Search.Proceed` AND
  `…Search.ProceedToConfirmationScreen`, so `rig tree <fqn>` rendered a spurious SECOND root. Exact-match-wins
  resolves the full FQN to exactly `Proceed`; `ProceedToConfirmationScreen` only appears where it is genuinely
  reached (as a deeper callee), never as an extra root. A **partial/short** pattern (`Search.Proceed`, `Save`)
  never equals a full namespaced FQN, so it keeps the substring behaviour (matches both twins) — exact-wins is
  strictly a refinement, not a behaviour change for partials.
- **AMBIGUITY IS DISCLOSED (stderr).** When a pattern resolves to >1 **distinct** symbol (same method name on
  different types — overloads don't count, they share a param-free FQN), every traversal command prints
  `note: pattern 'X' matched N distinct symbols (…) — results span ALL of them; qualify the pattern to narrow.`
  The results are the UNION across all matched targets (a tree forest with mixed roots, a merged caller/reach
  set) — when you see the note and meant ONE symbol, re-run with the qualified `Type.Method`/FQN. On stderr so
  `--format tsv/llm` stdout stays machine-clean; an LLM driving rig should treat the note as a signal to
  re-query qualified before trusting the merged answer.
- **Use the FQN, not the EP route.** The `▶` / `callers --entrypoints` EP lines print the slash-form ROUTE
  (`Appointment/Search/Search.Proceed`), which matches NOTHING as a pattern. `derive`/`entrypoints`/`callers
  --entrypoints` now ALSO print the queryable FQN — a `↪ <fqn>` line in human output and a trailing `fqn`
  column under `--format tsv` (`impact` already labelled its EP cards with it). Copy THAT FQN; exact-match-wins
  then resolves it to precisely that member. (The FQN line/column is suppressed when it equals the route —
  e.g. class-inheritance EPs, whose route already IS the FQN — and falls back to the route for sites with no
  indexed method symbol, like ctor-less pages and promoted handoff origins.) Unexpected empty / "No path" /
  "0 call edges" → suspect a route-form pattern first.

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
also reachable synchronously). `--async` is uniform across `reaches`/`tree`/`path`/`callers`/`impact`.

**Three traversal modes (`FactPathFinder.TraversalMode`, gate = `FactPathFinder.CutsHandoff`).** `SyncCut`
(default) cuts every handoff. **`AsyncExact` (the `--async` default)** crosses sound handoffs but CUTS
publish→consumer DELIVERY FAN-OUT — edges where `AddDeliveryEdges` joined a raise to MANY same-symbol
subscribers (`CallEdge.DeliveryPrecision == "fanout"`), a symbol/name join with no instance identity that
links unrelated callers to unrelated handlers (see docs/FIX-event-raise-overapproximation.md). **`AsyncInclude`
(`--async --include-delivery`)** crosses everything incl. the fan-out. Single-subscriber delivery is stamped
`"exact"` and IS walked under AsyncExact (unambiguous). The `event_cycle` deriver reads `CallEdges` directly,
so it always sees every delivery edge regardless of mode. Invariants the SQL path holds: `CHA-oracle(sync) ==
SQL(Kind<>'handoff')`; the bounded SQL load is the **async-INCLUDE superset** (it pulls all handoffs incl.
fan-out), and the in-memory `FactPathFinder` is the arbiter that prunes fan-out for AsyncExact — so
`AsyncExact ⊆ AsyncInclude ⊆ bounded-load`, `sync ⊆ AsyncExact`. **`dead`
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

## Control-dependence guards (`--guards`)

`tree --guards` partitions an EP's reachable calls into the **must-run spine** vs the **guarded shell**, from
a CFG control-dependence analysis frozen at INDEX onto each call site (`ReferenceFact.EnclosingGuards`).

- **Render**: a branch-gated edge shows `⎇ [condition]` beside the node (the analog of `🔁[loop]`); a must-run
  (unconditional-in-its-caller) edge shows nothing — so the glyph-free frames are what runs on EVERY
  invocation. In `--format tsv|llm|llm-ids` the condition is a trailing `guards` column (empty = must-run).
- **The condition is the FULL source predicate**, reconstructed from the lowered CFG: a short-circuit
  `a || b` renders whole (not split into `a`/`b`), an else-arm is negated `!(…)` (De Morgan handled), and the
  `foreach (x in C)` MoveNext guard is dropped as redundant with `🔁[x in C]`. Multiple DISTINCT decisions
  (a loop condition + an inner `if`) `&&`-join.
- **INTRA-METHOD only (don't over-read it).** A guard says the call is gated WITHIN its direct caller — NOT
  whether that caller itself always runs from the entry point. The cross-method "always-runs-from-EP"
  composition (AND-folding guards along the EP→…→call chain) is a DERIVE-side follow-up, not yet built.
- Built always-on at index (scoped to bodies with references); ~no measurable extraction cost (MedDBase
  extract held ~29s with it on). A store indexed by a pre-guards `rig` shows no guards until re-indexed.

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

- **`rig index` builds internally — no external pre-build.** One bare `rig index <sln>` call runs a
  parallel, cached design-time build itself then extracts (defaults are sane; `--parallelism`/
  `--reuse-build-cache` are tuning knobs, not required); the old "MSBuild first" step is obsolete. `index`
  is the sole extraction command. **Prefer the full solution**: `index --from <csproj>` (the entry-scoped
  closure, the old `mine`) follows ProjectReference edges only — solution projects consumed as binary/paket
  references fall outside the closure and their internals are absent from the store, while query-side rules
  still tag their APIs at call sites, so the store looks complete without being traversable into them.
  Never run two `index` commands against one clone at once.
- **`rig index` REPLACES atomically** (temp + rename) — re-running a standalone `index` cleanly overwrites;
  no need to delete `.rig` first. Only `index --identity` (multi-solution accumulate) APPENDS in place.
- **Multi-solution unified store — `rig index <sln> --merge`.** A repo with many solutions (MedDBase has
  ~20) is indexed into ONE queryable store by running `index` once per solution with `--merge` (accumulate
  into the existing store) instead of replacing. The store is **commit-scoped**: every solution from the same
  working tree maps to the same `<shortsha>-dirty` store dir, so all merges land together; `rig runs` then
  lists **one run per solution** under that store, and `tree`/`reaches`/`callers`/`derive` span all of them.
  Mechanics + gotchas:
  - **`--merge` ≈ `--identity` auto-derived from the commit** — you usually don't pass `--identity` by hand;
    `--merge` appends a new run keyed to the current commit. (`--identity` is the lower-level "append under
    this explicit id" knob, used by the legacy `mine`.) Re-merging the same solution updates its run.
  - **Pass `--rules <ruleset>.json` on EVERY merge** (same INDEX-time rules asymmetry as above) — else that
    solution's DI/XML-DI come back empty.
  - **Cross-solution assembly divergence is tolerated, not fatal**: when two solutions compile the same
    assembly (e.g. `MMS.Standard`) with different content, index WARNs `possible fork; keeping latest` and
    continues — it does not abort the merge. Worth a glance in the log; "keeping latest" can mask a real fork.
  - **Bulk-merge resiliently**: loop the solutions with continue-on-failure + per-solution logging. One
    `index` at a time per clone (never two concurrent against one working tree).
  - **The common failure is `DegradedBuildException` ("design-time build produced 0 source files after 3
    attempts")** — rig REFUSES to index a project whose DTB yields no source (it would write absent types +
    mis-bound dependents = a corrupt index) rather than silently degrade, and **aborts the WHOLE solution**
    if any one project degrades. Causes, in order of likelihood:
    1. **Un-restored solution.** For MedDBase the restore tool is **Paket**, not NuGet — `.paket/paket.exe
       restore` at the repo root (a `msbuild -t:Restore` / `dotnet restore` returns rc=1 here). Then MSBuild.
    2. **Legacy net48 ASP.NET web projects** (`<TargetFrameworkVersion>v4.8` + `UseIISExpress` +
       `Microsoft.WebApplication.targets`, e.g. `MedDBase.Site`, `ContractManagement.Site`) yield **0 source
       via Buildalyzer DTB even after a clean Paket-restore + successful MSBuild + `--no-build-cache`** — the
       web build targets don't surface `Compile` items to Buildalyzer. These index ONLY through an
       MSBuild-built + entry-`--from <csproj>` pipeline, or must be **excluded** from the merge. Their
       `.aspx`/web layer is entry points, not data-access, so they rarely matter for effect/hazard detectors.
    3. **`.sqlproj` DB-model solutions** (`ChambersDbModel`, `Permissions-db-model`) — not C#, never indexable.
    (On MedDBase's ~20 solutions: ~half merge clean after Paket-restore; the net48-web + sqlproj ones don't.)
  - **`--from <csproj>` whose closure resolves to 0 buildable C# projects CRASHES** with an unhandled
    `IndexOutOfRangeException` (`SolutionSourceLoader.LoadAsync`) instead of a clean "nothing to index" —
    known rig defect; don't read the crash as a store problem.
  - **Why bother**: a single-solution store silently makes every entry point in the OTHER solutions invisible
    and can turn real cross-solution callers into apparent dead code / phantom-only reach. Index all the
    application solutions before trusting reachability/dead-code/`callers --entrypoints` across the product.
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

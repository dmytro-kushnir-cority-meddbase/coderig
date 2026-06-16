---
name: rig
description: Drive the `rig` fact-based .NET code-intelligence CLI to trace entry-point→effect call graphs, do reverse reachability ("which entry points reach X"), inventory effects (DB/cache/object-store/throw/messaging/HTTP), find unreachable/dead code, and ground-truth-validate static-analysis findings across multi-project solutions. Use when analysing a .NET/C# codebase's call graph or side effects, answering "what does this reach / who reaches this", auditing a migration's blast radius, finding dead code, or when the user mentions rig, coderig, the `.rig` index, `rig derive/reaches/tree/callers/dead`, or effect/entry-point detectors.
---

# rig — fact-based .NET code intelligence

`rig` extracts rule-agnostic **facts** (declared symbols, references, type relations) from C# source
into `.rig/rig.db` (SQLite), then answers call-graph and side-effect questions over those facts —
entry-point-independent, cross-project, with no Roslyn re-run for query/rule changes. Tool repo:
`C:\git\coderig` (global dotnet tool `rig`).

## Two-phase model

1. **Extract** (`index` / `mine`) — runs Roslyn once, writes facts. Expensive. Re-run only when source changes.
2. **Query / derive** (`derive`, `reaches`, `tree`, `callers`, `path`, `dead`, `refs`, `symbols`) —
   read-only over the facts DB. Detectors are **data** (JSON rules), so new rules = new answers, no re-extract.

**Every command except `index`/`mine` runs from the directory that holds `.rig/`** (the cwd is how rig
finds the DB). `--rules <path>` (repeatable) cascades extra rule files over the builtin set.

## Quick start

```bash
# Extract (from anywhere; pre-build the target first — see Gotchas)
rig index path/to/Solution.slnx                 # whole solution: cross-project source in one run
rig mine Solution.slnx --from Entry.csproj --parallelism 1   # BFS down the dep graph of Entry

# Query (cwd MUST contain .rig/)
rig reaches "Type.Method"                        # effects reachable from an entry point (synchronous; handoffs NOT crossed)
rig reaches "Type.Method" --async                 # ALSO walk async handoffs → ⚡cross_thread scheduled reach in a separate bucket
rig tree "Type.Method"                            # call tree (default: synchronous paths that hit an effect; --full; --async to cross handoffs)
rig tree "Type.Method" --effects --maxdepth 3     # COMPACT: only effectful methods, no skeleton (escape the 10-screen tree)
rig tree "Type.Method" --exclude throw            # drop a noisy effect class (see Effect filtering)
rig tree "Type.Method" --raw                      # bypass codebase render rules (see Tree render rules)
rig tree "Type.Method" --no-cache                 # bypass the .rig/cache.db forest+effects cache (see below)
rig callers "Type.Method" --roots                # reverse: no-predecessor candidates that reach it (heuristic; a background callback shows as its OWN root)
rig callers "Type.Method" --entrypoints           # reverse: the RULE-DETECTED entry points (the `derive` set) that reach it — precise, no unbound-interface noise
rig path "From.Method" "To.Method" [--async]     # one concrete path between two symbols (synchronous; --async renders the ⤳ handoff hop)
rig derive                                       # re-derive ALL effects + entry points from facts
rig dead --root "App.Main"                       # unreachable first-party methods (report-only)
rig refs "IFoo" / rig symbols "Foo" --kind method
```

Patterns are case-insensitive substring matches over DocIDs (`M:Ns.Type.Method(args)`) — i.e. the
**dotted** form (`Type.Method`). The `▶` entry-point lines and `callers --entrypoints` print the EP
**route** (e.g. `AI/SmartLetter.SendMessage`, with a folder/slash); querying with that route form matches
**nothing**. Strip the route to the dotted DocID (`SmartLetter.SendMessage`). If a query unexpectedly
returns "No path"/empty or `tree` shows "0 call edges", suspect a route-form pattern first.

## Reading effect output (don't misread the counts)

`reaches`/`tree`/`derive` annotate methods with effects, each prefixed by an emoji (💾 write, 🔍 read,
📥 fetch, ☎️ soap, 🌐 http, 📤 queue, 📣 echo, 📡 eventbus, 🗃️ cache, 📦 object-store, 📁 io, ⚠️ throw,
✅/↩️ tx). Override the glyph map per-repo with `rig.effect-emoji.json` (`{"llblgen:write":"💾",...}`).

- **An effect listed N times = N static call-sites that reach it (branches included), NOT N runtime
  writes.** An insert-vs-update method shows the same write twice (one per branch); only one fires per
  call. Read occurrences as "places in code," never as execution multiplicity.
- **`~heuristic` marks an INFERRED dispatch hop.** Virtual/interface dispatch is resolved from EXACT
  Roslyn-mined facts (unmarked — trust them); where Roslyn couldn't bind (net48 `!:` partial binding)
  rig falls back to name/arity matching and marks the hop `~heuristic` (tree: `«impl-dispatch
  ~heuristic»`, path: `[impl-dispatch (heuristic)]`, reaches: `~heuristic` suffix; TSV: trailing
  `dispatchBasis` column). ~99% correct — verify before relying on such a path.
- **Dispatch fan-out is an OVER-APPROXIMATION, not a confirmed call.** A virtual/interface call resolves
  to a concrete runtime method (**one hop**); CHA can't pick the runtime type, so it lists ALL impls.
  `reaches` segregates targets reached ONLY via that fan-out into a separate **"dispatch fan-out (NOT a
  real call)"** bucket; `tree` tags them `«impl-dispatch ×N fan-out»`. Read `×N` as "could be any of these
  N," never "calls all N." A resolved target is NOT re-dispatched (dispatch is one hop) — so an impl
  reached via the interface won't drag in its base method's *other* overrides.
- **`tree` children are in source order** (call-site line ≈ eager-inline execution order), deterministic.
- **Generic labels show the REAL instantiation, not `<T, U>`.** When a node is reached from a concrete
  entry, `tree` monomorphizes the declaring-type AND generic-method args down the call chain —
  `QueryPipeline<PersonDataFieldDefinition, PersonDataFieldDefinitionColumn>.Create<DefinitionAndRangeDto,
  …>` instead of `QueryPipeline<T, U>.Create<T, U>`. Works through static factories, generic methods, and
  lambda bodies (`skip: i => Create(…)`). A position stays a placeholder (`T`/`U`) only when its type is
  genuinely unknown on that path — no concrete entry pinned it, or it crosses an **impl-dispatch** hop
  (interface→impl dispatch carries no type binding). The arity is always real (`Foo\`2` → `<T, U>`).
- **Effect filtering** — `--only <list>` keeps just those, `--exclude <list>` drops them (exclude wins).
  The list is comma- **or** whitespace-separated and repeatable; tokens match `provider` (`throw`) or
  `provider:operation` (`llblgen:read`). Headline use: **`--exclude throw`** to hide exceptions.

## Async handoffs — SYNC-CUT by default; `--async` to include them

rig is a synchronous reachability tool. A delegate handed to a dispatcher to run LATER / on another
thread (a background/timer/actor/event scheduler) is an async **handoff**, NOT a plain call — a distinct
`handoff` edge KIND. By **default it is CUT** (never conflated with a synchronous call), and `--async`
walks it, tagged. Consequences:

- **`reaches`/`tree`/`path`/`callers` default to sync-cut**: a registration like
  `new RepeatingBackgroundProcessSchedule(ts, ProcessHealthcodeQueue, ..)` does NOT make the registrar
  look like it runs `ProcessHealthcodeQueue` — the callback's DB/SOAP effects are absent from the
  registrar's synchronous reach. Per-entry effect inventories and `callers --roots` origins are the
  trustworthy synchronous ones; a background callback surfaces as its OWN origin.
- **`--async`** walks handoff edges, tagged. `reaches --async` splits output into **direct** (real call
  paths), **async (scheduled)** (⚡, reached across a handoff — `⤳ via <dispatcher>`), and **dispatch
  fan-out**. `tree --async` marks the hop `⤳handoff via <dispatcher> [cross_thread]`; `path --async`
  renders it. Use it to ask "what does this scheduler eventually cause", knowing it's cross-thread.
- `rig derive` lists classified handoffs by kind (background/timer/actor/event) with their dispatcher +
  registration site, and collapses the unclassified-methodGroup residual to a count (independent of the
  traversal default).
- **Dispatchers are rule DATA** — the `handoffDispatchers` section in `rig.rules.json` (per dispatcher:
  `consumerPatterns` matched against the consuming ctor/method DocID, an EP `kind`, a `repeating` flag).
  A method-group is classified a handoff only when a curated dispatcher consumes it (recall rail:
  unmatched method-groups keep synchronous semantics; `dead` keeps ALL method-group/handoff targets as
  roots regardless). Re-run `rig graph` after editing dispatchers (no re-index needed).
- **Limits**: classification is co-location-based (the dispatcher consumes the method-group as a
  same-line argument); BCL dispatchers (`Task.Run`) and lambda callbacks fall into the unclassified
  residual. Interleaving/ordering/races are out of scope — rig tags, never orders.

## Tree render rules — codebase-specific abstraction (data, not heuristics)

A `rig tree` can explode not because of app branching but because of a *codebase-specific* seam where
static analysis loses precision — e.g. a reflection/string-keyed service-locator (one virtual call CHA
must fan out to ALL impls) or an ORM's generic entity-construction factory. These blow-ups are a
property of the codebase, so the abstraction is **rule DATA**, never a baked-in degree threshold (no
"collapse if fan-out > N"). The `render` section in `rig.rules.json` (cascaded via `--rules`) carries it;
ships EMPTY, so a codebase with no curated render rules always sees the raw exact tree.

- **`collapseSeams`** (`{pattern, label}`) — `pattern` is a DocID substring matching a fan-out HUB. Its
  candidate children are folded into ONE summary leaf: the de-duped **union of effects** reachable
  through them + a hidden-line count (`⋯ N dispatch targets collapsed [seam: <label>] {effects…} (+M
  lines hidden)`). The reach is untouched — this is presentation only.
- **`opaqueTypes`** (`{pattern, label}`) — a node matching the type/namespace pattern is drawn as a
  leaf (`«opaque: <label>»`): its own effects still print, its subtree does not. For framework/infra
  you have source for but rarely need to read (e.g. the Echo actor runtime, an ORM's support classes).
  The pattern matches the DECLARING type only (the DocID with its parameter list stripped), so a
  namespace pattern like `M:Echo.` hits methods declared in `Echo.*` but NOT an app method that merely
  takes an `Echo.ProcessId` parameter. Anchor with `M:` for a namespace (`M:Echo.`); a bare type name
  (`LinqMetaData`) matches anywhere in the declaring type.
- **`rig tree --raw`** bypasses ALL render rules to print the exact unfiltered tree. Use it to expand a
  collapsed seam, or to confirm the rules aren't hiding something you need.
- Render rules NEVER affect `reaches`/`callers`/`path`/`dead` or the reach itself — only what `tree`
  draws. (MedDBase example: `IService.Startup` ×113 service-locator + `Construct\`2.New` ×49 entity
  registry collapse a depth-32 `InvoiceMain.MatchPayments` tree from 1970 → ~106 lines.)

## Extending effect coverage — add a detector (data, query-side, NO re-index)

Effects are DATA. To tag an outside-world call rig misses, add an entry to the `effects` array in
`rig.rules.json` (cascaded via `--rules`), then re-run `rig derive`/`reaches`/`tree` — **no re-index**
(effects are query-side; only `FactExtractor` changes need re-indexing). Schema (full in REFERENCE):
`{ provider, operation, methods:[…], receiverTypes | declaringTypes:[…], resource }`.

- **Match the API at its FIRST-PARTY call site — even for an external library.** The socket/IO call lives
  in the dependency (e.g. `StackExchange.Redis.IDatabase.HashGet`, `System.Web.HttpResponse.Write`), but
  the call SITE is first-party and the effect keys to the enclosing first-party method. So `receiverTypes`
  can name an external type; rig still tags it where your code calls it. (Anchor at the real boundary, not
  a deep wrapper — e.g. tag the Redis API, not just the Echo `ICluster` façade.)
- **`resource: "argument_name"`** captures the first arg (the key/channel/setting name) as the resource —
  high-signal (`config:read OpenAiKey`, `redis:read <key>`). Other strategies: `receiver_type`,
  `declaring_type`, `argument_type`, `type_argument`.
- **Verify live**: `rig derive --format tsv | awk -F'\t' '$1=="effect"{print $2}' | sort -u` (providers),
  then `rig reaches <method>` — but **derive totals ≠ reachable**: an effect surfaces from a caller ONLY
  if its enclosing id is a **call-graph node** (a method/accessor/lambda/ctor `M:`/synthetic id). An effect
  keyed to a property (`P:`)/field (`F:`) id shows in `derive` totals but never in `reaches`/`tree`.
- A detector matches by method NAME + receiver/declaring type, so a same-named method on an unrelated type
  can mis-fire — gate with the tightest `receiverTypes`/`declaringTypes` that still covers the real sites.

## Core workflow — answer a reachability/effect question

1. **Confirm the index is healthy**: `rig runs` (entrypoints/effects/symbols counts). A sane Pages-scale
   index has thousands of EPs + effects; near-zero EP/effect with healthy symbols = base-type-chain binding flake (see REFERENCE).
2. **Forward** ("what does X do/touch"): `rig reaches X` (effects) / `rig tree X --full` (call tree).
3. **Reverse** ("who/what entry points reach X"): `rig callers X --roots`.
4. **Specific path**: `rig path X Y`.
5. **Effect/EP inventory**: `rig derive [--rules extra.json]`.
6. **Dead code**: `rig dead` — see below.

## Deployment attribution — which service hosts an entry point (`deployments.json`)

Optional and opt-in: drop a `deployments.json` next to `.rig/` and every command that renders an entry
point annotates it with the deployed **service(s)** whose process loads it. Absent the file, output is
unchanged.

- **Config** (`deployments.json`): `{ "services": [ { "name", "host": "<entry csproj, relative to the
  solution dir>", "kind": "iis|kube|exe|…", "provides": ["<token>", …]? } ] }`. JSONC (comments + trailing
  commas OK). Seed it from the build's own artifact manifest (e.g. a NUKE `Build.Artifacts.Spec.cs`),
  then curate. `kind`/prod-topology is a hand-maintained overlay — the build manifest knows the csproj,
  not which app-pool/region actually runs it.
- **How it maps**: each service's entry csproj → transitive `<ProjectReference>` closure (via the indexed
  solution); an EP's source file → its owning csproj → the service(s) whose closure contains it.
- **loaded-in vs active-in (capability gate)**: closure membership is "code is *loaded* in service X", an
  **upper bound** — shared libs fan out to every referencing host. To refine to *active-in*, a service
  declares opaque tokens it `provides` and a rule (`handoffDispatchers` / entry-point rule in
  `rig.rules.json`) declares tokens it `requires`; an EP is active-in a loaded service iff
  `provides ∩ requires ≠ ∅` (ANY). No `requires` ⇒ ungated ⇒ active wherever loaded (output unchanged —
  the gate is opt-in). Tokens are opaque to rig (a deployment convention, e.g. a startup-set id).
- **Rendering** (the ▶ custom EP line): `▶ <kind> <route>  ⟦MedDBase (iis)⟧`, with a dim
  `· N linked-inactive` delta when the EP links into hosts it is gated out of, or `⟦N svcs: A, B, C +k⟧`
  for a multi-host fan-out. Appears in `derive` (+ a per-service active-in summary, + trailing `service` =
  loaded and `activeService` = active columns in `--format tsv`), `callers --entrypoints`/`--roots`,
  `tree` (root AND any EP node in the body), and the `reaches`/`path` From line.
- **Out of scope**: a single rule gates all its EPs identically — a runtime `if` inside one registrar that
  starts some actors only on one host is not expressible by rule alone; cluster routing / lazy spawn too.
  Confirm against config/logs.

## Query cache (`rig tree`)

`rig tree` caches its computed forest + effects in a separate `.rig/cache.db` (the main `rig.db` is opened
read-only). A repeat query skips the traversal + effect derivation and only re-loads the graph to render —
identical output, lower latency. **Auto-invalidated on reindex:** the key embeds the rule fingerprint + a
store identity (`rig.db` size/mtime) that `rig index`/`rig graph` change, and stale rows are purged on
open, so you never see a result from an old index. It's best-effort (any failure silently recomputes).
Pass `--no-cache` to bypass it (e.g. when benchmarking). Safe to delete `.rig/cache.db` anytime.

## Finding dead code (`rig dead`)

First-party method not reachable (forward, incl. dispatch) from any root = candidate. Roots = derived
entry points + handoffs + `Main` + test methods. **Report-only** — confirm against the C# compiler
(IDE0051/CS0169) or by reading source before removing; facts can't see reflection/DI/serialization.
- Tiers: private-uncalled = **High** (act on these), internal = Medium, public/protected = **Low** (likely API/reflection; hidden unless `--all`).
- `--root <pat>` seeds roots facts can't see (top-level `Program.Main` is synthesized → invisible; reflection hosts).
- `--lib` = library mode (public/protected become roots). `--include-dispatch` also flags unreached overrides/virtuals.
- A dead method with 0 callers = removable cluster root; >0 = reached only by other dead code.

## Validate a finding (the ground-truth loop)

**Never trust the mined DB as ground truth — it is the fallible Roslyn pass's output.** Validate against
(a) a synthetic fixture (truth by construction) or (b) the actual source. Loop: pick methods → trace
from SOURCE by hand/subagent → replicate with rig (`callers --roots`, `reaches`, `tree`) → diff → fix the
**detector/rule** (not the test) → re-run. Use the SHIPPED engine (`rig reaches/path`), never a BFS reimpl.

## Top gotchas (full list in REFERENCE.md)

- **Pre-build the target before `index`/`mine`** — design-time builds don't emit DLLs; cross-assembly metadata won't bind otherwise.
- **`mine` at `--parallelism 1`** (>1 can corrupt/deplete shared DLLs); a 100+-project mine depletes net48 bins — rebuild the entry project before any later single-project index.
- **`rig index` APPENDS** (no run dedup) — delete `.rig` before re-indexing the same project, or facts double.
- **Index with the published global `rig`; query with anything.** A plain Debug build can't run the Roslyn workspace (missing MEF deps).
- Detector results are only as good as the rules + what's in scope — see the **fundamental static-analysis limits** in REFERENCE.md before trusting an effect/EP count.

See **[REFERENCE.md](REFERENCE.md)** for: full command reference, indexing semantics (index vs mine vs
solution), the rule/detector model + detector families, recall behaviour (dispatch + dead-code), the
fundamental limits, and env gotchas.

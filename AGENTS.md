# AGENTS.md — working notes for `rig`

`rig` is a fact-based .NET code-intelligence CLI. Product overview, the full command table,
effect observations, and deployment attribution live in [README.md](README.md). Vocabulary:
[docs/ubiquitous-language.md](docs/ubiquitous-language.md). Current docs navigation:
[docs/README.md](docs/README.md). Work tracking: [docs/backlog/](docs/backlog/README.md).
This file is only the things that aren't obvious from those and that you'd otherwise re-derive.

## Current state — 2026-07-18

- `main` is synchronized with `origin/main` at `e091bff4` (`feat(index): select target framework explicitly`).
  The July 15–16 indexing work made multi-target projects deterministic: indexing prefers the first declared
  TFM by default and `rig index --framework <tfm>` can select one explicitly. `callers --format tsv` also
  now honors `--include-reverse-only`.
- The product is well past the original CLI-only MVP. The load-bearing path remains
  Roslyn/MSBuild extraction → immutable SQLite facts → query-time derivation/reachability, but the CLI now
  also exposes `serve` (interactive web explorer), `impact`/`effects-diff`, hazard views, deployment
  attribution, per-commit stores, and assembly-reference usage. Treat README's command table and
  `Rig.Cli/CommandLine/Root.cs` as the current surface, not archived milestone docs.
- Work is tracked as files in `docs/backlog/{todo,progress,done}/`; the old `docs/progress.md` and
  `docs/handover.md` no longer exist. There are currently two partial items in `progress/`:
  `aaa-web-timing-unification-ui.md` (only finer impact timing phases remain) and
  `rules-only-effect-gaps.md` (only the low-priority `*Inbox` suffix-rule support remains).
- Important known gaps: `dead` is deliberately unregistered until it uses the same one-hop dispatch engine
  as forward traversals; `--include-tests` can abort after a degraded build; one flaky generated
  ClientPage-proxy extraction test is skipped; and multi-TFM indexing selects one framework rather than
  unioning all target-framework source sets. See the corresponding files in `docs/backlog/todo/` before
  picking up any of these.
- The primary real-world calibration target is MedDBase. Its current indexed store/config is
  `C:/git/meddbase-analysis`; its source checkout is `C:/git/meddbase-main-application`. Query from the
  analysis directory so rules, deployments, and `.rig/` resolve together.
- Before starting work, run `git status --short --branch` and inspect `docs/backlog/progress/`. This snapshot
  is orientation, not a replacement for current Git state or the backlog cards.
- `UseNamedArgs 0.0.3` is not on NuGet.org (0.0.2 is the nearest public version). Until upstream publishes
  the required changes, the analyzer source is pinned as the `tools/use-named-args-fs` submodule from
  [`dmytro-kushnir-cority-meddbase/use-named-args-fs`](https://github.com/dmytro-kushnir-cority-meddbase/use-named-args-fs),
  branch `feature/exclusion-config`, commit `f62fa418`; its nuspec declares `0.0.3` and adds the
  `UseNamedArgs.exclude_*` configuration used by this repo. It is proposed upstream as
  [`mykolav/use-named-args-fs#1`](https://github.com/mykolav/use-named-args-fs/pull/1), which is still open
  with four commits and no review as of 2026-07-18. Upstream is
  [`mykolav/use-named-args-fs`](https://github.com/mykolav/use-named-args-fs) and only publishes `0.0.2`.
  `Directory.Build.props` references the pinned analyzer project directly. After the PR is merged and a
  suitable package is published, delete the submodule and return to a private `PackageReference`.

## The `rig` skill — source of truth is THIS REPO

The canonical `rig` skill lives in-repo at **`.agents/skills/rig/`** (`SKILL.md` + `REFERENCE.md`) — version
it here, update it here when CLI surface/flags change. The globally-INSTALLED copy at
`~/.codex/skills/rig/` is **read-only and DISPOSABLE**: never hand-edit it (edits get clobbered on reinstall
and drift from the repo). Edit the repo copy, then **install via a CLI copy**:

```pwsh
# pwsh — REMOVE the dest first, then copy. Copying onto an EXISTING dir NESTS (creates
# ~/.codex/skills/rig/rig/ and leaves the top-level SKILL.md/REFERENCE.md STALE) — silent footgun.
$d = "$env:USERPROFILE/.codex/skills/rig"; Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; Copy-Item -Recurse -Force .agents/skills/rig $d
# or bash:  rm -rf ~/.codex/skills/rig && cp -rf .agents/skills/rig ~/.codex/skills/
```

(There's no native `codex skill install`; the copy IS the install. `--plugin-dir`/`--plugin-url` load a
plugin for one session only.) **Always delete the dest dir before copying** — `Copy-Item`/`cp -r` onto an
existing `~/.codex/skills/rig/` nests a `rig/` subdir inside it and the top-level files go stale; verify
after with e.g. `Get-ChildItem ~/.codex/skills/rig` (should list ONLY `SKILL.md` + `REFERENCE.md`, no
nested `rig/`). If the installed skill ever looks stale, reinstall — don't patch it in place.

## Orchestration — director → orchestrator → coding agents

> **Role check — read FIRST.** This section describes the **orchestrator** (the top-level session the user
> talks to). **If you are a DISPATCHED CODING SUBAGENT** (you received a single scoped task prompt, not a
> conversation with the user), this loop is your CALLER's job, NOT yours: you are a **leaf worker** —
> implement your one task directly and do **NOT** dispatch further subagents, gather "context", or re-run the
> orchestration loop. Ignore the rest of this section; follow your task prompt.

The effective workflow here for multi-step work is a LOOP. The USER directs (goals, the load-bearing
calls, course-correction); YOU are the **orchestrator**; SUBAGENTS do the coding. The loop:

1. **Orchestrator gathers + HOLDS the context** — reads the code, maps the design, runs the calibration
   queries. The context lives in the orchestrator so dispatching a build doesn't lose it and the review can
   happen without re-reading.
2. **Define the task + propose the architecture** — surface genuine forks; recommend, don't survey.
3. **GATE — coding does not start until either:** the user **approves the architecture in principle** (a
   fork chosen, a scope OK'd, a "go"), **OR `auto` mode is on** (approval bypass — proceed on your own
   judgement, still surfacing a fork only if it risks a false negative or is hard to reverse).
4. **Dispatch the coding to a SUBAGENT** — one scoped build, tightly prompted (below).
5. **Orchestrator REVIEWS + VALIDATES + COMMITS** — read the diff, confirm the full suite green, run the
   real-data check (the MedDBase re-graph/derive) yourself; then commit. The subagent never commits.

The rules that make this produce mergeable code, not plausible diffs:

- **Subagents NEVER commit — you review their diff and commit it.** This is the quality gate: treat an
  agent's "done, nothing else changed" as a CLAIM to verify (a real `--expect-no-effect-change` gate
  regression was caught *only* in review). Independently confirm the suite is green AND do the real-data
  validation (the agent can't touch the MedDBase store) before committing.
- **Prompt tightly or get the wrong thing.** Each agent task states: the exact problem, a PRECEDENT to
  mirror (e.g. "mirror the existing X machinery"), the EXPLICIT owned-files list, hard constraints
  (annotate-only / full suite green / **do NOT commit** / don't touch docs), the explicit ACCEPTANCE test
  (as a RUNNABLE check), a STRUCTURED report (what changed / tests added / any existing test updated), and
  the gotchas (TUnit filter syntax, named-args, NEW test file not the shared one,
  verify-assertions-against-real-`rig`-output, this list). **Do NOT ask agents to run csharpier / format
  their files** — the ship step (`mini-ci`) formats everything on publish; inline formatting is wasted effort.
- **One agent at a time on shared files** — `FactEffectDeriver`/`FactHazardDeriver`/`builtin-rules.json`/the
  derive+impact paths all contend; concurrent agents just merge-conflict. Parallel only on disjoint work.
- **Run code agents in the MAIN checkout, NOT `isolation: worktree`** — worktree isolation branches from a
  STALE base in this repo (an old `main` merge), so prerequisite files are missing. Bit us twice.
- **Green + committed before the next unit** — never let agent output stack up uncommitted.
- **Design first, dispatch second, calibrate after.** Argue the architecture before building; ship each
  detector/feature with a bug/fix fixture; FP-calibrate a new signal on the real MedDBase store before it
  goes on-by-default (a structurally-true detector that fires 179× is still noise). Surface genuine forks
  to the user; autopilot the rest.
- **Decide CLI-only vs CLI+web at design time.** rig has a web UI (`Rig.Cli/Web/RigApiEndpoints.cs` +
  `wwwroot/`). Any feature whose output is a browsable/shareable **report, ranking, diff, or graph**
  (inventories, project/assembly graphs, hazard/impact views) — as opposed to a one-shot fix or a
  detector-internal change — should get a **web slice**, scoped as an explicit follow-on. CLI-first is still
  right (the facts/logic are load-bearing; web is a view), but ASK the question at the design gate and
  **capture the web slice in the backlog item** so it isn't forgotten. (Cache note: a query-side feature that
  doesn't touch a `*Schema`/`derivationVersion` axis needs its own web cache-key thinking — see the cache
  section.)
- **Don't dispatch for small/exploratory work** — root-causing, calibration queries, one-file fixes, and
  doc edits are faster inline. Agents earn their keep only on self-contained builds with a clear acceptance test.
- **Subagent verifies test assertions against REAL output, not its imagination.** Subagents can't build (bin/
  clobber) but CAN run the installed global `rig` (read-only). Any rendering/output change → the prompt MUST
  say: "run `rig <cmd>` on a real input, paste the ACTUAL output, write assertions against THAT paste." The
  recurring review failure was agents asserting (e.g.) namespace-qualified names against `ShortName` output —
  3 dead tests in one dispatch, caught only in review. One prompt line turns a round-trip into zero.
- **Agent-authored tests go in a NEW `<Feature>Tests.cs`, NEVER the shared `CliApplicationTests.cs`** —
  concurrent agents editing the shared file clobber each other. If an EXISTING test pins old behavior the
  change breaks, have the agent FLAG it (not edit the shared file) and fix it yourself at review.
- **Capture the real-store baseline BEFORE you dispatch.** Snapshot the relevant MedDBase counts/tsv (e.g.
  `rig derive --format tsv | awk …`) while the agent works — a clean before/after A/B for recalibration, on
  the orchestrator's otherwise-idle time. (The FR-1 hazard recalibration AND the parallelise-loads revert
  both turned on a pre-captured baseline.)
- **Independent verify for trust-critical work** — for a risky detector / large diff, a SECOND fresh-context
  agent adversarially verifies the diff against the acceptance check + the real store BEFORE you commit; the
  builder rationalizes its own false positives. (The 6-agent UX panel worked because the agents were independent.)

## Build / test / ship

- **Ship flow is `scripts/mini-ci.ps1`** — csharpier **format** (in place) → `dotnet build` → all tests →
  pack → reinstall the global `rig` tool. Run it after any source change you intend to use from the CLI.
  **No need to format first / inline** — mini-ci formats the whole repo on publish as its first step (the
  repo is kept clean, so it only rewrites the files that drifted). `scripts/format.ps1 -Check` still gives a
  verify-only pass if you want one.
- **Tests are TUnit on Microsoft.Testing.Platform, not vstest.** For every focused test run, pass TUnit's
  filter after the `--` separator; `dotnet test --filter` does NOT work (prints help, "Zero tests ran"):
  `dotnet run --project tests/Rig.Tests --no-build -- --treenode-filter "/*/*/<ClassName>/*"`
  (path is `/Assembly/Namespace/Class/Test`; `*` wildcards each segment). `dotnet test` with no filter is fine.
  Do not put MSBuild switches such as `-m:1` or `--no-incremental` on `dotnet test`: MTP forwards unknown
  switches to TUnit, which rejects them. When those switches are needed, run `dotnet build ... -m:1
  --no-incremental` first, then `dotnet test ... --no-build --no-restore`.
- **`-warnaserror` is OFF** (removed from mini-ci 2026-06-22). Analyzer diagnostics — the forked
  `UseNamedArgs` (`use-named-args-fs`, fires on every multi-arg first-party call), `MA0011`
  (format-provider) — are now **non-fatal warnings**, not build errors: the build no longer fails on them
  and there's no fix-it round-trip. Still worth following for readability where cheap (named args on new
  multi-arg first-party calls; `CultureInfo.InvariantCulture` on formatting), and `.editorconfig`
  `UseNamedArgs.exclude_methods` still suppresses the noisy stdlib ones — but none of it gates the build.
  - **Don't run csharpier format CONCURRENTLY with a `dotnet build`** (format edits a file the compiler is
    reading) → spurious "N Error(s)" with no diagnostic. mini-ci avoids this by formatting as a discrete
    step BEFORE build; the trap only bites if you kick off a manual format mid-build. Re-run the build once
    it settles; verify with `--no-incremental`.

## Effect ↔ reachability model (read before touching effects or `EnclosingSymbolId`)

The pipeline is: extract facts (`Rig.Analysis/Extraction/FactExtractor.cs`) → derive effects keyed to an
**enclosing symbol id** (`Rig.Domain/Functions/FactEffectDeriver.cs`) → `reaches`/`tree --full` surface an
effect only if that enclosing id is a **node in the reachable call graph** (the join is literally
`reachable.ContainsKey(e.EnclosingSymbolId)` — see `Rig.Cli/Commands/ReachesCommand.cs`).

**Invariant: every effect's enclosing id must be a call-graph node.** Call-graph nodes are methods,
bodied property/indexer accessors, lambdas, and ctors — all `M:`/synthetic ids. Properties (`P:`),
fields (`F:`), and events (`E:`) are **never** nodes (only their bodied accessors are). So an effect keyed
to a `P:`/`F:`/`E:` id is silently orphaned from reachability — it shows in `rig derive` totals but
**never surfaces in `reaches`/`tree`** from any caller.

- `FactExtractor.EnclosingSymbolId` therefore keys an accessor-body effect to the accessor method
  (`M:get_X`/`M:set_X`), not the property (`P:X`). Expression-bodied properties (`X => Repo.Fetch();`),
  full `get{}`/`set{}` blocks, and `get =>`/`set =>` accessors all route to the accessor. Regression test:
  `Calls_inside_accessor_bodies_are_owned_by_the_accessor_not_the_property` in `FactExtractorCaptureTests`.
  (This was the 2026-06-16 fix; pre-fix, 1,280 / ~22k MedDBase effects were `P:`-keyed and invisible.
  Post-fix: 264 `P:` remain, but 261 of those are lambdas declared in property bodies whose synthetic id
  is `P:Type.Prop~λN` — cosmetically `P:`-prefixed but reachable, because the methodGroup edge into the
  lambda is now enclosed by the accessor, so the lambda node connects and node-id == effect-key.)
- Still-open analogous gap (~24 effects): **initializers** that run in the ctor — auto-property
  `{ get; } = expr` (the 3 non-lambda `P:`) and field `= expr` (`F:field`). The correct owner is `.ctor`,
  not an accessor; deliberately not fixed. Both `P:` and `F:` are never call-graph nodes.
- Quick audit after extraction changes: `rig derive --format tsv | awk -F'\t' '$1=="effect"{print substr($5,1,2)}' | sort | uniq -c`
  — `M:` is reachable; `P:…~λ` lambda ids are reachable; a non-lambda `P:` or any `F:` is orphaned.

## Two-stage design + dispatch model (read before touching dispatch)

`rig` is deliberately **two stages**: (1) Roslyn extraction → immutable facts (`rig index`), (2) fact-based
derivation of effects / entry-points / reachability with **no Roslyn** (`rig derive`/`reaches`/`tree`/`path`).
Why: iterating a detector rule against a live Roslyn compilation meant a full reload from zero state per
change (slow); `derive` over the fact store is seconds. The price of that speed: **we reconstruct
polymorphism (virtual/interface/base dispatch) ourselves at query time**, since the semantic model is gone
after stage 1. The split that keeps this honest (we are NOT reimplementing the compiler):

- **Semantic resolution is Roslyn's, frozen into facts.** `FindImplementationForInterfaceMember` /
  `OverriddenMethod` are mined at extraction into `dispatch_facts` (Basis=`roslyn`). We never re-derive
  binding/overload/inference. A `~heuristic` (name+arity CHA) edge appears only where Roslyn couldn't bind.
- **Whole-program devirtualization is ours.** The compiler resolves a call to ONE static symbol and stops;
  "which concrete methods can actually run" is a call-graph problem (CHA) that no compiler does. So this
  layer is an over-approximation we **disclose** (`~heuristic` tag, the `reaches` "dispatch fan-out (NOT a
  real call)" bucket) — not language semantics we reimplement.

**Dispatch is ONE HOP** (`FactPathFinder.Successors` `fromDispatch`): resolving a virtual/interface call
yields the concrete runtime method; that method must NOT be re-dispatched as if it were another virtual
call site — only its BODY (direct calls) is walked. Composing dispatch-with-dispatch is a category error:
e.g. `IPerformanceLogger.Startup` impl-resolves to the INHERITED `ServiceBase.Startup`, and chaining into
that base method's 31 unrelated service overrides made a single-entity cache fetch "reach" `SideBySideManager`.
One-hop mirrors a single-step Roslyn devirtualization. Scoped to the user-facing forward traversals
(`reaches`/`tree`/`path`); the receiver-blind oracle `ReachableFromAll` stays all-hops. Companion edge-level
rule in `DispatchTargets`: the mined-fact closure never crosses impl↔override kinds within one resolution.
Tests: `OneHopDispatchTests`, `MinedDispatchTests`. (`dead` is **disabled** meanwhile — it ran on the
all-hops SQL superset, which the one-hop engine no longer matches; re-enable in `CommandLine/Root.cs` once
moved onto the same engine.)

**Interface-receiver narrowing** (`FactPathFinder.Dispatch.cs` `InReceiverScope`, used by `NarrowByReceiver`):
a method declared on a BASE interface but called through a more-derived SUB-interface receiver binds to the
base member, so CHA fans to every base-interface implementer — incl. implementers of unrelated SIBLING
sub-interfaces. e.g. `IConfiguration : IPersistentState`; `config.GetItem(…)` (config : IConfiguration)
bound to `IPersistentState.GetItem` over-fanned to `WebConfiguration`/`PersistentApplicationConfiguration`/
`WebApplicationState` (sibling `IPersistent*State` impls, not `IConfiguration`). `ResolveNarrowRoot` already
returns the sub-interface as the narrow root; the gap was `NarrowByReceiver` testing membership via class
base-edges only (`InNarrowSubtree`), which never matches interface implementers — so it fell back to full
CHA. `InReceiverScope` adds the interface arm (`ImplementsInterface`), recall-safe. This is NOT new VTA — it
just completes receiver-narrowing (using the known static receiver type) to interface hierarchies. Tests:
`InterfaceReceiverNarrowingTests`.

Resist adding *further* bespoke narrowing (context-family/type-arg are hand-rolled VTA) — disclose residual
CHA imprecision instead; the principled ceiling is a real type-flow pass, which still degrades to CHA at
reflection boundaries.

## Cache invalidation — bump the schema constant when you change a derivation (read before editing a detector/effect/impact)

Derived output (tree / effects / hazards / impact) is cached on **two** layers: the server disk cache
(`Rig.Cli/Caching/QueryCacheKeys.cs`) and the web client's IndexedDB (`wwwroot/api.js`, keyed by
`/api/meta`'s `derivationVersion`). Both hedge on the SAME three axes — **store identity** (rig.db
size+mtime; a reindex shifts it), **rules fingerprint** (a `rig.rules.json` edit shifts it; no reindex
needed), and a **per-artifact schema version** — the `*Schema` int constants at the top of `QueryCacheKeys`
(`EpSchema` / `TreeSchema` / `HazardEffectsSchema` / `GraphHazSchema` / `ImpactSchema`).

**The rule: if you change the derivation LOGIC or the cached PAYLOAD SHAPE of an artifact — a detector's
classification, an effect's fields, the impact diff computation — and neither the store facts nor the rules
change, BUMP that artifact's `*Schema` constant.** That is the only signal that invalidates a warm cache;
skip it and every warm store (disk + every user's browser) keeps serving the pre-change result forever.
Reindex and rule edits are already covered by the store/rules axes — the bump is specifically for a
*same-input, different-output* logic change.

- Store, rules, and the schema constants are the WHOLE hedge. **Do NOT re-introduce the assembly MVID / a
  build timestamp / an app-version string** into any cache key — that was removed 2026-07-06 precisely
  because it changed on every recompile, so it destroyed the expensive impact diff (minutes; loads+derives
  BOTH stores) and the >1 MB client trees on any unrelated `.cs` edit. The `*Schema` bump is the deliberate,
  honest replacement.
- The client stays in lockstep for free: `QueryCacheKeys.DerivationSchemaToken()` folds in **all** the
  `*Schema` constants and feeds `/api/meta`, so bumping any one also moves the client's `derivationVersion`
  (it can never keep serving an artifact whose server schema advanced). You only touch the one constant.
- Leave a one-line `// vN->vM: <why>` trail on the constant (the existing ones do) — it's the audit of what
  each bump flushed.

## MedDBase store (the main real-world target)

- The indexed store + its config live in **`c:/git/meddbase-analysis`** (`.rig/` is ~2 GB, plus
  `rig.rules.json`, `deployments.json`). **Run every `rig` query command from that directory** — it picks
  up the rules + store + deployment map from cwd. The source it indexes is `c:/git/meddbase-main-application`.
- Re-index after any **extraction** change (effects/EPs are query-side and need no re-index, but
  `FactExtractor` changes do): from `c:/git/meddbase-analysis` run
  `rig index <MedDBase.slnx> --rules rig.rules.json` — **bare full-solution, no `--from`, no external
  MSBuild pre-build** (defaults are sane; observed ~3 min on a warm dtb cache, 2026-07-15). Do NOT use
  `--from …/MedDBase.csproj`: the entry-scoped closure follows ProjectReference only, so paket/binary-referenced
  solution projects (e.g. `src/dfs`) silently drop out — their effects still tag at call sites but their
  internals aren't traversable.
- **Index efficiently — the slow part is the monorepo BUILD, so don't repeat it:** (1) before indexing a
  commit, check `rig runs` / `.rig/<short-sha>/` — stores are commit-scoped, so if that commit is already
  indexed, **skip the build+index entirely**. (2) Index in the **primary checkout** (or a persistent
  build location) so rig's design-time-build cache (`.rig/dtb-cache`, on by default) is reused across indexes
  — a **fresh `git worktree` per index loses the cache and forces a from-scratch build of the whole monorepo**.
  Only use a throwaway worktree when the working tree genuinely must stay on another branch, and expect the
  full-build cost. (3) Building per branch-switch on a tree-change heuristic is NOT commit-store-aware — it
  rebuilds even for an already-indexed commit; gate on the store's existence, not the tree state.

# CLAUDE.md — working notes for `rig`

`rig` is a fact-based .NET code-intelligence CLI. Product overview, the full command table,
effect observations, and deployment attribution live in [README.md](README.md). Vocabulary:
[docs/ubiquitous-language.md](docs/ubiquitous-language.md). Handover notes: [docs/handover.md](docs/handover.md).
This file is only the things that aren't obvious from those and that you'd otherwise re-derive.

## Orchestration — director → orchestrator → coding agents

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
  mirror (e.g. "mirror the existing X machinery"), hard constraints (annotate-only / full suite green / **do
  NOT commit** / don't touch docs), the explicit ACCEPTANCE test, and the gotchas (TUnit filter syntax,
  csharpier-first, named-args, this list).
- **One agent at a time on shared files** — `FactEffectDeriver`/`FactHazardDeriver`/`builtin-rules.json`/the
  derive+impact paths all contend; concurrent agents just merge-conflict. Parallel only on disjoint work.
- **Run code agents in the MAIN checkout, NOT `isolation: worktree`** — worktree isolation branches from a
  STALE base in this repo (an old `main` merge), so prerequisite files are missing. Bit us twice.
- **Green + committed before the next unit** — never let agent output stack up uncommitted.
- **Design first, dispatch second, calibrate after.** Argue the architecture before building; ship each
  detector/feature with a bug/fix fixture; FP-calibrate a new signal on the real MedDBase store before it
  goes on-by-default (a structurally-true detector that fires 179× is still noise). Surface genuine forks
  to the user; autopilot the rest.
- **Don't dispatch for small/exploratory work** — root-causing, calibration queries, one-file fixes, and
  doc edits are faster inline. Agents earn their keep only on self-contained builds with a clear acceptance test.

## Build / test / ship

- **Ship flow is `scripts/mini-ci.ps1`** — csharpier check → `dotnet build` → all tests →
  pack → reinstall the global `rig` tool. Run it after any source change you intend to use from the CLI.
  Format first (`dotnet csharpier format <files>` or `scripts/format.ps1`) or the csharpier gate fails.
- **Tests are TUnit on Microsoft.Testing.Platform, not vstest.** `dotnet test --filter` does NOT work
  (prints help, "Zero tests ran"). Run a subset with:
  `dotnet run --project tests/Rig.Tests --no-build -- --treenode-filter "/*/*/<ClassName>/*"`
  (path is `/Assembly/Namespace/Class/Test`; `*` wildcards each segment). `dotnet test` with no filter is fine.
- **`-warnaserror` is OFF** (removed from mini-ci 2026-06-22). Analyzer diagnostics — the forked
  `UseNamedArgs` (`use-named-args-fs`, fires on every multi-arg first-party call), `MA0011`
  (format-provider) — are now **non-fatal warnings**, not build errors: the build no longer fails on them
  and there's no fix-it round-trip. Still worth following for readability where cheap (named args on new
  multi-arg first-party calls; `CultureInfo.InvariantCulture` on formatting), and `.editorconfig`
  `UseNamedArgs.exclude_methods` still suppresses the noisy stdlib ones — but none of it gates the build.
  - **The csharpier auto-format still races an in-flight `dotnet build`** (format edits a file the compiler
    is reading) → spurious "N Error(s)" with no diagnostic. Re-run the build once it settles; verify with
    `--no-incremental`.

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

## MedDBase store (the main real-world target)

- The indexed store + its config live in **`c:/git/meddbase-analysis`** (`.rig/` is ~2 GB, plus
  `rig.rules.json`, `deployments.json`). **Run every `rig` query command from that directory** — it picks
  up the rules + store + deployment map from cwd. The source it indexes is `c:/git/meddbase-main-application`.
- Re-index after any **extraction** change (effects/EPs are query-side and need no re-index, but
  `FactExtractor` changes do): from `c:/git/meddbase-analysis`, run `pwsh -File build-if-due.ps1 -ThenIndex`.
  It builds the app in Debug if the tree changed (MSBuild.exe, for the legacy net48 web + .sqlproj projects
  that `dotnet build` can't), then runs `rig index <MedDBase.slnx> --from …/MedDBase.Site/MedDBase/MedDBase.csproj --rules rig.rules.json`.
  Full re-index is slow (whole monorepo). `-ScanOnly` reports whether a build is due; `-Force` rebuilds anyway.

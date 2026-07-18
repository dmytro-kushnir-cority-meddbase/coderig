---
name: rig
description: Drive the `rig` fact-based .NET code-intelligence CLI to trace entry-point→effect call graphs, do reverse reachability ("which entry points reach X"), inventory effects (DB/cache/object-store/throw/messaging/HTTP/EF Core/parallel), surface operational HAZARDS (TOCTOU/race windows, N+1 reads, dual-writes, serializer-unsafe payloads) as ranked candidates, diff a branch's blast radius + behavioral/hazard delta against another commit (`rig impact`, per-entry-point), find unreachable/dead code, and ground-truth-validate static-analysis findings across multi-project solutions. Use when analysing a .NET/C# codebase's call graph or side effects, answering "what does this reach / who reaches this", hunting concurrency/consistency hazards, auditing a PR/migration's blast radius or per-EP effect/hazard changes, finding dead code, or when the user mentions rig, coderig, the `.rig` index, `rig derive/reaches/tree/callers/dead/impact/entrypoints`, hazards/race_window/dual_write, commit-scoped stores (`--store`/`--commit`), or effect/entry-point/hazard detectors.
---

# rig — fact-based .NET code intelligence

`rig` extracts rule-agnostic facts (symbols, references, type relations) from C# into `.rig/rig.db`
(SQLite), then answers call-graph + side-effect questions over them — cross-project, no Roslyn re-run for
query/rule changes. Tool repo: `C:\git\coderig` (global tool `rig`).

## Model
- **Extract** (`index`): runs Roslyn once, writes facts. Expensive — re-run only on source change. `index`
  is the SOLE extraction command; **prefer the bare full-solution form** (sane defaults; the internal build
  cache makes warm re-runs fast). `--from <entry.csproj>` (entry-scoped closure) is a narrowing tool, not the
  default (the old `mine` is superseded).
- **Query/derive** (`derive` `reaches` `tree` `callers` `path` `refs` `symbols` `impact` `entrypoints`):
  read-only over facts. Detectors are JSON rules → new rule = new answer, NO re-extract.
- **Run every query command from the dir holding `.rig/`** (cwd locates the DB). `--rules <path>`
  (repeatable) cascades extra rule files over the builtins.
- **Commit-scoped stores**: `index` writes `.rig/<short-commit>/` + a `LATEST` pointer (reads default to
  LATEST). Any query (and `impact --base`) takes `--store <ref>` (aliases `--commit`/`--at`) to read a
  specific commit/id — so two commits sit side by side for diffing. `index` builds the call-graph views at
  the tail by default (fast SQL path); `--no-graph` opts out.

## Commands
```bash
rig index Sln.slnx                           # PREFERRED: whole solution, ONE call, sane defaults (internal parallel cached build + extract)
rig index Sln.slnx --framework net10.0       # select this TFM in multi-targeted projects (default: first declared TFM)
rig index Sln.slnx --from Entry.csproj       # narrowing ONLY: Entry's transitive ProjectReference closure — see gotcha below before reaching for this
rig index Other.slnx --merge --rules r.json  # multi-solution: ACCUMULATE into the (commit-scoped) store; one run/solution, queries span all. Pass --rules every time. Loop w/ continue-on-failure for a many-solution repo.
rig reaches "Type.Method" [--async]          # effects reachable from a node (sync; --async also walks handoffs)
rig tree "Type.Method" [--view paths|full|effects|summary|hazards] [--format llm|llm-ids] [--guards] [--suppress ctors,lambdas] [--exclude throw] [--raw]   # call tree (default --view paths = effectful paths; --guards marks branch-gated edges)
rig callers "Type.Method" [--roots|--entrypoints]  # reverse (roots = no-predecessor origins; entrypoints = rule-detected EPs)
rig path "From" "To" [--async]               # one concrete path
rig derive [--list-providers] [--exclude-namespace <ns>]   # ALL effects + EPs; --list-providers = the valid --only/--exclude token set
rig entrypoints                              # rule-detected EPs by kind (--format tsv)
rig refs "IFoo"  |  rig symbols "Foo" --kind method [--limit n] [--no-lambdas]
rig impact --base <ref> --head <ref> [--structural] [--format tsv]   # blast radius + behavioral delta (both refs REQUIRED; per-EP diff is the default)
rig reaches "X" --store <id|sha>             # query a SPECIFIC commit's store
```
**Patterns = case-insensitive substring over DocIDs (use the DOTTED form `Type.Method`), but EXACT MATCH
WINS:** a pattern that exactly equals a node's full DocID or its `M:`-stripped param-free FQN seeds ONLY that
member — so the full FQN `…Search.Search.Proceed` resolves to exactly `Proceed`, not its prefix-twin
`…Search.ProceedToConfirmationScreen`; a partial pattern (`Search.Proceed`) stays substring and still matches
both. Applies to every seed (`tree`/`reaches`/`callers`/`path` roots + the `path` target). The `▶` /
`callers --entrypoints` / `derive` EP lines print the slash ROUTE (`AI/SmartLetter.Send`) which matches
NOTHING — but now ALSO print the queryable FQN (`↪ <fqn>` line + tsv `fqn` column); paste THAT (exact-match
then resolves it precisely). Unexpected empty / "No path" / "0 call edges" → suspect a route-form pattern first.

## Reading output (don't misread it)
Effects are emoji-tagged `provider:op` (💾write 🔍read 📥fetch 🌐http 📤queue 📣echo 🗃️cache 📦object-store
📁io ⚠️throw 🧵parallel 🛢️db_command; + EF Core `efcore:*`, raw ADO `db_command|db_connection|db_reader`).
Per-repo glyphs via `rig.effect-emoji.json`.
- **N occurrences = N static call-SITES (branches included), NOT N runtime writes** — "places in code," never execution count.
- **`~heuristic` = INFERRED dispatch** (Roslyn couldn't bind, net48 `!:`; name/arity fallback, ~99% — verify). Unmarked dispatch = exact mined fact, trust it.
- **Fan-out `×N` = "could be any of these N," NOT "calls all N"** (CHA over-approximation). `reaches` buckets fan-out-only targets as "NOT a real call"; a resolved target is not re-dispatched (one hop).
- **`derive` totals ≠ reachable**: an effect surfaces in `reaches`/`tree` only if its enclosing id is a call-graph node (`M:`/accessor/lambda/ctor). Effects keyed to `P:`/`F:` show in `derive` but never reach.
- `tree` children are source-ordered (≈ execution order). Generic labels show the REAL instantiation (monomorphized down the chain), not `<T,U>`.
- **Filter**: `--only`/`--exclude <list>` (comma/space-sep, repeatable; `provider` or `provider:op`; exclude wins). Headline: `--exclude throw`. An UNKNOWN token now WARNS (stderr) instead of silently matching nothing — `rig derive --list-providers` prints the valid set for the current rules.
- **`shared_state:read` is write-pair GATED by default** (`derive` / `tree --view hazards` / `impact`): a static-field/auto-property read is emitted only when that exact cell is also WRITTEN somewhere in scope. A read of a never-written cell — an enum member, `const`, `static readonly`, or any unmutated static — can't be the "check" half of a read-before-write, so it's dropped as inventory noise (≈94% of raw static-field reads on MedDBase — 81.7k→4.9k sites: `FieldIndex.*`, `MessageBoxButtons.*`, enum members, etc.). **`--no-gate`** restores the full ungated read inventory. Presentation-only — `race_window`/hazard output is UNCHANGED (the matcher already pairs same-cell read+write, so unpaired reads never contributed a hazard).
- **Hazards** (`tree --view hazards`, `derive` rollup): per-method deduped (one row `×N` sites; the per-type header reads "N site(s) across M method(s)"). `race_window`/`lazy_init_race` are TRIAGE CANDIDATES, not proofs — sort/read by method, not raw count; `#cctor` static-init is exempt (CLR type-init lock). `lazy_init_race` is TIERED by reason: `[lazy_init_heuristic]` = the bare shape (triage first); `[lazy_init_lock_enclosed_verify_dcl]` = the write sits inside a `lock` (looks like DCL — lower priority, but verify the cell is `volatile` and the publish is last; DCL without volatile is still broken). `--exclude-namespace <prefix>` (repeatable) drops framework/vendored hazard noise (e.g. `--exclude-namespace Echo.Process`); hazards-only, effects unaffected.
- **Control-dependence guards** (`tree --guards`): a call edge that runs only under a branch is marked `⎇ [condition]` (the analog of `🔁[loop]`); an unconditional (must-run) edge carries none — so the glyph-free frames are the SPINE that fires on every invocation, and `⎇` frames are the guarded shell. The condition is the full source predicate — a short-circuit `a || b` shows whole (not split into operands), an else-arm is negated `!(…)`; the `foreach` MoveNext guard is filtered as redundant with `🔁`. **INTRA-METHOD only**: it says the call is gated WITHIN its direct caller, NOT whether that caller always runs from the EP (the cross-method "always-runs-from-EP" composition is a derive-side follow-up). `--guards` also appends a trailing `guards` column to `--format tsv|llm|llm-ids` (a dedicated column, since a condition can contain `||`).

## Async handoffs — SYNC-CUT by default
A delegate handed to a dispatcher to run later/elsewhere is a `handoff` edge, NOT a call. Default CUT: a
background registration's callback effects are ABSENT from the registrar's reach; the callback is its OWN
origin (so `callers --roots` origins + per-EP inventories are the trustworthy sync ones). `--async` walks
handoffs, tagged (`reaches` splits direct / async-scheduled ⚡ / fan-out; `tree`/`path` mark `⤳ via
<dispatcher>`). Dispatchers are rule DATA (`handoffDispatchers`; re-run `rig graph` after edits, no
re-index). Co-location-based; BCL (`Task.Run`)/lambda callbacks fall to the unclassified residual.

**`--async` excludes imprecise DELIVERY FAN-OUT (use `--include-delivery` to restore it).** Publish→consumer
delivery edges (`event_raise`, `actor_tell`) join a raise to its subscribers by SYMBOL/name only — no
instance or call-site identity. When a channel has many subscribers (e.g. a reused dialog/proxy event with
N `+= Handler` sites), the join fans the raise out to ALL of them, manufacturing false cross-caller paths
(MedDBase: 22/22 sampled were wrong — see docs/FIX-event-raise-overapproximation.md). So plain `--async`
walks the SOUND handoffs (registrant→handler `event`, scheduler/timer/spawn, and single-subscriber `exact`
delivery) but CUTS the multi-subscriber fan-out. `--async --include-delivery` walks the fan-out too (the
over-approximate superset). Trust the `--async` default for reachability/auth questions; reach for
`--include-delivery` only when you want every theoretically-possible delivery edge. The cycle hazard
(`event_cycle`) is unaffected — it always considers all delivery edges.

## Detectors & render rules (DATA, query-side, NO re-index)
- **Add an effect**: append to `effects` in `rig.rules.json` (`{provider, operation, methods:[…],
  receiverTypes|declaringTypes:[…], resource}`), re-run `derive`/`reaches`. Match the API at its
  FIRST-PARTY call site — a `receiverTypes` may name an EXTERNAL type (e.g. `StackExchange.Redis.IDatabase`)
  and rig still tags it where your code calls it. `resource:"argument_name"` captures the key/channel arg
  (high-signal); others: `receiver_type`/`declaring_type`/`argument_type`/`type_argument`/`string_argument`
  (drops when the arg isn't a literal — prefer `string_argument_or_receiver`, which falls back to the
  receiver instead). Gate with the tightest type to avoid same-name misfires.
- **Tree render rules** (`render`; ships empty, PRESENTATION-only — never affect reach): `collapseSeams
  {pattern,label}` folds a fan-out HUB into one summary leaf (union of effects + hidden count);
  `opaqueTypes {pattern,label}` draws a type/namespace as a leaf (anchor a namespace with `M:`). `tree
  --raw` bypasses them.

## Core workflow
1. **Health-check**: `rig runs` (EP/effect/symbol counts). Thousands of EPs+effects expected; near-zero with healthy symbols = base-type binding flake (REFERENCE).
2. Forward "what does X touch": `rig reaches X` / `rig tree X --view full`.  Reverse "who reaches X": `rig callers X --roots`.  Path: `rig path X Y`.  Inventory: `rig derive`.  Change blast radius: `rig impact --base <ref> --head <ref>`.
3. **Validate against SOURCE, not the DB** (it's the fallible Roslyn pass's output): trace from source → replicate with the SHIPPED engine (`reaches`/`path`, never a BFS reimpl) → fix the RULE, not the test.

## Blast radius & behavioral diff (`rig impact`)
Diffs the derived graph between two commits (immune to format/rename churn). It diffs the two indexed STORES
— NO git working-tree diff, NO `--repo`. **BOTH `--base <ref>` AND `--head <ref>` are REQUIRED** (each resolves a ref→sha→store;
there is no LATEST defaulting). The **per-entry-point effect-set diff is the DEFAULT output** — the precise
lens; surfaces deltas the global diff masks (one EP losing a sink others still reach; `-` on some EPs + `+`
on others = a relocation). Three layers: changed set (FILE-granular → over-approx), affected EPs (by
service), behavioral delta (`±effect`/`+observation`, param-free keyed). **`--structural`** expands the
one-line structural-only summary into the full list of EPs whose reachable TREE changed (incl. the usually
large set rippled only by a data-shape change). **`--expect-no-effect-change`** is the CI gate (exit 1 if any
EP's effect set changed).
- **Cross-check surprising deltas against UNBOUNDED `tree`/`reaches` (the oracle), two known gaps:** (a) the
  per-EP reach can be depth-capped while `tree`/`reaches` aren't — a path crossing the cap reports a spurious
  `±`; verify with `tree "<EP>" --view effects --store <head>` vs `<base>`. (b) reverse layers
  (`callers`/`--roots`/affected-EP) can MISS an EP reached only via interface-dispatch/lambda — confirm
  forward with `rig path "<EP>" "<target>"`.
- Setup: index base then branch (each → its own store), then `impact --base <store> --head <store>`.
  A full impact run is minutes on a big store (in-process, parallel — no N shell-outs).

## Deployment attribution (`deployments.json`, opt-in)
Drop `deployments.json` next to `.rig/` → every EP line annotates the hosting service(s) `⟦Service (kind)⟧`.
`{services:[{name, host:"<entry csproj rel to sln>", kind, provides?:[…]}]}` (JSONC). Maps each service's
csproj → ProjectReference closure → an EP's file → owning csproj → service. Closure = "loaded-in" (upper
bound); refine to "active-in" via rule-declared `provides`∩`requires` tokens. Full schema in REFERENCE.

## Notes
- **`rig dead` is currently DISABLED** (unwired in `CommandLine/Root.cs`; errors *"'dead' was not matched"*). Approximate via `callers <m> --roots` on suspected-unused methods, or read source. Model in REFERENCE for when it's re-enabled.
- **Query cache**: `tree` caches forest+effects in `.rig/cache.db` (auto-invalidated on reindex). `--no-cache` to bypass; safe to delete anytime.

## Gotchas (full list in REFERENCE)
- **`index` builds internally — NO external pre-build.** Bare `rig index <sln>` defaults are sane (parallel cached internal build; it no longer clobbers shared `bin/`); pass `--parallelism N` only to tune.
- **Prefer FULL-solution index over `--from`.** An entry-scoped closure follows ProjectReference edges only, so solution projects consumed as binary/paket references are silently OUTSIDE it — their internals vanish from the store while rule-tagged effects still appear at call sites (`receiverTypes` matches the external type), so the store LOOKS complete but `path`/`tree` can't descend into them (MedDBase: `--from` the site csproj dropped the whole `dfs` project; `DFS.SaveText` effects showed, `AzureBlobStorageClient` didn't exist). Use `--from` only when you deliberately want that scoping and can name what it excludes.
- **Standalone `index` REPLACES atomically** (write-temp + rename) — no need to delete `.rig` first. `index --merge` (or `--identity`) ACCUMULATES instead: one run per solution into a commit-scoped unified store (`rig runs` lists them; queries span all). A single-solution store makes other solutions' entry points invisible → false dead-code/phantom reach; index all app solutions before trusting cross-product reachability. See REFERENCE.md.
- **Index with the global `rig`** (Debug *can* index now MEF deps are pinned, but global is reliable; don't index rig's own repo from its own `bin/` Debug dll — it self-clobbers mid-run).
- Results are only as good as the rules + what's in scope — see the fundamental static-analysis limits in REFERENCE before trusting a count.

See **[REFERENCE.md](REFERENCE.md)** for: full command reference, indexing semantics, the rule/detector
model + detector families, recall behaviour (dispatch + dead-code), the fundamental limits, and env gotchas.

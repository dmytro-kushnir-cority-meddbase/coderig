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
  is the SOLE extraction command (`--from <entry.csproj>` = entry-scoped closure; the old `mine` is superseded).
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
rig index Sln.slnx --parallelism 16          # whole solution, ONE call (internal parallel build + extract)
rig index Sln.slnx --from Entry.csproj       # entry-scoped: Entry's transitive ProjectReference closure
rig reaches "Type.Method" [--async]          # effects reachable from a node (sync; --async also walks handoffs)
rig tree "Type.Method" [--full|--effects] [--exclude throw] [--raw] [--no-cache]   # call tree (default: only effectful paths)
rig callers "Type.Method" [--roots|--entrypoints]  # reverse (roots = no-predecessor origins; entrypoints = rule-detected EPs)
rig path "From" "To" [--async]               # one concrete path
rig derive                                   # ALL effects + entry points from facts
rig entrypoints                              # rule-detected EPs by kind (--format tsv)
rig refs "IFoo"  |  rig symbols "Foo" --kind method
rig impact --base <ref> [--per-ep] [--format tsv]   # blast radius + behavioral delta vs another commit
rig reaches "X" --store <id|sha>             # query a SPECIFIC commit's store
```
**Patterns = case-insensitive substring over DocIDs — use the DOTTED form (`Type.Method`).** The `▶` and
`callers --entrypoints` lines print the EP ROUTE (`AI/SmartLetter.Send`, with slashes); querying that route
matches NOTHING — strip to `SmartLetter.Send`. Unexpected empty / "No path" / "0 call edges" → suspect a
route-form pattern first.

## Reading output (don't misread it)
Effects are emoji-tagged `provider:op` (💾write 🔍read 📥fetch 🌐http 📤queue 📣echo 🗃️cache 📦object-store
📁io ↯throw 🧵parallel 🛢️db_command; + EF Core `efcore:*`, raw ADO `db_command|db_connection|db_reader`).
Per-repo glyphs via `rig.effect-emoji.json`.
- **N occurrences = N static call-SITES (branches included), NOT N runtime writes** — "places in code," never execution count.
- **`~heuristic` = INFERRED dispatch** (Roslyn couldn't bind, net48 `!:`; name/arity fallback, ~99% — verify). Unmarked dispatch = exact mined fact, trust it.
- **Fan-out `×N` = "could be any of these N," NOT "calls all N"** (CHA over-approximation). `reaches` buckets fan-out-only targets as "NOT a real call"; a resolved target is not re-dispatched (one hop).
- **`derive` totals ≠ reachable**: an effect surfaces in `reaches`/`tree` only if its enclosing id is a call-graph node (`M:`/accessor/lambda/ctor). Effects keyed to `P:`/`F:` show in `derive` but never reach.
- `tree` children are source-ordered (≈ execution order). Generic labels show the REAL instantiation (monomorphized down the chain), not `<T,U>`.
- **Filter**: `--only`/`--exclude <list>` (comma/space-sep, repeatable; `provider` or `provider:op`; exclude wins). Headline: `--exclude throw`.

## Async handoffs — SYNC-CUT by default
A delegate handed to a dispatcher to run later/elsewhere is a `handoff` edge, NOT a call. Default CUT: a
background registration's callback effects are ABSENT from the registrar's reach; the callback is its OWN
origin (so `callers --roots` origins + per-EP inventories are the trustworthy sync ones). `--async` walks
handoffs, tagged (`reaches` splits direct / async-scheduled ⚡ / fan-out; `tree`/`path` mark `⤳ via
<dispatcher>`). Dispatchers are rule DATA (`handoffDispatchers`; re-run `rig graph` after edits, no
re-index). Co-location-based; BCL (`Task.Run`)/lambda callbacks fall to the unclassified residual.

## Detectors & render rules (DATA, query-side, NO re-index)
- **Add an effect**: append to `effects` in `rig.rules.json` (`{provider, operation, methods:[…],
  receiverTypes|declaringTypes:[…], resource}`), re-run `derive`/`reaches`. Match the API at its
  FIRST-PARTY call site — a `receiverTypes` may name an EXTERNAL type (e.g. `StackExchange.Redis.IDatabase`)
  and rig still tags it where your code calls it. `resource:"argument_name"` captures the key/channel arg
  (high-signal); others: `receiver_type`/`declaring_type`/`argument_type`/`type_argument`. Gate with the
  tightest type to avoid same-name misfires.
- **Tree render rules** (`render`; ships empty, PRESENTATION-only — never affect reach): `collapseSeams
  {pattern,label}` folds a fan-out HUB into one summary leaf (union of effects + hidden count);
  `opaqueTypes {pattern,label}` draws a type/namespace as a leaf (anchor a namespace with `M:`). `tree
  --raw` bypasses them.

## Hazards — pattern findings over effects (CANDIDATES, never verdicts)
A third layer over effects + reachability: detectors match PATTERNS across the effect graph and emit ranked,
confidence-tiered findings for an LLM reviewer (suspicion maps, not proofs — **annotate, never suppress**;
calibrate FP rate before on-by-default). Shipped finding types (catalog: `HazardKinds`): `race_window`
(read→write of the same cell, same method, no tx — TOCTOU/lost-update; high, medium when tx-bracketed),
`lazy_init_race` (the lazy-init/do-once shape, low/heuristic), `n_plus_1` (looped read, key varies per
iteration), `unserializable_payload` (`Option<T>`/etc. into a store/serialize), `dual_write` (≥2 distinct
durable systems written in one method — db/queue/search/cache/http).
- **See them**: `rig derive` prints a **Hazards** section (named, counted, per-confidence-tier) + a
  `hazard` tsv row (`type/confidence/reason/cell/enclosing/file:line`).
- **Diff them**: `rig impact` reports a per-EP hazard DELTA (`+/- hazard <type>(<conf>)` base→head). NB
  `--expect-no-effect-change` is effect-set-only and does NOT trip on a hazard-only delta.
- **Drill into one EP**: `rig tree <ep> --hazards` — a ⚠ marker per hazard-bearing node (`type(conf)`, ×N
  for repeats) + a summary section + `--format tsv` `hazard` rows. Re-derives the EP's bounded closure with
  the static-field refs threaded in + the hazard post-pass; the augmented effects aren't cached, so a plain
  `tree` is unaffected. This is the surface that shows the field-fed `shared_state:read/mutate` effects a
  plain `tree` omits (it doesn't thread field refs).
- **Not yet wired**: `event_cycle` (blocked on a missing publish→consumer graph edge). Hazards need static
  field read/write effects → **re-index** after the field-emission/structural-context extraction changes.
- **Design**: [docs/hazards.md](../../docs/hazards.md) (abstract, in-repo). MedDBase-grounded roadmap + RCA
  corpus live in `meddbase-analysis/docs/` (backlog-bug-detection.md, rca-corpus-meddbase.md).

## Core workflow
1. **Health-check**: `rig runs` (EP/effect/symbol counts). Thousands of EPs+effects expected; near-zero with healthy symbols = base-type binding flake (REFERENCE).
2. Forward "what does X touch": `rig reaches X` / `rig tree X --full`.  Reverse "who reaches X": `rig callers X --roots`.  Path: `rig path X Y`.  Inventory: `rig derive`.  Change blast radius: `rig impact --base <ref>`.
3. **Validate against SOURCE, not the DB** (it's the fallible Roslyn pass's output): trace from source → replicate with the SHIPPED engine (`reaches`/`path`, never a BFS reimpl) → fix the RULE, not the test.

## Blast radius & behavioral diff (`rig impact`)
Diffs the derived graph between two commits (immune to format/rename churn). Needs BOTH commits indexed +
the source git diff. `--base <ref>` (branch = LATEST store). `--per-ep` = per-entry-point effect-set diff —
the precise lens; surfaces deltas the global diff masks (one EP losing a sink others still reach; `-` on
some EPs + `+` on others = a relocation). Three layers: changed set (v1 FILE-granular → over-approx),
affected EPs (by service), behavioral delta (`±effect`/`+observation`, param-free keyed).
- **Cross-check surprising deltas against UNBOUNDED `tree`/`reaches` (the oracle), two known gaps:** (a)
  `--per-ep` reach can be depth-capped while `tree`/`reaches` aren't — a path crossing the cap reports a
  spurious `±`; verify with `tree "<EP>" --effects --store <head>` vs `<base>`. (b) reverse layers
  (`callers`/`--roots`/affected-EP) can MISS an EP reached only via interface-dispatch/lambda — confirm
  forward with `rig path "<EP>" "<target>"`.
- Setup: index base then branch (each → its own store); `--repo <path>` for a separate source tree.
  `--per-ep` is minutes on a big store (in-process, parallel — no N shell-outs).

## Deployment attribution (`deployments.json`, opt-in)
Drop `deployments.json` next to `.rig/` → every EP line annotates the hosting service(s) `⟦Service (kind)⟧`.
`{services:[{name, host:"<entry csproj rel to sln>", kind, provides?:[…]}]}` (JSONC). Maps each service's
csproj → ProjectReference closure → an EP's file → owning csproj → service. Closure = "loaded-in" (upper
bound); refine to "active-in" via rule-declared `provides`∩`requires` tokens. Full schema in REFERENCE.

## Notes
- **`rig dead` is currently DISABLED** (unwired in `CommandLine/Root.cs`; errors *"'dead' was not matched"*). Approximate via `callers <m> --roots` on suspected-unused methods, or read source. Model in REFERENCE for when it's re-enabled.
- **Query cache**: `tree` caches forest+effects in `.rig/cache.db` (auto-invalidated on reindex). `--no-cache` to bypass; safe to delete anytime.

## Gotchas (full list in REFERENCE)
- **`index` builds internally — NO external pre-build.** `--parallelism 16` is the safe standard (the internal build no longer clobbers shared `bin/`).
- **Standalone `index` REPLACES atomically** (write-temp + rename) — no need to delete `.rig` first. Only `index --identity` (multi-solution accumulate) APPENDS.
- **Index with the global `rig`** (Debug *can* index now MEF deps are pinned, but global is reliable; don't index rig's own repo from its own `bin/` Debug dll — it self-clobbers mid-run).
- Results are only as good as the rules + what's in scope — see the fundamental static-analysis limits in REFERENCE before trusting a count.

See **[REFERENCE.md](REFERENCE.md)** for: full command reference, indexing semantics, the rule/detector
model + detector families, recall behaviour (dispatch + dead-code), the fundamental limits, and env gotchas.

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
rig reaches "Type.Method"                        # effects reachable from an entry point
rig tree "Type.Method"                           # call tree (default: paths that hit an effect; --full)
rig callers "Type.Method" --roots                # reverse: entry-point candidates that reach it
rig path "From.Method" "To.Method"               # one concrete path between two symbols
rig derive                                       # re-derive ALL effects + entry points from facts
rig dead --root "App.Main"                       # unreachable first-party methods (report-only)
rig refs "IFoo" / rig symbols "Foo" --kind method
```

Patterns are case-insensitive substring matches over DocIDs (`M:Ns.Type.Method(args)`).

## Core workflow — answer a reachability/effect question

1. **Confirm the index is healthy**: `rig runs` (entrypoints/effects/symbols counts). A sane Pages-scale
   index has thousands of EPs + effects; near-zero EP/effect with healthy symbols = base-type-chain binding flake (see REFERENCE).
2. **Forward** ("what does X do/touch"): `rig reaches X` (effects) / `rig tree X --full` (call tree).
3. **Reverse** ("who/what entry points reach X"): `rig callers X --roots`.
4. **Specific path**: `rig path X Y`.
5. **Effect/EP inventory**: `rig derive [--rules extra.json]`.
6. **Dead code**: `rig dead` — see below.

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

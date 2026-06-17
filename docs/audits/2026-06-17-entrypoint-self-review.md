# Entry-point self-review (dogfood) — 2026-06-17

`rig` reviewing its own CLI. For each of the 13 derived entry points, pulled the static `rig tree` trace
from the dogfood store (`C:\temp\rig-dogfood`), then **verified against source** which calls actually
re-execute at runtime before flagging anything. Method mirrors the `impact` review
(`docs/bugs/impact-base-store-ep-data-loaded-twice.md`).

## Headline

The static `rig tree` consistently *looks* far more redundant than runtime is. Across all 13 EPs, every
alarming repeated node — `↺seen`, a load under multiple parents, double `ReachedBy`, both SQL + EF graph
paths, tsv + human writers — was a **static-trace artifact**: a mutually-exclusive branch or a
shared-by-reference parameter, not real re-execution. Same trap as the impact "graph loaded 4×" misread.
Trust nothing from the tree's shape without opening the source.

Architecture is sound: one read-context per command, the `.rig/cache.db` query cache, the EF-for-reads /
raw-ADO-for-bulk-writes split, and single bounded-graph loads all check out.

## Per-entry-point verdict

| Command | Entry point | Verdict | Real finding |
|---|---|---|---|
| (root) | `CliApplication.RunAsync` | ✅ clean | thin dispatcher; tree fan-out is the static union of mutually-exclusive commands |
| reaches | `ReachesCommand.RunAsync:70` | 🟡 minor | rules ×7; Factory double-load under non-`--raw` |
| **tree** | `TreeCommand.RunAsync:118` | 🔴 **real** | **~410ms unconditional run/deployment EF query on warm/no-deployments path** |
| callers | `CallersCommand.RunAsync:83` / `RunEntryPointsAsync:235` | 🟡 medium | duplicate whole-method-table scan on `--entrypoints` |
| path | `PathCommand.RunAsync:61` | 🟡 minor | rules ×4 |
| dead | `DeadCommand.RunAsync:69` | 🟡 minor | rules ×3 (unshaped CHA superset is by-design, not flagged) |
| derive | `DeriveCommand.RunAsync:59` | ✅ clean | epData loaded once & shared (impact concern does NOT recur); rules ×5 |
| index | `IndexCommands.RunIndexAsync:65` | ✅ clean | rules loaded once, passed by value — not per-file |
| mine | `IndexCommands.RunMineAsync:301` | ✅ clean | graph built once post-BFS; rules once per project |
| graph | `IndexCommands.RunGraphAsync:456` | 🟡 medium | rules ×3–4 inside `MaterializeGraphAsync` (index/mine pay it too) |

## Issues filed

1. **`tree-unconditional-deployment-query-warm-path.md`** — 🔴 the standout; a `File.Exists` gate removes
   the dominant warm-query cost (helps every command calling `LoadDeploymentsAsync`).
2. **`rules-loadforsolution-no-memo.md`** — 🟡 systemic; `AnalysisRuleSet.LoadForSolution` re-reads +
   rebuilds 3–11× per command. One memo fixes all of it (incl. the reaches Factory double-load and the
   `RulesFingerprint` double-read).
3. **`callers-entrypoints-duplicate-method-scan.md`** — 🟡 derive `reachableSites` from the already-loaded
   `epData.Methods`.

## Confirmed NOT issues
- `derive` does not double-load `epData` (the impact-style concern) — it's loaded once and shared.
- `index`/`mine` do not re-read rules per file — loaded once, passed by value into the parallel extract.
- All the "duplicate" graph/context/method reads the trees show are distinct projections on a shared
  read-only context or mutually-exclusive branches.

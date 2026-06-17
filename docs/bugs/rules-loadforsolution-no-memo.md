# Perf: `AnalysisRuleSet.LoadForSolution` has no memoization — re-read & rebuilt 3–11× per command

**Status:** ⚪ Won't fix as a perf change — MEASURED negligible (2026-06-17). The redundancy is real and
systemic (verified across every entry point), but measurement showed it costs nothing worth chasing, and
every fix attempt either added complexity for no gain or didn't help. Kept here as a known structural smell;
revisit only as part of a clarity refactor, not for speed.

### What the measurement showed (MedDBase store, 53KB `rig.rules.json` + 56KB builtin, warm `rig tree`)
Instrumented `LoadForSolution`: **8 calls, 85.8ms total — but 71ms is the FIRST call; calls 2–8 are ~2ms
each.** The first `Deserialize` alone is ~48ms and drops to ~1ms on the second — i.e. the dominant cost is
one-time **JIT + first-use of the System.Text.Json reader machinery**, paid once per process no matter how
many loads happen. Only the ~2ms-per-redundant-call (≈14ms across the 7 repeats) is recoverable, which is
below the run-to-run noise floor (±25ms).

### Approaches tried and dropped
- **Process memo** keyed on (anchor, extraRules): A/B on warm `tree` was statistically identical (no
  measurable gain); adds a static mutable cache + staleness risk (tests reuse paths). Dropped.
- **JSON source generation** (`JsonSerializerContext`): hypothesis was the 48ms = STJ reflection-contract
  building. Measured first-parse with source-gen = ~58ms, i.e. NOT faster — confirming the cost is JIT/
  first-use, not reflection contracts. Neither faster nor cleaner here. Dropped.
- **Explicit `PreparedRules` + `IRuleSetRepository`** (parse once in a `prepare` phase, thread downstream):
  the *clean* fix — eliminates the redundancy structurally — but its perf gain is the same sub-noise ~14ms.
  Prototyped and rolled back; worth doing only as a deliberate clarity/architecture pass, tracked separately.

(Original analysis below retained for reference; the per-command counts are accurate, the cost is not.)

(Foreshadowed in `impact-base-store-ep-data-loaded-twice.md` as "see a future
`rules-loadforsolution-no-memo.md` if it proves to matter." Verdict: it fires everywhere, but does not
prove to matter for speed.)
**Kind:** systemic redundant I/O + allocation (not a correctness bug).
**Affected:** every command that loads rules — `tree`, `reaches`, `callers`, `path`, `dead`, `derive`,
`index`/`mine`/`graph` (via `MaterializeGraphAsync`).

---

## Summary

`AnalysisRuleSet.LoadForSolution` (`src/Rig.Analysis/Rules/AnalysisRuleSet.cs:71`) has no memoization. Each
call:
- runs `LoadBuiltIn()` — `File.OpenRead` + `JsonSerializer.Deserialize` of `builtin-rules.json` (the big one),
- merges the global `~/.rig/rig.rules.json` (`MergeWithFile` — `File.Exists` + read + deserialize),
- walks up to 8 parent dirs to find + merge the repo `rig.rules.json`,
- merges every `--rules` file,

and each `MergeDocument` is an immutable-record `with` that re-allocates ~25 collections via
`.Concat().ToArray()`. It builds the *entire* merged `AnalysisRuleSet` (all ~25 rule sections) only for a
caller that then projects out one slice.

No provider caches: every `FactXxxRuleProvider.LoadForWorkingDirectory` (and `ShapingRuleSet.Load`, which
calls **four** providers — `src/Rig.Cli/Rules/ShapingRuleSet.cs:21-27`) calls `LoadForSolution` fresh with
identical `(workingDirectory, extraRules)`. The result is a pure function of those args within one process,
so every call after the first is wasted I/O + parse + allocation.

## Per-command load counts (single runtime path, verified)

| Command | `LoadForSolution` calls | Notes |
|---|---|---|
| `tree` | ~9 | shaping ×4 + render + fingerprint + effects ×2 + emoji |
| `reaches` | 7 | shaping ×4 + factory(again) + effects ×2 — **one is a pure double-load** (see below) |
| `callers --entrypoints` | 6 | shaping ×4 + EP-rules ×2 |
| `derive` | 5 | handoff + effects ×2 + EP-rules ×2 (15 JSON parses) |
| `path` | 4 | shaping ×4 |
| `dead` | 3 | handoff + EP-rules ×2 |
| `index`/`mine`/`graph` | 3–4 | inside `MaterializeGraphAsync` (handoff + factory + handoff-again + fingerprint) — **per graph build**, paid by all three |

Absolute cost is small today (the JSON files are ~KB), so severity is **secondary** — but it is pure
duplicated work that scales linearly with rule-file size, and it is trivially correct to eliminate.

### Sub-findings that fold into the same fix

- **`reaches` loads the Factory rules twice** (`ReachesCommand.cs:91-94`): `ShapingRuleSet.Load` loads
  `Factory`, then `with { Factory = FactGenericFactoryRuleProvider.LoadForWorkingDirectory(...) }`
  immediately discards and re-loads it (the reaches-monomorphizes-even-under-`--raw` asymmetry). Under
  non-`--raw` that's a strict redundant load. Fix: have `ShapingRuleSet.Load` take a flag to load Factory
  ungated, or skip the `with` when `!raw`.
- **`RulesFingerprint.Compute` double-reads the rule files** (`RulesFingerprint.cs`): it calls
  `LoadForSolution` (which reads + parses every file), then re-reads each resolved file's bytes again to
  hash. It can hash off `LoadedRulesPaths` content already in hand. (Only on cache-enabled / deployments
  paths.)

## Fix direction

Memoize `LoadForSolution` for the process lifetime, keyed on `(anchor, extraRulesPaths)` (optionally
include file mtimes/size so a mid-process rule edit invalidates — though a CLI process loads one store and
one rule set, so a plain per-(anchor, extraRules) memo is sufficient). Alternatively, load one
`AnalysisRuleSet` at the top of each command and thread the projected slices into the providers /
`DeriveEffects` / `ShapingRuleSet` — they already receive `(workingDirectory, extraRules)`, so a single
up-front load + pass-through is a mechanical refactor. Collapses 3–11 loads to 1 per command.

## Test to add

A counting test (e.g. a seam/probe on file reads, or a memo hit-counter) asserting that a single command
invocation reads `builtin-rules.json` exactly once.

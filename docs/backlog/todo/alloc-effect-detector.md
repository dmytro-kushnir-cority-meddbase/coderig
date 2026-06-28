# `alloc` effect + `alloc_in_loop` / `alloc_hot_path` — allocation as a rig effect

**Status:** design-only (do-not-build-yet) · **Found:** 2026-06-28 (gcdump perf-archaeology + opus design pass, key claims verified against code) · **Family:** detector / effect-model-extension
**Related:** [[redundant-graph-index-rebuild-per-query]] + [[warm-graph-across-queries]] — this detector is the **discovery engine that feeds the redundancy work** (and those findings are its calibration corpus); the `bench gcloop`/gcdump harness is its **runtime ground-truth oracle**.

## The role: feed the redundancy work
rig's `tree`/`reaches` are **blind to allocation** — they show calls + typed effects (io/db/llblgen/…), never `new`/LINQ churn. The point of this detector is NOT a standalone feature: it's to **surface allocation hotspots automatically** so the perf-redundancy work has a candidate list instead of manual gcdump archaeology. The loop is mutual:
- detector → redundancy work: rank allocation-heavy sites on hot/looped paths → those become redundancy-elimination targets (the `impact` 6× rebuild, `BuildIndex`-per-query, etc.).
- redundancy findings → detector: the gcdump-confirmed hot allocations are the **ground-truth corpus** to calibrate the detector's gates against (you can't validate an allocation detector by reading source — allocation cost is runtime).

## Honest headline (verified)
A cheap v1 catches a real-but-narrow class and **0 of the 5 motivating examples** from this session. Why, confirmed against code:
- **Ctor refs are structurally blind.** `FactExtractor.OnCreation` (`FactExtractor.cs:245`) calls `AddReference` with NO `structural:`/`enclosingGuards:` — so `new List<>()` / `new X()` in a loop carries null loop context and is **underivable today**. (VERIFIED at line 245.)
- **LINQ invocations DO carry loop/guard context** (the `OnName` path threads `StructuralContextOf` + `EncodedGuards`). So v1 can only ride LINQ-materialization facts — zero new extraction, zero re-index.

## v1 design (cheap, the only thing to build first)
- **`alloc` effect** — data-driven `FactEffectRule`s in `builtin-rules.json`, **off-by-default** (else firehose): invocation rules on `System.Linq.Enumerable` **materializers only** (`ToList`/`ToArray`/`ToDictionary`/`ToHashSet`/`ToLookup`/`GroupBy`/`OrderBy*`/`Distinct*`/`Concat`/`Union`/`Reverse`/`Append`/`Prepend`) → `alloc:materialize`. The **streaming ops must be excluded** (`Where`/`Select`/`Count`/`Take`/`Any`/`First` are lazy — NOT allocations); this split is the precision lever (on rig's store: ~8 materializers-in-loop vs 45+ lazy-in-loop).
- **`alloc_in_loop` hazard** — the direct twin of `n_plus_1` (=read-in-loop): an `alloc` effect carrying `looped_effect`. ~30 lines mirroring the n_plus_1 arm in `FactObservationDeriver` (reuse `KeyVariesWith` to tier high/medium on per-iteration vs hoistable-invariant). Register in `HazardKinds.All`. Confidence **medium**.
- Fixture pair (alloc-in-loop-varying = flagged; hoisted-out = not) in a NEW `AllocInLoopTests.cs`. Ship as opt-in `--provider alloc` first; promote `alloc_in_loop` to default-on ONLY if it calibrates ≥85% on MedDBase.

## What "reliable" actually requires (the real work, beyond v1)
Reliability has two axes: COVERAGE (catch real allocation, not just LINQ-in-loop) and CALIBRATION-against-truth (know the candidates correlate with measured churn). In sequence:
1. **Ctor structural context (biggest coverage/effort).** Add `StructuralContextOf` + `EncodedGuardsFor` to `OnCreation` → collection-ctor-in-loop becomes derivable. ~5 lines but **forces a MedDBase re-index** (extraction change). Verify array-creation (`new T[]`) routes through `OnCreation`.
2. **Runtime-calibration loop (non-negotiable — this is what makes "reliable" falsifiable).** Static candidates → run `bench gcloop`/gcdump on the same workload → check candidates correlate with measured hot allocations → tune gates → repeat. Allocation cost is runtime; without this you cannot *know* it's reliable. The harness built this session IS the oracle.
3. **`IOperation` synthesized-allocation pass (deepest coverage).** Boxing, closures/lambda captures, iterator/async state machines, string interp/concat — no ctor/invocation node, invisible to fact-level detection. Detect via Roslyn `IOperation` at extraction — **leverages the CFG/`IOperation` machinery already built for branch-aware-effects** (the `ControlDependence` engine builds it per method). Catches the recursive-yield-iterator class.
4. **`alloc_hot_path` (the `BuildIndex` archetype).** Allocation on a must-run path reachable from many EPs = cross-method **reach-fan-in × must-run** (uses the new `EnclosingGuards` must-run bit + a NEW cross-method reach-weighting tier — none exist today; rig's hazards are all intra-method). "Hot" is a runtime fact rig can't see, so this is a disclosed **proxy**, not truth — high risk of the "structurally-true-but-179×-noise" failure mode; gate hard and calibrate against #2 or don't ship.

## The permanent ceiling (carve out, don't oversell)
Static fact detection can NEVER reliably do: **bytes / gen pressure / retained-vs-transient**, **resize churn** (capacity-vs-growth is runtime), and the synthesized allocations are only reachable via #3. Even fully built, this is a *profiler-pointing map with a measured hit-rate*, not an allocation oracle. It NARROWS where to point the gcdump; it never replaces it.

## Would v1 catch the motivating examples? (honest)
| example | v1 (`alloc_in_loop`, LINQ) | future arm | verdict |
|---|---|---|---|
| `BuildIndex` GroupBy/ToDictionary/… | NO (facts exist, none loop-gated — it's straight-line, hot per-query) | `alloc_hot_path` (#4) | needs reach-weighting; "hot" is runtime |
| `BuildIndex` `new List<>`×4 | NO (ctor structurally blind) | #1 + #4 | needs re-index + hot-path |
| recursive yield state machine | NO | #3 only | hard ceiling without `IOperation` pass |
| `Descendants` ConcurrentDictionary | partial (ctor fact → `alloc:collection`) | #4 | alloc visible; per-query coldness not |
| no-capacity collection resize churn | NO | never | hard ceiling (runtime) |

## Verdict
BUILD the **narrow opt-in v1** (LINQ `alloc_in_loop`, derive-side, zero re-index, ~0.5–1 day), measure on MedDBase, promote to default-on only at ≥85%. **DEFER** the ctor arm (re-index cost) and `alloc_hot_path` (new cross-method tier, weak precision) until v1 proves the hazard is useful AND the runtime-calibration loop (#2) exists. If v1 calibrates < ~80% or counts trivially on MedDBase → **keep it query-only, don't ship as a hazard.** Frame to consumers as "where to point the profiler," and set the expectation that it will NOT reproduce the gcdump's hot-path findings without #3/#4.

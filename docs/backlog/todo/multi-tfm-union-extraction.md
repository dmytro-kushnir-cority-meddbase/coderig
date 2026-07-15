# Multi-TFM projects: union extraction across target frameworks

## Current behavior (as of the PreferredResult fix, 2026-07-15)

A `<TargetFrameworks>a;b</TargetFrameworks>` project yields one Buildalyzer result per TFM (and can
yield a sourceless outer cross-targeting result). `SolutionSourceLoader.PreferredResult` picks the
**first result with sources** — deterministically the first declared TFM — and indexes only that
compilation. Before the fix, a blind `FirstOrDefault()` could land on the sourceless outer result and
degrade-abort the whole index (first hit: `MedDBase.CrossPlatform`, net48;net8.0).

## The gap

Single-TFM extraction is **lossy under conditional compilation**: members and call sites behind
`#if NET8_0` (any symbol set differing between TFMs) exist only in the non-chosen compilation and are
absent from the store — invisible to reaches/tree/impact. Empirically zero loss on MedDBase today
(2026-07-15: `MedDBase.CrossPlatform` has no preprocessor directives; the only other multi-TFM project
is a test project), but this silently becomes a recall gap the day someone writes `#if NET48` in a
dual-targeted library.

## Proposed fix: union extraction with site-level dedup

Buildalyzer's single `Build()` already returns ALL per-TFM results — no extra design-time-build cost.
For each multi-TFM project:

1. Build one Roslyn compilation per TFM result (each has its own preprocessor symbols + references).
2. Run fact extraction against EACH compilation.
3. **Union the facts, deduped at site level** (DocID + file:line for references/effects; symbol facts
   dedupe by DocID) — `#if`-gated extras survive, code common to both TFMs collapses to one fact, so
   "N occurrences = N call sites" stays honest (no ×2 inflation).
4. The WORKSPACE keeps exactly one project node (the preferred TFM's compilation) so dependents bind a
   single assembly identity — the union is extraction-side only.

Interim cheap disclosure (worth doing even before the union): when `PreferredResult` drops TFM results,
scan the project's sources for `#if`/`#elif` with framework-ish symbols and WARN that the non-chosen
TFM's conditional code is not indexed.

## Watch-outs

- Reference sets differ per TFM — a fact bound against the net8 reference closure may resolve a DocID
  differently (e.g. type forwarded); dedup must tolerate that or key on the syntactic site.
- Effects keyed to `#if`-gated enclosing members must still satisfy the call-graph-node invariant per
  compilation.
- `dispatch_facts` mined per TFM can genuinely differ (different impls available) — union is correct
  there too, same disclosed over-approximation as CHA.

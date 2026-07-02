# CLI: an ambiguous symbol pattern is resolved SILENTLY (wrong-tree / merged-results)

**Status:** DONE — shipped 2026-07-02. `AmbiguityNotice` (CLI) + `FactPathFinder.DistinctMatchTargets`
(Domain: match → drop contained lambdas → dedupe to param-free FQNs, so OVERLOADS never count as
ambiguity) emit a stderr note on `tree`/`callers`/`reaches`/`path` (both endpoints) when a pattern spans
>1 distinct symbol: `note: pattern 'X' matched N distinct symbols (…) — results span ALL of them; qualify
the pattern to narrow.` `tree` derives it from its BUILT roots so the note survives full cache hits.
Stderr keeps tsv/llm stdout machine-clean. Exact-match-wins was already shipped in `MatchNodes` (predates
this card — the third bullet of the fix was stale). The `callers` per-root reach-set SPLIT (grouping
results by matched root, beyond the existing `Matched nodes` list + this note) was deliberately NOT
built — disclosure covers the trust gap; grouping is a rendering rework to do if the note proves
insufficient. Tests: `AmbiguousPatternDisclosureTests` (7). Verified on the card's exact repro:
`rig tree BuildIndex` (rig's own store) now names both `IndexCommands.BuildIndex` +
`FactPathFinder.BuildIndex` before rendering; MedDBase `callers SubmitToHealthcode` disclosed 6 distinct
targets (+1-more capping); unambiguous and overload-only patterns stay silent; cache-hit runs still warn.
**Found:** 2026-06-28 (dogfooding `rig tree`/`callers BuildIndex` on rig's own store) · **Family:** cli-ux / correctness-of-disclosure

## The gap
The traversal commands match their `<pattern>` argument by substring/`Contains` over symbol ids, so one pattern
can resolve to MULTIPLE distinct symbols (name collisions, overloads, same method name in different types). When
it does, the commands handle it **silently and inconsistently** — the user can't tell they got an ambiguous or
"wrong" result.

Concrete repro (rig's own indexed source): `BuildIndex` matches BOTH `FactPathFinder.BuildIndex` (the graph
index) and `IndexCommands.BuildIndex` (the CLI `index` command — unrelated, same method name):

- **`rig tree BuildIndex` → renders ONE match, silently.** It drew `IndexCommands.BuildIndex`'s tree with ZERO
  notice that a second symbol matched, or that you got the "wrong" `BuildIndex`. You only notice if you happen to
  recognize the tree is the wrong method. (Had to qualify `rig tree FactPathFinder.BuildIndex` to get the intended one.)
- **`rig callers BuildIndex` → discloses but MERGES.** It printed `Matched nodes (19)` (good — both `BuildIndex`
  variants are visible), but then ran reverse-reach over the UNION, so the "76 reachers" conflate
  callers-of-`FactPathFinder.BuildIndex` with callers-of-`IndexCommands.BuildIndex` — you can't tell which reaches
  which target.

So `tree` hides the ambiguity entirely; `callers` shows the matched set but blends the results. Both are
"resolved silently" in the sense that nothing tells the user the answer spans/【picks-from】 multiple distinct symbols.

## Why it matters
Silent disambiguation is a correctness-of-DISCLOSURE bug: a reviewer/archaeologist reads a tree believing it's the
symbol they asked for, when it's a same-named sibling — or reads a merged caller set as if it's one target's. It's
exactly the "narrow honestly, don't over-claim" ethic violated at the CLI boundary. (rig already discloses
`~heuristic` dispatch, dispatch fan-out, truncation — pattern ambiguity should be disclosed too.)

## The fix (shared, one place)
Fix at the shared pattern-resolution layer (the `Contains`/match step feeding `TreeCommand`, `CallersCommand`,
`ReachesCommand`, `PathCommand`, etc.), so every traversal command is covered at once:
- When a pattern resolves to **>1 distinct symbol id**, emit a notice on stderr/stdout, e.g.
  `pattern 'BuildIndex' matched 2 symbols (FactPathFinder.BuildIndex, IndexCommands.BuildIndex); showing
  FactPathFinder.BuildIndex — qualify the pattern to disambiguate.`
- `tree`/`path` (single-root commands): pick deterministically (e.g. shortest id / ordinal-first) **with the
  warning**, OR require qualification when ambiguous. `callers`/`reaches` (set commands): keep the union but make
  the per-target split legible (the `Matched nodes` list is already a start — consider grouping results by matched
  root, or warn that results are merged).
- Exact-match should win over substring (an exact `FactPathFinder.BuildIndex` shouldn't also pull substring
  matches).

## Notes
- Low-risk, high-value CLI UX; no store/schema impact.
- Deferred at discovery because `TreeCommand`/the render path was under concurrent edit (branch-aware-effects M3
  render). Do against the shared matcher once that lands, or scope to the matcher (likely untouched) sooner.

## `callers`/`reaches` under-report hint: follow-ups

**Status:** todo
**Source:** split from the shipped callers-reaches async-underreport fix (2026-06-25)

### (a) Extend under-report footer to default `callers` path and `--roots`

Only `--entrypoints` has the async under-report footer today; `reaches` already discloses the scheduled bucket
under `--async`. Extending the footer to the default `callers` path and `--roots` is a small follow-up — it
reuses the same `AsyncReachableEpCount()` helper.

### (b) Fix #2: make `--async` the default (debatable)

An untaken, debatable fork. Not committed — assess separately.

# `rig index --include-tests` aborts the whole solution on one un-buildable test project

**Status:** PARKED / DONE-FOR-NOW (2026-07-08; moved out of the active queue 2026-07-19). Reopen when a
concrete workflow requires test projects in the graph; the accepted direction is still to make the build
phase honor explicit `projects.exclude`, never to silently drop degraded projects. · **Found:** dogfooding MR-!10898 reference-cut review — wanted test
projects indexed so rig could cross-check ReferenceTrimmer's "unused" claim on test-project cuts.
**Family:** index / robustness

## Symptom
`rig index MedDBase.slnx --include-tests` FAILS the entire index (0 usable store) because ONE test project,
`MMS.CrossPlatform.UnitTests`, produces 0 source files in this env (deterministic — 3/3 attempts) →
`DegradedBuildException` (`SolutionSourceLoader.cs:316`) aborts the whole solution. Without `--include-tests`
the same solution indexes fine (that test project is skipped), yielding 237 assemblies / 152-of-207 MR cuts
rig-grounded. So `--include-tests` is currently unusable on MedDBase: any single un-buildable test kills it.

## The abort is DELIBERATE — don't "fix" it by weakening it
`DegradedBuildException` is a designed fail-fast: a loud abort is *preferred* over a silently partial /
semi-successful index (the message says it — absent types → dependents fail to bind → a corrupt index that
looks fine). Same honesty ethos as the cache-schema discipline and "no silent caps." So the answer is NOT a
`--durable`/`--skip-degraded` that drops degraded projects with a warning — that reintroduces exactly the
silent semi-success the design rejects. (The README's `[--durable]` blurb oversells a mode that isn't — and
arguably shouldn't be — implemented.) The real friction is narrower and has an honest fix:

**The one genuine gap: you can't DELIBERATELY exclude a known-broken project when `--include-tests` is on.**
The build-selection test filter (`BuildSolutionProjectResults` → `IsTestProjectPath` gated by `excludeTests`,
`SolutionSourceLoader.cs:464`) is INDEPENDENT of `projects.exclude`/`IsExcludedProject` (the extraction filter
at `:99`), so adding the exact name to `projects.exclude` does NOT stop the build (confirmed — still aborted).

## Fix directions (honest ones only)
- **Make the build-phase filter honor `projects.exclude` too.** Then `--include-tests` + an exact-name exclude
  is an EXPLICIT, visible "index tests except this one I know is broken" — a deliberate opt-out, not a silent
  drop. Preserves fail-fast: an *unexpected* degraded project still aborts loudly. This is the right lever.
- **Fix the env root cause** (the actually-correct fix): why does `MMS.CrossPlatform.UnitTests` design-time-
  build to 0 source here — cross-platform TFM excluded on Windows? unrestored? A `dotnet restore`/binlog tells.
  Not rig's bug; rig is correctly refusing to index a project whose sources it can't see.
- Do NOT add a degraded-project auto-drop. Fail-fast is the feature.

## Why parked
The MR-review value of test rig-facts is marginal — test-project cuts are already reflection-risk-tiered via
grep, and the verdict (75 SAFE / 132 VERIFY) was stable with/without test indexing. Not worth blocking on the
degraded-abort yak-shave. Revisit if a use case needs test projects in the graph.

# coderig docs — index

Navigation for agents and humans. These docs describe the current system and open work. Completed handoffs
and superseded design docs were removed once their work shipped — recover them from git history if needed
(`git log --diff-filter=D --name-only`).

## Start here
- **[rig-review-issues.md](rig-review-issues.md)** — the canonical **issue/fix register**: audit findings,
  shipped fixes (dated, with commits + verification), and the open backlog. *This is the todo/fix tracker.*
  Latest shipped batch: "SHIPPED 2026-06-16" (Echo actor + Flurl route effects #16, SOAP dedupe #17,
  generic monomorphization #22/#23, derive truncation marker #24). One item still open: **#10** (Tier-1
  global flags).
- **[progress.md](progress.md)** — lighter-weight implementation task tracker (milestone checkboxes).

## Reference (current system)
- **[ubiquitous-language.md](ubiquitous-language.md)** — shared vocabulary / glossary. Read first if a term is unclear.
- **[mvp-spec.md](mvp-spec.md)** — product wedge + command flow (the "what rig is for").
- **[query-strategy.md](query-strategy.md)** — query storage & retrieval strategy; baseline perf analysis.
- **[fact-layer-refactor.md](fact-layer-refactor.md)** — the staged fact-layer pipeline; the **current architecture direction** (immutable facts → derived tables, no JSON blobs).
- **[effect-capture-validation.md](effect-capture-validation.md)** — effect-capture validation method + the gap inventory (ground-truth findings).
- **[async-model.md](async-model.md)** — background-processor surface (the 9-mechanism zoo) + the async/handoff model + the phased roadmap (phases 0–3 shipped; 4 partial; 5 open). Unifies the former three async docs.

## Design / future (not yet built)
- **[multi-solution-storage.md](multi-solution-storage.md)** — assembly-keyed unified store (Option C). Decided, partly implemented.
- **[incremental-indexing.md](incremental-indexing.md)** — incremental/cached indexing end state. Deferred (cache-invalidation is the hard part).

## Audits & bugs
- **[audits/](audits/)** — dated recall / entry-point-detection audit reports, ground-truthed against MedDBase source.
- **[bugs/](bugs/)** — resolved bug post-mortems (all three RESOLVED/FIXED — kept as worked examples of misdiagnosis vs. real root cause).

> Removed (in git history): the completed handoffs (`coderig-gapfix-mine`, `rig-tree-denoise`,
> `deadcode-and-skill`, `exact-dispatch-facts`, `receiver-type-dispatch-narrowing`, the async build spec +
> phased plan) and superseded designs (`handover`, `sqlite-persistence-notes`). Their shipped substance is
> reflected in `rig-review-issues.md` and `async-model.md`.

# coderig docs — index

Navigation for agents and humans. These docs describe the current system and open work. Superseded docs are
moved to [archive/](archive/) (not deleted).

## Start here — work tracking
- **[backlog/](backlog/)** — the **kanban**: `todo/ | progress/ | done/`, one issue per file, the index is
  `ls backlog/*/`. This replaced the old `rig-review-issues.md` register + `progress.md` tracker.
- **[rcas.md](rcas.md)** — production-incident RCA index → detectors / review invariants (generic-detector
  vs domain-bound split).

## Reference (current system)
- **[ubiquitous-language.md](ubiquitous-language.md)** — shared vocabulary / glossary. Read first if a term is unclear.
- **[fact-layer-refactor.md](fact-layer-refactor.md)** — the staged fact-layer pipeline; the **current architecture direction** (immutable facts → derived tables, no JSON blobs).
- **[effect-capture-validation.md](effect-capture-validation.md)** — effect-capture validation method + the gap inventory (ground-truth findings).
- **[async-model.md](async-model.md)** — background-processor surface + the async/handoff model + phased roadmap (phases 0–3 shipped; 4 partial; 5 open).
- **[hazards.md](hazards.md)** — hazard-detector design + catalog.
- **[build-cache.md](build-cache.md)** — design-time build cache (on by default), Paket/CPM invalidation.
- **[multi-solution-storage.md](multi-solution-storage.md)** — assembly-keyed unified store; `rig index --merge` (slices 1+2 shipped, slice 3 open).
- **[query-strategy.md](query-strategy.md)** — query storage & retrieval strategy. *(Note: the cold-`tree` baseline timings predate the SQLite pragma fixes — verify against the current store.)*
- **[delivery-rules.md](delivery-rules.md)** / **[FIX-event-raise-overapproximation.md](FIX-event-raise-overapproximation.md)** — delivery-edge model + the event-raise over-approximation fix.
- **[design-impact-behavioral-diff.md](design-impact-behavioral-diff.md)** — behavioral diff via immutable per-commit stores.

## Design / future (not yet built)
- **[incremental-indexing.md](incremental-indexing.md)** — incremental/cached indexing end state. Deferred.
- **[index-pipeline-fusion.md](index-pipeline-fusion.md)** — fuse compile/read/extract passes. Proposal.
- **[memory-optimization-strategies.md](memory-optimization-strategies.md)** — ranked index memory/wall-time strategies + empirical results.

## Audits & bugs
- **[audits/](audits/)** — dated recall / entry-point-detection audit reports, ground-truthed against MedDBase.
- **[bugs/](bugs/)** — bug post-mortems: most FIXED (kept as worked examples); `impact-base-store-ep-data-loaded-twice` is OPEN; `rules-loadforsolution-no-memo` is documented WON'T-FIX.
- **[meddbase-bug-corpus.md](meddbase-bug-corpus.md)** — triage of 100 MedDBase bugs against rig detector families.
- **[ux-research-2026-06.md](ux-research-2026-06.md)** — UX panel findings.

## [archive/](archive/)
Superseded / historical docs (preserved, not current): `rig-review-issues.md`, `mvp-spec.md`, `progress.md`,
`design-dispatch-precision.md`, `bug-callers-path-overreach.md`. See [archive/README.md](archive/README.md).

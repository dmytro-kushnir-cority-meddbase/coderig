# Verified detected bugs

Bugs **rig detected** and that have been **confirmed against the actual source** (not just structural candidates).
This is distinct from:
- `docs/backlog/` — rig *feature* work (detectors, perf, CLI).
- `meddbase-analysis/docs/rca-corpus-meddbase.md` — *historical* production reverts/fixes mined for detector coverage.

Here each file is a **live defect in the target codebase that a rig detector surfaced and a human/agent verified in source**.
One bug per file. Index is derivable from `ls` (kanban-style). Promote to a filed GitLab issue, then record the issue id.

| bug | detector | site | severity | filed |
|---|---|---|---|---|
| [paginator-partial-init-publish](paginator-partial-init-publish.md) | lazy_init_race | PdfService/PdfService2 `Paginator.Initialise` | medium (concurrency) | not yet |
| [performance-logger-double-startup](performance-logger-double-startup.md) | lazy_init_race | MMS `PerformanceLogger.get_Factory` | medium (concurrency) | not yet |
| [contact-removepersoncontactlinks-cache-coherence](contact-removepersoncontactlinks-cache-coherence.md) | cache_coherence (FR-7) | `ContactEntity.RemovePersonContactLinks` | high | candidate filed (GitLab, by Dmytro) |

**Verification bar:** the source was read and the failure mode confirmed reproducible in principle — NOT "the detector
fired." A detector firing is a candidate; an entry here means the underlying code genuinely exhibits the hazard.

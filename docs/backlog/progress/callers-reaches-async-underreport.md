## `callers`/`reaches` silently under-report when sync hides the async/scheduled surface (BUG-rig-missed-entrypoints-healthcode Defect 2)

A sync `rig callers <m> --entrypoints` that yields **0** reads as "not reachable from any entry point" — but the
entire scheduled + actor/message-dispatched surface is sync-cut by default and only appears under `--async`. In
the real case `Master.GetMedicalPerson --entrypoints` returned 0 sync but **92 under --async** (89 action + the
background worker + http + soap); `GetCompany` 16 → 219. For a security/authorization reachability question this
is actively misleading — a reviewer could wrongly de-risk a change. (Defect 1, the `[ClientBinding]` EP miss, is
fixed per-repo; this is the orthogonal engine half.)

**STATUS (2026-06-25): fix #1 SHIPPED for `callers --entrypoints` (both halves), validated on the fresh store.**
The 0-EP half was already in place (probe `--async`; "0 sync — but N via async; re-run with --async"). This
pass added the **non-zero under-report** half: a `AsyncReachableEpCount()` helper (one extra reverse `ReachedBy`
in AsyncExact, gated to SyncCut + graphs that actually contain handoff edges) and a footer
`… +K more entry point(s) reach this via async/scheduled handoff (not shown) — re-run with --async` whenever the
async surface reaches strictly more EPs than the sync set. Test: `CallersAsyncUnderreportTests` (event-`+=`
handoff playground fixture — `Task.Run(methodGroup)` is walked SYNC here, so it does NOT trigger; the
sync-cut handoff had to be an event subscription, auto-reclassified by `MarkEventSubscriptionHandoffs`).
Real-store: `Master.GetCompany` sync 14 → "+14 more" → `--async` 28; `Master.GetMedicalPerson` 1 → "+5" → 6.
Note the backlog's "handoff-skipped count already computed in FactPathFinder" was WRONG — no such count is
exposed; the async re-probe (the proven 0-case pattern) was used instead.

**Residual (not done):** the DEFAULT `callers` path and `--roots` still have no under-report hint (only
`--entrypoints` does); `reaches` already discloses the scheduled bucket under `--async`. Extending the footer to
default/`--roots` is a small follow-up if wanted (same helper). Fix #2 (`--async` default) remains an untaken,
debatable fork.

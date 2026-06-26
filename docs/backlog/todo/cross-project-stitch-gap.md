## Cross-project call-edge stitch gap (F5)

**Status:** todo — VERIFY against multi-solution `rig index --merge` before building
**Source:** extracted from `docs/rig-review-issues.md`, 2026-06-25 (F5 from the audit register)

### Finding

**F5 — Cross-project call edges dropped (scoped-mine stitch gap).** When a solution is indexed with
`--from <entry.csproj>`, projects outside the scoped set are not mined; call edges that cross the scope
boundary are silently dropped. Evidence: `SubmitToHealthcode → ExportQueue.*` — `Core.Background` was not
stitched to `Workflows` in a single-project mine.

Severity: Medium. The effect is silent truncation of the call graph at project-scope boundaries.

### Before building: verify against multi-solution `--merge`

The 2026-06-25 session introduced `rig index <sln> --merge --rules r.json` which accumulates one run per
solution into a commit-scoped store so queries span all. MedDBase now indexes 11/20 solutions via `--merge`
(see `docs/backlog/done/session-2026-06-25-findings.md`). **This may already resolve F5**: if
`Core.Background` and `Workflows` are both indexed as separate solution runs under `--merge`, their call
edges are in the same store and cross-project reaches should join.

**Verification step (do before designing a fix):**
```
# from c:/git/meddbase-analysis
rig reaches "SubmitToHealthcode" --only queue
```
If `ExportQueue.*` effects surface, F5 is resolved by `--merge`. If they're still missing, the stitch gap
survives merge and needs a dedicated fix.

### Fix direction (if --merge does NOT resolve it)

The per-project mine emits reference facts against `SymbolId`s that are defined in OTHER project runs. At
query time the call-graph join must resolve cross-run symbol ids to their owning-run nodes. Root cause
likely: `call_edges` materialization is per-run and doesn't stitch cross-run edges at graph time. Fix would
be in `FactGraphProjection` / `GraphMaterializer` — cross-run edge stitching after `--merge` accumulates
multiple runs.

## Findings — session 2026-06-25 (cache-coherence EP trace, render & multi-solution index)

Surfaced tracing GI-#4460 (the FR-7 `RemovePersonContactLinks` / Person finding) end-to-end + a whole-repo
multi-solution index sweep. #4460 closed as not-a-live-defect (all callers guarded or CHA-fan phantoms); the
structural FR-7 signal is correct (verified: Person bulk_write, only `ContactCache`/`EntityCache`/`ItemCache`
invalidated on the path, never `PersonCache`).

- **`cache_coherence`/`event_cycle` not in per-EP `tree --view hazards`** → extracted to
  [cache-coherence-per-ep-hazards.md](cache-coherence-per-ep-hazards.md).
- **~~opaqueType render-collapse HIDES effects inside a redirected-override subtree~~ — RETRACTED (2026-06-25):
  NOT a render bug; an index/cache-time artifact.** Initially diagnosed as a render-rule bug (the override body
  + `ContactCache.Remove` were missing from `tree` while the ORM-runtime `opaqueType` was active). Could NOT be
  reproduced afterward: re-layering the `M:SD.LLBLGen.Pro.` opaqueType via `--rules` on the current store +
  `--no-cache` still renders `ContactEntity.Delete «via ORMSupportClasses.EntityBase»` → `RemovePersonContactLinks`
  → `cache:invalidate ContactCache` in full; the opaque rule only collapses a genuinely-internal `EntityBase.Save`
  subtree, NOT the redirected first-party override. The single-impl fold (`FoldSingleImplHops`) promotes the
  narrowed override into the base hop's slot BEFORE render, so the rendered node is `ContactEntity.Delete` (not
  opaque-matching) and its subtree shows. opaque is render-only and never read at index (rig-on-rig: the index
  command has NO path to `MatchOpaque`; only `TreeCommand`→`TreeRenderer` reaches it). **Actual cause = a stale
  MATERIALIZED GRAPH (index/graph-time), NOT the query cache first guessed:** the SQL-fast-path `tree`/`reaches`
  walk the PERSISTED `call_edges`, into which `redirectRules` are BAKED at graph-materialization
  (`IndexCommands.MaterializeGraphAsync` → `FactGraphProjection.FromAnalysis` → `RedirectClassifier.Redirect`;
  `GraphMaterializer.BuildAsync` bakes them into `call_edges`). A graph materialized BEFORE the redirect rule
  existed lacks the baked redirect edge → the forward walk dead-ends at the un-indexed external base → override
  body absent. A re-graph (today's `rig index --merge` re-materialized MedDBase) bakes the edge → body appears.
  **Lesson: redirect is index/graph-time (baked into `call_edges`), NOT query-time for the SQL fast path — after a
  `redirectRules` change, `rig graph` (re-materialize) before trusting forward reachability.** No render fix
  needed; the fold + opaque interaction is correct.
- **`rig index --from` 0-projects crash** → extracted to
  [index-from-zero-projects-crash.md](index-from-zero-projects-crash.md).
- **Multi-solution unified store now in use + documented** (skill updated): `rig index <sln> --merge --rules
  r.json` accumulates one run/solution into the commit-scoped store; queries span all. MedDBase merged 11/20
  solutions clean (`cache_coherence` across them = unchanged 4, all the one reviewed-benign case); net48-web +
  `.sqlproj` are the skips (see the cursed item at the very bottom). A single-solution store silently makes
  other solutions' EPs invisible → false dead-code / phantom reach, so index all app solutions before trusting
  cross-product reachability.

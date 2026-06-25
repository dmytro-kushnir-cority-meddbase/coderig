## Monomorphization rework — ✅ SHIPPED (2026-06-25)

The static-monomorphization rework shipped transitive + lambda-closure materialization (real-store validated:
`DebtorOverride.SaveIncludedServices` 665→38 reach, verified sound) on a uniform EF load path behind the
`meta` schema-version gate, then went unconditional + FP-calibrated. Shipped sub-items below. The remaining
SUBSTRATE work (graph-time materialization end-state; forward≡reverse real-store validation) is NOT here — it
lives in [../progress/dispatch-precision-substrate.md].

### Re-enable the SQL reads fast path (binding-aware) — ✅ DONE (2026-06-25, `71f35478`)
`SqlReachability.LoadGraphFromReachSetAsync` now re-attaches `DeclaringTypeArgBinding`/`MethodTypeArgBinding`
from `reference_facts` onto the bounded `CallEdge`s (one bulk pass keyed by caller/callee/line, alongside
`TypeArguments`), so the ShapeGraph monomorphization seam fires on the bounded graph. `SqlFastPathEnabled =
true`. The CTE only BOUNDS the load; the in-memory FactPathFinder+ShapeGraph runs over it, and reach_set is
the CHA SUPERSET so it reproduces the full-EF narrowed reach (`Bounded_graph_reproduces_full_graph_reach` +
`Sql_*` equivalence tests). Real-store: `DebtorOverride` → 38 via the SQL path, ~14s vs 22s full-EF.

### Materialize the monomorphized subgraph at `rig graph` time — ➡️ OPEN, moved to the substrate tracker
The next perf lever (bake `~mono` into persisted `call_edges`/`dispatch_edges` at `rig graph` time so the CTE
walks an already-narrowed graph). This is the substrate's end-state and now lives in
[../progress/dispatch-precision-substrate.md] (item 1) — not duplicated here.

### Single static SQL connection across the app — ❌ WON'T DO (2026-06-25)
Decided not to pursue. Each query opening its own `RigDbContext`/connection is fine; the warm shared-connection
win doesn't justify the shared-static-state cost.

### forward ≡ reverse on the real store — parked tests reconciled ✅; real-store validation OPEN (substrate tracker)
The 8 parked reverse-dispatch tests are **✅ RECONCILED (2026-06-25, `cc9a529b`)** — un-skipped and fixed to
the narrowed truth: the reverse walk excludes CHA phantoms (forward≡reverse on those seams) and
dispatch-declaration waypoints (interface/base-virtual decls aren't caller-origins), keeping the real
caller/EP assertions. Suite has **zero** skips now. The real-store forward≡reverse validation at scale is OPEN
and tracked in [../progress/dispatch-precision-substrate.md] (item 2).

### Monomorphization FP-calibration before trusting on-by-default — ✅ LIVE (2026-06-25)
**Went live: the `Reads.MonomorphizeEnabled` toggle is REMOVED — monomorphization is unconditional.** A/B on
the fresh store (`MonomorphizeEnabled` flipped for the OFF baseline): `DebtorOverride.SaveIncludedServices`
7861 → 175 reachable methods (type-parameter fan, narrowed); `BillingRuleHelper.SaveServices` 7843 → 7614 and
`Master.GetCompany` 638 → 601 (mostly base-virtual, irreducible); control `ContactEntity.RemovePersonContactLinks`
13 == 13 (non-generic, zero spurious change). An **independent adversarial verifier** (fresh-context agent,
read-only source + `rig`) returned **SOUND, high confidence**: the DebtorOverride drop is on the change-log
virtuals inside `CommonEntityBase.Delete → GetChangeLogger` (overridden by 32 entities); the narrowing pins the
receiver to `BillingRuleDebtorOverrideServiceIncludedEntity`, a LEAF type that overrides none of them, so the
dropped Person/Invoice/Company closures are genuinely No-path. All three false-negative vectors (multi-valued
type-arg, wrongly-narrowed virtual, reflection-bound dispatch) ruled out with source evidence; the helper's 3
distinct instantiations stay independent (validates per-instantiation materialization). Second clean check
(the first Haiku pass wasn't persisted) → shipped.

**Residual (non-blocking, no longer gating):** clone count + the per-method (50) / total (100k) caps are
unmeasured at real-store scale (no direct command — would need light instrumentation); a broader sweep beyond
DebtorOverride was not run (the verifier + control were judged sufficient to go live). A/B calibration now
requires a temporary local edit (no runtime toggle).

### Misc rework debt
- **Re-index MedDBase**: ✅ DONE (2026-06-25) — fresh single store `caa9373ffbf6-dirty` on the new schema
  (377,512 symbols / 2,123,817 references / 145 di), all prior stores dropped. Query side is unblocked.
- **`<T,U>` label gap**: plain method-generic instantiation labels don't render concrete even on the EF path
  (`PrettyGenericName` / renderer, separate from narrowing + load-path).
- **Phase-3 collapse of mono-lambda ids**: ✅ VERIFIED on the store (2026-06-25) — no `~mono`/`{M}~λN~mono⟨…⟩`
  ids leak into `tree`/`callers`/`reaches`/`path` output in ANY format (text, `--format tsv`, `--format
  llm-ids`), checked against materializing targets (`DebtorOverride.SaveIncludedServices`,
  `BillingRuleHelper.SaveServices` incl. its Func lambdas). `MonomorphCollapse` folds them as intended.
- **CallersCommand auxiliary `ReachedBy` sites** (≈203/371/420) left un-collapsed: ✅ confirmed HARMLESS — those
  sites build set-membership/filter sets (forward-verify target ids, the async re-probe), never rendered, so
  un-collapsed `~mono` ids there cause no leakage (verified above). No wrap needed.
- **`SchemaVersion.Index`/`.Graph` bump discipline**: the gate is only safe if the C# consts are bumped on a
  schema-shape change (that's the whole tripwire).
- **Cleanup**: `StorageProbes` header comment still mentions `ADD COLUMN` (stale post column-probe removal);
  `TableExistsAsync` now used only by `SchemaMeta` bootstrap + `Writes` assemblies merge-bootstrap.
- **EntryPointSiteStore** `entry_point_sites_meta` probe kept (rules-hash cache, orthogonal to schema
  version) — could fold into a rules-hash stamp later.

## FR-7 cache-coherence — ✅ SHIPPED, FILTERED, CALIBRATED; the single finding was a reviewed FALSE POSITIVE (2026-06-25)

**STATUS: built + the generated-ORM filter shipped + calibrated (48 generated → 4 app-code candidates); the one
real-code finding was triaged and DISPROVEN by code review (benign staleness — see below).** A clean
demonstration that FR-7 is a disclosed CANDIDATE generator, not a verdict. Migrated onto `FactCorrelationDeriver`
(the CEP absence-join — cache_coherence is its first
instance), NOT the original `FactCacheCoherenceDeriver` framing. Stays rule-gated (fires only when a repo
supplies a `cacheCoherence` rule); MedDBase `rig.rules.json` carries one. Correct posture — `cachedEntities`
is repo-specific, so this is never a builtin/on-by-default rule.

- **Mechanism:** a reach-join — a BULK write (`UpdateMulti`/`DeleteMulti`/`UpdateEntitiesDirectly`) to a cached
  entity X whose forward closure reaches no `XCache` invalidation (`Remove`/`RemoveKey`/`Clear`/`Invalidate`).
  Confidence `medium` (heuristic name-pairing + forward/depth-capped reach).
- **`excludeEnclosingNamespaceSuffix` filter SHIPPED** (deriver `FactCorrelationDeriver.ExcludedByNamespace` +
  `FactCacheCoherenceRule.ExcludeEnclosingNamespaceSuffix` schema/provider): drops bulk-writes whose ENCLOSING
  method is generated ORM plumbing (`.CollectionClasses`/`.DaoClasses`). This was the "next unit" — done.
- **Calibration (fresh store, 2026-06-25): 48 generated-`CollectionClasses` candidates → filtered to 4 real
  app-code findings**, all high-confidence, all `ContactEntity.RemovePersonContactLinks` (hand-written
  `MMSEntityClasses`, lines 80/84/88/92), `bulk_write_without_cache_invalidation` on the `Person` cache.
- **Triaged → FALSE POSITIVE (disproven by code review on the actual path, agent 2026-06-24).** rig's
  structural signal IS present — `RemovePersonContactLinks` does 4 `UpdateMulti` bulk writes to `PersonEntity`
  (nulling Fk{Insurer,Employer,Legal,School}Contact) bypassing save-hooks, and NO `Person`-cache invalidation
  is reachable from the EP (`Contact/Edit.Delete`) in EITHER sync OR async reach (verified 2026-06-25), INCLUDING
  the `DoWhenCommitted` post-commit callbacks — rig DOES walk those (the lambda body is a `~λ0` node reached by
  a methodGroup edge at the registration site; proven here by the EP reaching `ContactCache.Remove` at :68
  THROUGH the DoWhenCommitted lambda). So the no-invalidation signal is ACCURATE, not a reachability gap. But the
  staleness is UNOBSERVABLE, so it is not a bug: the nulled FKs point at the contact being deleted, whose own
  cache is removed post-commit (`DoWhenCommitted(() => ContactCache.Remove(pkContact))`, ContactEntity.cs:68);
  a cached PersonRecord holding a stale Fk*Contact still resolves via `GetContactRecord → ContactCache` to
  EMPTY (contact gone) — identical to a null FK. No wrong data is ever shown. [Exact reasoning per the prior
  code review — confirm/refine.]
- **FP CLASS for FR-7 (record this):** "bulk write with no reachable invalidation" is a STRUCTURAL signal that
  the detector cannot clear semantically. Benign-staleness — a stale FK whose target is also being deleted (so
  it resolves to the same empty result), a cached projection that's never read on a path where it matters, or
  a value that's overwritten before any read — are FPs no reach/name-pairing heuristic can rule out. FR-7
  stays a DISCLOSED CANDIDATE generator (medium confidence), never a verdict; semantic review is required per
  finding. This single MedDBase finding was a candidate, reviewed, and cleared.
- **Calibration rule data** (in MedDBase `rig.rules.json`): `cachedEntities` = the ~36 `*Cache` types stripped
  of `Cache`; `bulkWriteMethods` = UpdateMulti(/Async)/DeleteMulti/UpdateEntitiesDirectly(/Async);
  `invalidationMethods` = Remove/RemoveKey/Clear/Invalidate; `excludeEnclosingNamespaceSuffix` =
  CollectionClasses/DaoClasses.
- **Residual (non-blocking):** the 3 deriver limits remain noted — substring-seed (`ReachesFromEachSeed`),
  `InvalidationReachable` O(mutations×edges) perf (fine at 4 findings), `Reaches maxDepth=20` deep-invalidation
  FP. Cross-resource (write on Document ⇒ invalidate Person, GI-4199's harder half) is still the open
  enhancement — this finding is the SAME-entity case (Person write ⇒ Person cache), which FR-7 fully covers.

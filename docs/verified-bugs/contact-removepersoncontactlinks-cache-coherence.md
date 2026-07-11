# ContactEntity.RemovePersonContactLinks — bulk write without cache invalidation

**Detector:** `cache_coherence` (FR-7) · **Verified:** 2026-06-26 (source + tree trace) · **Severity:** high
**Site:** `MedDBase.DataAccessTier.EntityClasses.ContactEntity.RemovePersonContactLinks` — `src/main/MedDBase.DataAccessTier/MMSEntityClasses/ContactEntity.cs:80` (×4)

## Defect
Four `llblgen:bulk_write` to `PersonCollection` (clearing person→contact FK links across Insurer/Employer/Legal/School)
whose forward closure contains **no `cache:invalidate` of `PersonCache`**. The bulk `UpdateMulti`-style writes bypass the
per-entity `PersonCache.Remove` path, so `PersonCache` can serve stale person rows after the links are cleared.

```
rig tree <EP> --view hazards →
  ContactEntity.RemovePersonContactLinks  {🗄️ llblgen:bulk_write CollectionClasses.PersonCollection ×4, …}  ⚠ cache_coherence(high)×4
      Person  <- ContactEntity.RemovePersonContactLinks  ContactEntity.cs:80  [bulk_write_without_cache_invalidation] ×4
```

Reachable from concurrent IIS entry points (e.g. `Contact/Edit.Delete`). This is the canonical/calibration case for the
FR-7 `cache_coherence` detector (`FactCorrelationDeriver` absence-join), and the case that motivated wiring graph-tier
hazards into `tree --view hazards`.

## Caveat (the `[LLM]`/human-confirm step)
The detector confirms the *structural* gap (write without a same-key invalidate on the path). A true defect also requires
that nothing invalidates `PersonCache` out-of-band. Verified here as a real gap; the filed candidate is the record of that
human judgement.

## Status
Bug candidate filed in GitLab (git.meddbase.com) by Dmytro Kushnir. (Add the issue id here.)

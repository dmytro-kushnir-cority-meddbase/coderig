## Detector: `static_init_capture` — config/mutable value frozen in a static field initializer (NEW, corpus GI-862)

**Status:** proposed · **Family:** staleness/cache-coherence (sibling to FR-7) · **Found:** 2026-06-24 (MedDBase GI-862 RCA)

### The hazard
A value that can change at runtime — a feature flag, a `Settings.*`/config read, or anything derived from
one — is captured into a `static` / `static readonly` **field initializer**. Field initializers run **once**
at CLR type-initialization and never re-evaluate, so the value is frozen for the AppDomain lifetime. The
symptom is the classic *"wrong until the app is restarted"* — a restart re-runs the static initializer.
Distinct from FR-7: FR-7 is a missing *invalidation* of an instrumented cache; this is a value baked into
**immutable static state with no invalidation surface at all** (there is nothing to invalidate — only a
process recycle clears it).

### Corpus case — GI-862 ("Cache in consultation … wording not updated until app restart")
- Flag: `Configuration.Settings.DisableTentativeForClinicalDecisionSupport` (persisted + cluster-propagated
  correctly on save — the config layer is NOT stale).
- Display wording derived from it: `ConceptView.TentativeText`, `PatientSnomedCodes.TentativeTextSetting`.
- Of the 4 read sites of those props (store `caa9373f`, `reference_facts`), exactly **one** has an `F:`
  (field) enclosing symbol — `F:ConceptView.ConceptStatus @ ConceptView.cs:92`, a `static readonly DomMap`.
  That one freezes; the other 3 are `M:` method/getter bodies that re-evaluate live. The single `F:` vs `M:`
  distinction in the fact table IS the bug (form view stale, list view live).
- **Prevention nuance:** rig would NOT flag the *original* defect (a missing `if` is not a modelled hazard).
  It WOULD flag the *incomplete fix* — wiring `TentativeText` to read `Settings.*` while leaving
  `ConceptStatus` a `static readonly` capture — which is exactly the trap (fix the property, miss the static
  capture, still-broken-until-restart). That is the high-value framing: catch the insufficient fix.

### Pattern (CEP form)
`co-presence(read of T, enclosing(read) is a static-field initializer)` where `T` transitively reads a
`config:*`/`Settings.*` effect (or a flagged "mutable source"). Window = the field initializer. Single-event
classification with a taint condition; no ordering needed → not gated on the dispatch-precision substrate
like the ordering operators are.

### Prerequisites / what rig needs
1. **Static-field-initializer enclosing identity.** `reference_facts.EnclosingSymbolId` already carries the
   `F:` prefix; need to confirm rig can tell a `static` field init from an instance field init (instance
   fields re-run per construction, so only `static` qualifies). May need a `static` modifier fact on the
   field symbol, or treat the synthetic `.cctor`/type-init enclosure as the signal.
2. **Mutable-source taint.** A rule-declared set of "mutable sources" — `Settings.*` getters, config
   providers, feature-flag reads — and transitive reachability from the captured expression to one of them.
   (Today `TentativeText` hardcodes a constant, so there is no taint to trace until the flag-read fix lands;
   the detector is exercised against the *fixed* tree.)
3. FP calibration: legitimate static caches of genuinely-immutable derived constants must not trip it — gate
   strictly on the mutable-source taint, and consider excluding `const`/compile-time-constant captures.

### Validation
Build the rule against store `caa9373f` *after* adding the `Settings.DisableTentative…` read to
`ConceptView.TentativeText`; assert exactly one hazard at `F:ConceptView.ConceptStatus @ :92` and none at the
three `M:` sites. Synthetic fixture: a `static readonly` field initialized from a `Settings`-backed property
vs a method returning the same — only the field trips.

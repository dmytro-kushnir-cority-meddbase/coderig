## Feature: CEP over effects — a pattern engine for hazard detectors ⭐ HIGHEST PRIORITY (deferred)

**Status:** highest-priority forward work, DEFERRED (gated on the dispatch-precision substrate fix in
[bug-callers-reverse-overreach.md](../../bug-callers-reverse-overreach.md) + a design pass). Conceptual model:
memory `project_coderig_effect_correlation_model` + the shipped `FactCorrelationDeriver` (FR-7
`cache_coherence` is its first instance).

### Idea
Treat the program's effects as an EVENT STREAM and express hazard detectors as declarative PATTERN QUERIES
over it (Complex Event Processing). `FactCorrelationDeriver` is already one CEP operator (absence-join);
generalize it into a small operator set + a JSON pattern DSL, and migrate the bespoke hazard derivers onto it.

### Mapping
- **event** = effect (`provider:op` + resource + enclosing + location + structural context already captured:
  loop, `EnclosingScopes` (using/lock), `EnclosingInvocations`).
- **correlation key** = `ResourceKey` (resolve + normalize).
- **"time"** = reachability + lexical happens-before (NOT wall-clock).
- **window** = method | forward-tree | EP-reach | held-scope | loop.
- **operators** = absence · co-presence/join · divergence (XOR) · sequence (followed-by) · aggregate-in-window.
  `dominance` is OUT (needs a CFG rig doesn't have).

### Detectors as patterns (migration map)
- `cache_coherence` = absence(bulk_write, cache:invalidate, key, fwd-tree) — **shipped instance**
- `dual_write` = divergence(db_write, cache/index/bus_write, key, common-origin)
- `read_before_commit` = sequence(read, commit, key, method)
- `N+1` = aggregate(read, loop-window)
- `event_cycle` = closure over delivery edges — migrate `FactCycleDeriver`
- `lock_coverage` = dominance(mutate, acquire) — OUT until a CFG pass exists
- `static_init_capture` = co-presence(config/mutable-derived read, enclosing = static-field initializer) — config-derived value frozen at type-init → stale until restart (see dedicated section below; corpus: GI-862)

### Phased plan
0. design doc (`docs/design-effect-cep.md`): event model, reachability-as-time + path-insensitivity ceiling, operators, migration map.
1. generalize the operator seam in `FactCorrelationDeriver` (relation/polarity → operator set).
2. windows first-class (method/tree/EP/held-scope/loop).
3. JSON `patterns` DSL (the composability path — CEP *is* the deferred DSL).
4. migrate bespoke derivers onto it (golden-oracle byte-equivalence; delete each as migrated, as `FactCacheCoherenceDeriver` was).
5. new detectors as pure data (dual_write, …); FP-calibrate each on the real store before on-by-default.

### Hard constraints
- **Substrate dependency (why deferred):** CEP runs over the reachability graph; the dispatch
  over-approximation (the `base.M()` ×49 fan + receiver-less calls) pollutes happens-before → phantom pattern
  matches. The dispatch-precision fixes are a **prerequisite** for trustworthy CEP.
- **Path-insensitivity ceiling:** ordering is structural reachability, not execution → sound findings
  (structural absence/presence), **unsound clears**; `dominance` needs a CFG. Disclose, don't pretend.

### First slice (after the design doc)
Add the `sequence` operator + migrate `read_before_commit` (today an observation) onto it — proves the
abstraction generalizes beyond `absence` before touching the DSL.

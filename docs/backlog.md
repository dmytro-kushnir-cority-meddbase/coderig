# rig — feature backlog

Forward-looking feature specs not yet scheduled. Distinct from
[rig-review-issues.md](rig-review-issues.md) (the MR-!10645 audit punch-list). Promote an item to a branch
+ commits when picked up; convert to a GitHub issue (`gh issue create`, remote `dv00d00/coderig`) if tracked
externally.

---

## Feature: CEP over effects — a pattern engine for hazard detectors ⭐ HIGHEST PRIORITY (deferred)

**Status:** highest-priority forward work, DEFERRED (gated on the dispatch-precision substrate fix in
[bug-callers-reverse-overreach.md](bug-callers-reverse-overreach.md) + a design pass). Conceptual model:
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

---

## Feature: Dispatch-fan disclosure + generic monomorphization (the dispatch-precision substrate)

**Status:** proposed · **Found:** 2026-06-24 (reverse receiver-narrowing fix `c7fe4f0f` + the design dialogue).
This is the concrete spec for the "dispatch-precision substrate" the CEP feature is gated on, and it folds in
the old perf note (precise full-closure reverse `O(E)` not `O(N×reach)`) and the forward/reverse asymmetry.

### Why
After per-edge receiver narrowing (`c7fe4f0f`), forward and reverse share the dispatch PRIMITIVE
(`DispatchTargets`) but are not the same ALGORITHM: forward is a context-carrying traversal (threads
`incomingReceiver`/`carriedBinding`/this-type along the path), reverse is a static edge-inverted map (raw
`edge.ReceiverType`, no binding). They agree where narrowing is edge-local (the receiver win); they diverge
where it is path-dependent (generic type-arg flow). The residual over-approximation concentrates at generic
seams: e.g. `BillingRuleHelper.SaveServices<TEntity>` → `s1.Delete()` with stored receiver `"TEntity"` (open
type parameter) → `ResolveNarrowRoot` can't place a type-parameter → full CHA cone over the 97-way mined
`EntityBase.Delete` fan.

### Calibration (MedDBase store caa9373ffbf6-dirty)
- Mined dispatch dominates: **9,981 roslyn** vs **266 heuristic** edges (97.4% precise). Roslyn binding is NOT
  the problem; we are not reimplementing the compiler.
- Pure CHA-fallback (heuristic basis) is tiny: 266 edges, **1** high-fan source (`GenericImportEntity.Save~λ0`,
  fan 26), the rest fan-1 (precise anyway).
- The REAL over-approximation is **mined multi-target fans that receiver-narrowing fails to trim**: 103 mined
  sources fan ≥10 (26 of them ≥40); `EntityBase.Delete` = 97 mined targets. Dominant cause is the receiver
  gap: **212,357 / 613k call edges (34%) carry NO receiver** → narrowing can't even start → full CHA cone.
- ⇒ The actionable signal is NOT "heuristic basis"; it is "**un-narrowed multi-target dispatch site**", and the
  cause to disclose is *why the receiver couldn't narrow* (absent / type-parameter / base-typed / unbound).

### The model (reified generics → monomorphization)
C# generics are reified: every `Foo<Account>`/`Foo<Invoice>` is a distinct monomorphic type at runtime; an open
`SaveServices<TEntity>` never executes — only concrete instantiations do. So the precise analysis treats a
generic method as a TEMPLATE and materializes it per concrete type arg seen at call sites (RTA/VTA-style).
Three outcomes per generic/dispatch site:
1. **Monomorphize** where the concrete type arg is in scope — precise; kills the fan (`SaveServices<ServiceEntity>`
   → `s1.Delete()` resolves to `ServiceEntity.Delete`, not all 97).
2. **CHA cone over the constraint** where it isn't — SOUND, never a dead node. "Can't resolve" ≠ "dead": the
   binding may be supplied at runtime (framework/DI/reflection-bound open entry point), the constraint
   (`where TEntity : EntityBase`) still bounds the runtime type to a cone, and monomorphization can be
   unbounded (`F<List<T>>` recursion) so it must cap and fall back. Treating unresolved as dead is the one
   UNSOUND move (silent false negative) — the direction rig never takes.
3. **Prune (RTA)** only where the program-wide instantiation set is empty — the *only* place "dead" is sound
   (a generic whose type is never instantiated anywhere is genuinely unreachable).

### Disclosure as diagnostic (load-bearing)
Every CHA-cone fallback is DISCLOSED, attributed to a site, and **classified**:
- **actionable** (`likely missing rule / EP def`): open-generic entry point bound by the host, DI open
  registration, a type-arg or receiver that flows from a seam we could capture with a rule — fixing the
  rule/EP def monomorphizes it. This is the valuable worklist.
- **irreducible** (`hard boundary`): polymorphic-recursion past the fuel cap, fully-dynamic
  `MakeGenericType(Type.GetType(...))` — no rule fixes these; tag, don't TODO.
Rank by fan degree × incoming-edge count (a ×97 fallback is a "go write a rule" flag; a ×2 is noise). Builds on
existing disclosure infra (`~heuristic` tag, the "dispatch fan-out (NOT a real call)" bucket, the `×N fan-out`
annotation) — makes it attributed + prioritized instead of merely present.

### End-state
Narrowing becomes a PURE FUNCTION OF THE (instantiated) EDGE → materialize ONE narrowed graph; forward and
reverse traverse the identical edge set → **forward ≡ reverse by construction** (closes the asymmetry) and
precise full-closure reverse is `O(E)` (closes the perf note).

### Increments (playground-first: synthetic unit tests green → validate on the real store → iterate)
1. **Dispatch-fan disclosure / diagnostic** — ✅ **SHIPPED** (`027819ca`, `rig dispatch-fans`). Pure-additive,
   zero traversal change. Calibration result: 841 un-narrowed hubs / 71 actionable; top irreducible
   `IGenericServiceProvider.ProvideService``1` (fan 5 × 980 edges — a service-locator seam); top type-parameter
   actionable `EntityBase.Save` (115 × 11), `EntityBase.Delete` (49 × 8), `Construct``N.New` factories.
2. **Static monomorphization** (was "increments 2 + 3" — they COLLAPSE; see revision below). The single change
   that kills the type-parameter over-fan AND delivers forward ≡ reverse + `O(E)` reverse. DESIGN-FIRST.

### Revision (2026-06-24): increments 2 and 3 are the SAME change
Empirical finding while scoping #2 — the concrete type arg IS already in the facts: the call
`DebtorOverride.SaveIncludedServices → SaveServices<…>` carries `TypeArguments =
BillingRule…IncludedEntity,int` (reference_facts), and `NarrowByTypeArguments` already monomorphizes a fan
against `carriedBinding`. So a query-side "thread carriedBinding" fix LOOKS cheap — but it is **structurally
defeated**: the forward traversal is NODE-MEMOIZED (`Enqueue` keys `visited` by node id, FactPathFinder.cs),
while `carriedBinding` is PATH-dependent. `EntityBase.Delete` is a hub reached from thousands of sites, so
**first-reach-wins**: whichever path hits the hub first expands its dispatch fan with its (usually empty)
binding and marks the node visited; the binding-carrying path arrives to an already-expanded node. Binding
narrowing therefore can't fire at exactly the shared hubs that matter. (Same node-memoization-vs-path-dependent
conflict as the forward/reverse asymmetry.)
⇒ The only robust fix is to make each instantiation a **distinct node** — STATIC monomorphization (clone the
instantiated generic body, keyed by its type-arg binding), which IS the materialized-graph change. There is no
cheap separate query-side increment. 2 ≡ 3.

### Design forks for static monomorphization (decide before building)
- **(node, binding)-keyed traversal** vs **static body-cloning into distinct nodes.** Keying risks
  combinatorial blowup; cloning needs an instantiation INVENTORY (which `<T=concrete>` actually occur) and a
  fuel cap for polymorphic recursion (`F<List<T>>`). Cloning is the principled end-state (pure edge function →
  forward ≡ reverse); keying is a smaller but leakier step.
- **Instantiation source**: the per-call-site `TypeArguments` / `MethodTypeArgBinding` already in
  reference_facts (loaded onto `CallEdge`, Reads.cs) supply the inventory — no re-index needed for v1.
- **Service-locator bucket is SEPARATE** (`ProvideService``1`, the #1 actionable by blast radius): not a
  monomorphization case — resolve via the existing `di_registrations` facts (table present in the store) to the
  registered impl, instead of CHA-fanning. A small targeted build, independent of the monomorphization work.
- **Bound**: instantiation count cap + CHA-cone fallback on overflow (NEVER dead; disclose via #1).

### Hard constraints
- **Playground → green → big-boy → iterate**, unit-test coverage required (mirror `ReverseReceiverNarrowingTests`
  / `OneHopDispatchTests` synthetic-`FactGraphData` style).
- **Unresolved generic → CHA cone, NEVER dead** (soundness; disclose, don't drop).
- **Disclose + classify every fallback**; a high-fan actionable fallback is a hypothesis that a rule or EP def
  is incomplete.

### Reconcile-on-settle: parked reverse-dispatch tests (recover when the substrate settles)
Five unit tests are `[Skip]`-parked, each pinning the PRE-substrate reverse-dispatch over-approximation that
per-edge receiver narrowing (`c7fe4f0f`) intentionally changed. They document the phantom reverse reach that
**forward ≡ reverse** (the monomorphization prize, design goal #2) is meant to eliminate — so they cannot pass
until the substrate is on. **Trigger to recover: after static monomorphization (the materialized graph) is
wired into the load path on-by-default AND FP-calibrated on the real MedDBase store** (Phase 4 of
[design-dispatch-precision.md](design-dispatch-precision.md)). At that point, re-enable each and flip its
assertion from "documents the over-approximation / includes the phantom" to the narrowed truth (no phantom
caller; reverse == forward at the dispatch hop). The five:
- `CallersForwardVerificationTests.Reverse_reach_includes_both_eps_documenting_the_over_approximation`
- `CallersForwardVerifiedClosureTests.Reverse_closure_includes_the_phantom_caller_documenting_the_over_approximation`
- `CallersForwardVerifiedClosureTests.Forward_verify_confirms_the_real_reacher_and_partitions_the_phantom_as_reverse_only`
- `FactPathFinderFanoutTests.ReachedBy_finds_transitive_callers_including_interface_dispatch`
- `FactPathFinderFanoutTests.Reverse_dispatch_narrows_by_receiver_at_the_dispatch_hop`

---

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

---

## MedDBase staleness/cache-coherence corpus (validates FR-7 + `static_init_capture`)

Recent `Bug 🐛`-tagged GitLab issues confirming the cache/staleness family is a live, recurring defect class
(probed 2026-06-24, `mms/meddbase-main-application`). Use as FR-7 / `static_init_capture` regression corpus.

| Issue | Shape | Maps to |
|---|---|---|
| **GI-862** Consultation SNOMED wording wrong until restart | config value frozen in `static readonly` field init | `static_init_capture` (NEW) |
| **GI-4199** Existing document import does not invalidate person cache | write (doc import) with no companion cache:invalidate on patient-record cache; "wait 30 min" lifetime flush | reads textbook but **FR-7 misses it as configured** — see validation below |
| **GI-4448** Location name change doesn't reach existing SbS sessions | write (location rename) not invalidating cached sessions; "eventually updates" on unrelated event | FR-7 variant, **cross-resource** (location → cached session) — same hard cross-resource shape as 862 |
| **GI-4367** Entra signing key cache should invalidate on cache miss | cache lacks negative/miss revalidation on external key rotation | staleness, but **not** FR-7 (no local mutation; external change) — a "cache miss should revalidate" pattern; track as a distinct future detector |

Takeaway: FR-7's "missing invalidation after a mutation" plus the new "frozen-in-static" capture cover GI-862,
GI-4199, GI-4448. The cross-resource cases (4448, 862, **4199**) need the resource-correlation to bridge a
derived dependency, which FR-7's same-resource scoping does not yet do — the known cross-resource limit.
GI-4367 is a separate "stale cache on external change / revalidate-on-miss" class worth a future entry.

### GI-4199 validation (2026-06-24, store `caa9373f` — substrate YES, current rule NO)
Traced the import write path against the store (the bug is OPEN, so the buggy code is in `LATEST`):
- **Substrate sees it:** `DocumentEntity.SaveImportedDocument` reaches **5 `llblgen` writes and 0
  `cache:invalidate`** (`rig reaches … --only cache,llblgen`). The person-cache invalidation API it should call
  exists (`Application.Core.Messages.PersonModelCacheAddOrUpdate*.Tell(...)`). The missing-invalidation fact is
  present and queryable.
- **FR-7 as configured does NOT fire, for TWO reasons:** (1) **anchor mismatch** — the rule anchors on
  `llblgen:bulk_write`; this is a *per-entity* `DocumentEntity.Save` (FR-7 deliberately skips per-entity saves,
  assuming self-invalidation). (2) **same-entity scoping** — the mutation is on **Document**, the stale cache is
  the **Person** record (patient record aggregates its documents). The rule requires a *same-entity*
  invalidate; it cannot express "Document.write ⇒ Person cache:invalidate."
- **Conclusion:** GI-4199 demotes from high→**medium** fit, gated on the SAME cross-resource enhancement as
  862/4448. Three proven cases now justify the **declared cross-resource dependency** feature for FR-7
  (`{ownerEntity: Person, partEntity: Document, …}` → a write on `partEntity` obligates an invalidate on
  `ownerEntity`'s cache). This is the single highest-leverage FR-7 upgrade; the substrate already supports the
  query.

---

## Detector: `write_set_divergence` — import/API path writes fewer tables than the UI path (NEW, corpus-surfaced)

**Status:** proposed · **Family:** consistency/dual-write sibling · **Found:** 2026-06-24 (100-bug GitLab triage, `docs/meddbase-bug-corpus.md`)

### The hazard
Two entry points that perform the "same" logical operation on the same entity — typically the **canonical UI
save path** and an **import / Enterprise-API path** — write **different sets** of tables. The
secondary/derived tables the UI path maintains (`PERSON_EVENT`, junction/link tables, counter or denormalized
columns, audit rows) are silently skipped by the import path, leaving them stale or inconsistent. No
exception, no missing row in the primary table — just a quietly-incomplete write-set.

### Corpus evidence (the standout pattern from the 100-bug sweep)
- **GI-4385** — import updates `DOCUMENT` but leaves `PERSON_EVENT` untouched (status read goes stale).
- **GI-3951** — import path misses ~5 junction tables that the UI write maintains.
- Recurs across the dual_write cluster; the triage flagged this as a class NOT captured by FR-1..7 +
  `static_init_capture`. Full evidence in `docs/meddbase-bug-corpus.md`.

### Why it's rig-shaped (cheap — no new extraction)
rig already has per-EP reachable `db:write`/`llblgen:*` resource-sets. The detector is a **structural set-diff**:
for a pair of EPs (import-EP, ui-EP) operating on entity T, compute each EP's reachable write-set and flag
tables in `writes(ui) \ writes(import)` (and vice-versa) as candidate divergence. Set-algebra over facts rig
already derives.

### The hard part — pairing the EPs (the real design question)
"Same logical operation" is not a fact rig has. Options, cheapest-first:
1. **Rule-declared pairs** — `{entity, uiEntryPoint, importEntryPoint}` triples in `rig.rules.json`. Precise,
   zero false pairs, but manual; good for a first slice on known import/UI dyads.
2. **Anchor-table heuristic** — EPs whose write-set contains the same *primary* entity table T are treated as
   peers; diff their full write-sets. Automatic but noisier (an EP that legitimately does a narrower op trips
   it) → emit as a disclosed CANDIDATE, never a verdict.
3. **Reference write-set** — pick the EP with the maximal write-set for T as the "canonical" baseline; flag the
   others' shortfalls. Risky (maximal ≠ correct) — research only.
Start with (1) on the corpus dyads to prove the set-diff core, then evaluate (2) for recall.

### FP / honesty notes
- A narrower write-set is often CORRECT (the import genuinely shouldn't touch T2). This is candidate
  generation; significance needs the pairing rule or a human — same posture as FR-1.
- Bounded by what's instrumented: an EP that maintains a secondary table via an in-memory/cache path rather
  than a `db:write` won't show in the write-set.

### Validation methodology (applies to the whole corpus — note the closed-bug trap)
**A fixed/closed bug is already corrected in a recent index**, so the detector will show it absent there. To
validate against a fixed case, index the **fix commit's PARENT** and run the detector on that store
(`--store <parent-sha>`); confirm it fires, then confirm the fix commit's store is silent — a before/after
golden check. Open bugs (e.g. GI-4199, GI-4385) are still present in current `LATEST` and validatable as-is.
For `write_set_divergence`: build fixture dyads from GI-4385 / GI-3951 at their pre-fix commits.

---

## Feature: LLM-optimised call-tree summary format (`--llm-summary`)

### Problem

CodeRig currently produces two output formats for call-tree analysis:

| Format | Approx. size | Issue |
|---|---|---|
| Annotated tree (terminal) | ~3 k chars | Box-drawing chars and emoji tokenise badly; structure encoded twice (indent + box chars) |
| Flat TSV (`--summary`) | ~100 k chars | Full CLR signatures, unreduced effect lists, and per-row file paths make it prohibitively token-expensive |

Neither is well-suited as LLM input. The terminal format is readable by humans but wastes tokens on
decoration. The flat TSV is structurally sound but ~30–50× larger than necessary, primarily due to full CLR
signatures.

The primary consumer of this output is an LLM doing structural reasoning: redundancy detection, side-effect
analysis, entry-point classification. That consumer does not need namespaces, parameter types, or file paths.

### Proposed solution

Add a `--llm-summary` flag (or `--summary=llm`) that emits a compact, flat, deterministically diffable TSV
optimised for LLM token budgets.

#### Format specification

Tab-separated, one row per node, with a header row. File is UTF-8, LF line endings.

```
depth    parent    name    arity    calls    effects    flags
```

| Column | Type | Description |
|---|---|---|
| `depth` | int | 0-based nesting depth |
| `parent` | string | Short name of the direct caller; empty for roots |
| `name` | string | `TypeName.MethodName` — no namespace, no parameter types |
| `arity` | int | Parameter count (preserves overload disambiguation without listing types) |
| `calls` | int | Number of call sites from parent (replaces `×N` in tree format) |
| `effects` | string | Deduplicated, counted effect list: `io:read ×3, efcore:read ×2` |
| `flags` | string | `cycle`, `x-phase`, `elided`, `lambda` — pipe-separated if multiple |

#### Name shortening rules

1. Strip all namespace segments — keep only the declaring type's simple name and method name.
2. Strip parameter types — preserve arity (count) only.
3. Lambda nodes: omit the row entirely (flag on parent as `lambda` if relevant); lambda bodies are token
   waste for structural reasoning.
4. Compiler-generated types (`<>c`, `d__N`): suppress or fold into the nearest named ancestor.

#### Effect deduplication rules

Current flat TSV emits one token per effect occurrence: `io:write,io:write,...×16`.
New format aggregates: `io:write ×16`. If only one occurrence: `io:write` (no count).
Multiple distinct effects: comma-separated after aggregation: `io:read ×3, efcore:read ×2`.

#### Elision policy

`⋯elided` in the tree format is a correctness hazard for redundancy analysis — the LLM cannot distinguish
"not called again" from "called but suppressed." The new format should either:

- **Include** the elided call with `flags=x-phase` and full effect annotation (preferred), or
- Emit a synthetic row with `name=<elided>` and a stable reference back to the first occurrence.

The first option is preferred because it makes redundancy analysis unambiguous without expanding token cost
significantly.

#### Example

Input tree fragment (current):
```
├─ Reads.LoadFactGraphAsync ⋯elided  {⚡ efcore:read Data.CallEdge, ⚡ efcore:read Data.ImplementsEdge, ...}
```

New format row:
```
1    DeriveCommand.RunAsync    Reads.LoadFactGraphAsync    1    1    efcore:read ×4    x-phase
```

Full example output (abbreviated):
```
depth    parent    name    arity    calls    effects    flags
0        DeriveCommand.RunAsync    9    1    io:write ×16    
1    DeriveCommand.RunAsync    RuleSetLoader.Load    2    1        
2    RuleSetLoader.Load    RuleSetLoader.LoadMergedDocument    3    1    io:read    
3    RuleSetLoader.LoadMergedDocument    RuleSetLoader.LoadBuiltIn    1    1    io:read    
3    RuleSetLoader.LoadMergedDocument    RuleSetLoader.MergeWithFile    2    2    io:read ×2    
1    DeriveCommand.RunAsync    Reads.LoadFactGraphAsync    1    1    efcore:read ×4    
1    DeriveCommand.RunAsync    Reads.LoadFactGraphAsync    1    1    efcore:read ×4    x-phase
```

The duplicate `Reads.LoadFactGraphAsync` rows — one plain, one `x-phase` — make the redundant load
immediately visible without any tree traversal.

### Non-goals

- Human terminal readability (that is the existing tree format's job).
- File paths and line numbers (use the existing format or the full TSV for navigation).
- Full type-resolution fidelity (arity is sufficient for structural reasoning; the full TSV remains
  available when types matter).

### Acceptance criteria

- [ ] `--llm-summary` flag produces valid TSV with header row.
- [ ] No CLR namespaces or parameter type names appear in output.
- [ ] Effect lists are aggregated (`×N` form).
- [ ] X-phase calls are included with `x-phase` flag rather than silently suppressed.
- [ ] Lambda nodes are suppressed.
- [ ] Output is deterministic across runs for the same input (diffable).
- [ ] Size regression test: output for the reference codebase stays under a defined token budget
  (suggested: 8 k tokens for a mid-sized solution).

### Implementation notes (orchestrator)

- The tree is already built (`TreeCommand` / `FactPathFinder.BuildTree`); this is a new **renderer** over the
  existing forest + the effect annotations, alongside the terminal renderer and the `--summary` TSV — not a
  new traversal. Name shortening reuses `SymbolNameFormatter`'s simple-name logic.
- The `x-phase`/`elided` flag is exactly the `⋯elided` "seen" marker the tree renderer already computes (see
  `docs/bugs/tree-spurious-seen-footer-for-lambdas.md` for the lambda edge case) — surface it as a column
  instead of suppressing the subtree. This dovetails with the redundant-reload findings the derive call-tree
  surfaced (x-phase duplicates become first-class, greppable rows).

### Token efficiency: the `parent` column

The `parent` column re-spells the parent's short name on every child row (and the same name is also that
node's own `name` on its own row) — long names repeated N× across N siblings. Cut it **per projection**:

- **Reconstructable views (default spine-kept / full):** rows are DFS pre-order with `depth`, so a row's
  parent is *the nearest preceding row at `depth-1`* — fully derivable (lambda-folding and x-phase both
  preserve this). So **drop `parent` entirely** in these views: biggest token save, zero indirection (the LLM
  reads it like an indented tree, natively). Verified the depth+order linkage holds after lambda folding.
- **Effects-flat view (gaps, no spine):** `parent` cannot be recovered from depth+order, so it stays
  explicit. *Here* a surrogate row-id (`id` column; `parent` = parent's id) earns its keep — saves the
  repeated long name AND disambiguates short-name collisions (two `Foo.Bar` from different namespaces shorten
  identically, making a name-parent ambiguous). Trade-off: an id forces the LLM to build a row-id lookup vs.
  reading a name locally, so prefer it only where the name is genuinely repeated/ambiguous.
- Introduce surrogate ids *globally* only if short-name collisions prove common in practice — measure first;
  the indirection cost is real. Touches `LlmSummaryRenderer`; sequence after the `--format llm` refactor.

---

## Refactor: single graph-shaping entry point (`LoadShapedGraphAsync`)

### Problem

The reachability-shaped call graph (`classify methodGroup→handoff` → `RewriteGenericFactories` → delivery
edges) is assembled in **three scattered, partial places**:

- `GraphMaterializer.BuildFromGraphAsync` — bakes classify + factory + delivery into the persisted `call_edges`.
- `DeriveCommand.RunAsync` — hand-rolls `LoadFactGraphAsync → RewriteGenericFactories → LoadDeliverySites →
  AddDeliveryEdges` inline (for FR-10 `event_cycle`).
- `FactPathFinder.ShapeGraph` (used by `impact` + the EF-fallback traversals) — does factory + cut + context
  but **omits delivery edges entirely**, so `impact`'s per-EP reach and EF-fallback `reaches`/`tree`/`path` do
  not see publish→consumer delivery at all.

Three definitions that can drift, a real coverage gap (impact/EF-fallback miss delivery edges), and a
documented-only ordering invariant (the delivery join consumes the classifier's `Kind=handoff` output, so it
must run after — enforced by comment, not structure).

### Proposed solution

One Storage entry point `Reads.LoadShapedGraphAsync(context, RuleSet rules, ct)` that returns the fully
in-memory-shaped graph: `LoadFactGraphAsync` (load + classify) → `RewriteGenericFactories` → delivery edges
(`LoadDeliverySitesAsync` + `AddDeliveryEdges`) → attach cut/context metadata. Every in-memory consumer
(`derive`, `impact`, EF-fallback traversals) calls it; `GraphMaterializer` persists **exactly its
edge-creating output** to `call_edges` (cut/context stay traversal-time, as today). Net:

- **Closes the gap**: `impact` per-EP reach + EF-fallback traversals gain delivery edges uniformly.
- **Resolves review finding #1a**: the graph is loaded + shaped **once** in `derive` and reused by both the
  handoff-EP derivation and the cycle pass (was loaded twice — `DeriveHandoffEntryPointsAsync` internal +
  `DeriveCommand:115`).
- **Dissolves the ordering coupling**: stage order lives inside one function, tested — not a cross-call comment.
- One shaping definition; `call_edges` becomes purely its materialization.

### Acceptance criteria

- [ ] `derive`, `impact`, EF-fallback traversals, and `GraphMaterializer` all obtain the shaped graph from the
  one entry point; no hand-rolled `classify→factory→delivery` sequence remains at a call site.
- [ ] `impact --per-ep` and EF-fallback `reaches`/`tree` now traverse delivery (event/actor) edges (new test).
- [ ] `derive` loads the graph once (verify via the call tree — no duplicate `LoadFactGraphAsync`).
- [ ] Behavior otherwise unchanged: `rig derive` output byte-identical; MedDBase `event_cycle` 24/all-high;
  persisted `call_edges` count unchanged; full suite green.
- [ ] `dead`'s unshaped-CHA-superset requirement still met (the raw/`--raw` path bypasses delivery shaping).

### Related: parallelise the independent query-side loads — INVESTIGATED, DOES NOT PAY (do not rebuild)

The derive (and impact) commands issue several **data-independent** loads — graph edges, EP data, delivery
sites, effect inputs — that run **sequentially** on one `DbContext`. They are temporally decoupled, so they
*looked* like candidates to overlap across **separate read `DbContext`s / connections** (sound — the store is
opened read-only and SQLite allows concurrent readers; not `Task.WhenAll` on one context, which throws).

**Profiled + built the lowest-risk slice + measured on the real store → reverted.** Findings (2026-06-23,
MedDBase, Threadripper 32-logical, NVMe):
- The synthetic raw-SQLite concurrency experiment looked promising: 2 concurrent `reference_facts` scans on
  separate connections ran **1.94–2.75× faster** than sequential. The reads ARE CPU/marshaling-bound, not
  single-disk-serialised, so in isolation they overlap.
- **But the real `derive` command got no win — a slight regression.** Built the cleanest slice
  (`LoadShapedGraphAsync ∥ LoadFactEntryPointDataAsync` via `Task.WhenAll` on a second read context, in both
  `derive` and impact's `LoadHeadSideDataAsync`). Output stayed **byte-identical** (correctness fine), but warm
  `derive` went **~13.2 s → ~13.7 s median** (5+ runs each). The DB-load region is only ~33–36 % of wall-clock
  (Amdahl ceiling ~1.1–1.3 ×), and even that didn't materialise: the two big loads contend on EF marshaling /
  memory bandwidth, and the second context's setup (per-connection `mmap_size=1 GB` + 256 MB page cache) +
  EF compiled-model warmup outweigh the DB-layer overlap.
- **Conclusion:** adding a second `DbContext` + concurrency for net-negative perf is the trade we explicitly
  rule out. The bottleneck is the single-threaded CPU passes (`FactEffectDeriver.Derive`, `FactCycleDeriver`)
  + EF row materialisation, which DB-connection parallelism can't touch. If derive latency ever matters,
  attack THAT (the CPU passes / marshaling), not the load sequencing. Do not re-attempt the connection
  parallelisation without a materially different store profile.

---

## Perf: redundant work per entry point (rig self-dogfood, F1–F9)

Found by running `rig` on its own store and reading every EP's `--format llm` call tree (the `x-phase` flag
makes a re-reached node a first-class row). One command calling the same heavy load more than once in a
single invocation. The **derive-path** instances are FIXED (commit `perf(derive): cut redundant reloads`);
the rest are the same patterns in other commands, still open. Severity = the cost of the repeated work.

| # | Redundant work | EPs | Status |
|---|---|---|---|
| F1 | `LoadFactGraphAsync` (efcore:read ×4) loaded inside `DeriveHandoffEntryPointsAsync` AND again directly | Derive | **FIXED** (`9caef5d1`) — `LoadShapedGraphAsync` loaded once, threaded into `DeriveHandoffEntryPointsAsync` + the cycle pass |
| F2 | `LoadFactEntryPointDataAsync` (efcore:read ×5) loaded top-level AND again inside a derivation callee | **FIXED** (Derive + Tree/Reaches) (`1be1094f`) | the real duplicate was the EF-fallback path (`TraversalGraphLoader` + `EntryPointContext.DeriveEpSiteKind`); threaded one load via `ReachInputs.EpData`. Callers/Path/Impact load epData at their own level (no dup through `BuildEpContext`) |
| F3 | `LoadFactGraphAsync` HEAD + BASE in Impact; each opens a fresh ADO conn via `LoadDispatchFactsAsync` | Impact | conn-reuse part FIXED in `LoadFactGraphAsync`; the base/head double-load is **intentional** (different stores) |
| F4 | `LoadDeploymentsAsync` (io:read ×3, slnx+projrefs parse) runs **twice** (`calls=2`) | Impact | **FIXED** (`78dbe9c2`) — hoisted before the cache branch, reused on both paths |
| F5 | `EffectDerivation.DeriveEffects` (full effect-match loop) runs twice on cold cache | Tree/Reaches/Derive | **NOT A REDUNDANCY** (investigated, `1be1094f`) — the bounded tree-path derive and the whole-store hazard-augmented `DeriveHazardEffectsAsync` use different complementary inputs; merging would change semantics |
| F6 | `RuleSetLoader.LoadMergedDocument` re-run for fingerprinting (4× total per command) | **FIXED** (`1be1094f`) | derive + Tree + Impact + EntryPointContext.Materialize now use out-param `Load` + `ComputeFromPaths` (one caller, `LoadOrDeriveEpSiteKind`, has no nearby `Load` — left) |
| F7 | `StoreLayout.ResolveReadStoreDir` (io:read ×7) resolved in `OpenReadContext` AND again for `StoreKey` | Derive | **FIXED** (`78dbe9c2`) — `OpenReadContext` surfaces the dir via out-param, reused for `StoreKey` |
| F8 | `LoadStaticField{Write,Read}RefsAsync` — two reads, identical base query | **FIXED** (Derive + Impact) | derive + impact (both sides) use the combined `…AccessRefsByKindAsync` (`78dbe9c2`); Tree already routes through the shared `DeriveHazardEffectsAsync` (combined) |
| F9 | `LoadDeploymentsAsync` (io:read ×3) loaded in `RunEntryPointsAsync` AND again at depth-1 | Callers | **FIXED** (`78dbe9c2`) — `DeploymentMap` loaded at the call site, threaded into `RunEntryPointsAsync` |

Cross-EP heavy shared methods (benign at once-per-command, the F1–F9 cases are the >once ones):
`LoadFactGraphAsync` (7/9 EPs), `LoadFactEntryPointDataAsync` (7/9), `LoadDeploymentsAsync` (7/9),
`DeriveEffects` (4/9), `RuleSetLoader.Load` (9/9). The `LoadShapedGraphAsync` consolidation (above) plus a
shared per-command `DeploymentMap` cache and threading already-loaded data into callees would clear most of
the open rows; F6's non-derive instances want `RulesFingerprint` to accept pre-resolved paths everywhere.

### Residual follow-ups surfaced by the work

- **Route EF-fallback `TraversalGraphLoader` through `LoadShapedGraphAsync`. — WON'T DO.** The consolidation
  (`9caef5d1`) routed derive + impact through the single shaped-graph loader, closing impact's `--async`
  delivery-edge gap — but the EF-fallback traversal loader (reaches/tree/path/callers when not on the SQL
  `call_edges` path) was left doing its own `LoadFactGraphAsync + ShapeGraph + MarkEventSubscriptionHandoffs`
  WITHOUT `AddDeliveryEdges`, so those fallback paths don't see delivery edges. **Decided not to fix
  (2026-06-23):** it's a corner case — the EF-fallback only triggers when `rig graph` hasn't run (no
  `call_edges`: `--no-graph` or pre-graph stores), and every modern graph-by-default index takes the SQL path
  where delivery edges are baked into `call_edges`. The fix is delicate (shaping is split between the loader's
  `ShapeGraph` and the command's `MarkEventSubscriptionHandoffs`, so threading `AddDeliveryEdges` in with the
  load-bearing ordering — `AddDeliveryEdges` must precede `MarkEventSubscriptionHandoffs`, the one that cost a
  24→0 `event_cycle` regression — is fiddly) and is not validatable on the MedDBase store (which has
  `call_edges` and never hits the fallback) without constructing a `--no-graph` store. The risk to the
  contended shaping path outweighs fixing a fallback modern indexes don't reach; left as a known limitation.
- **`seen` flag: split into `seen` vs `depth-capped` via a `TruncationCause` on `TraceNode`. — DONE**
  (`861bd0c4`). `TruncationCause { None, AlreadyExpanded, DepthCapped, BudgetCapped }` is set by precedence in
  `BuildTree`; the llm `seen` flag maps only to AlreadyExpanded, with distinct `depth-capped`/`budget-capped`
  flags and `seen:<id>` back-ref only for AlreadyExpanded. Tree payload-schema version bumped v1→v2.

---

## Detector coverage gaps (RCA production corpus)

Source: `meddbase-analysis/docs/rca-corpus-meddbase.md` (real production reverts/fixes), made executable by
`tests/Rig.Tests/Fixtures/ProductionFixCorpus.cs` + `…/Analysis/ProductionFixCorpusTests.cs` — each bug is
compiled in-memory and run through the real extract→derive with shipped rules; `_Gap_`-named tests pin a
KNOWN blind spot. **Status (2026-06-23): 4 of 7 FR families implemented + corpus-proven** (FR-1/1b shared-
mutation-under-concurrency *candidate*; FR-3 N+1 looped read; FR-4/1e per-EP effect/read-set + hazard delta in
`impact`; FR-6 unserializable `object_store` payload). The uncovered families, promoted here:

- **FR-7 — cache coherence (entity_cache write with no matching invalidation). NOT IMPLEMENTED — biggest open
  opportunity.** Maps the largest RCA cluster: !7721 (Redis entity-cache invalidation), #4199 (import doesn't
  invalidate person cache), #3941 (billing↔import invalidation missing), #4367/#4235 (signing-key cache miss),
  #940 (corrupted cache keys via race). Likely shape: a derive-side reachability rule — an `entity_cache:write`
  (or its keyed variant) reachable on an EP whose reach lacks a corresponding invalidation call for the same
  key/region. Design first: what counts as an "invalidation", per-key vs blanket, and how to avoid the FP class
  FR-1 hit (disclose candidate, don't claim proof). Ship with a corpus fixture per mapped case.
- **FR-1 PRECISION (not recall) — the pinned `_Gap_` sub-patterns. PARTIALLY DONE (`039d2eec`).** FR-1 already
  fires (recall is fine); the gap is false positives + uncoupled findings.
  - **DONE this pass** (the triage half — UX panel #2, no new extraction): `#cctor` exemption (CLR type-init
    lock → not a race; was a `lazy_init_race` FP class), per-`(type, method)` dedup with a `×N` count in the
    rollup (the 26-site `HandleSettingsToBeLogged` cluster → one row), and a `--exclude-namespace` filter for
    framework/vendored noise. Validated on MedDBase (`#cctor` 16→0, real findings survive).
  - **STILL OPEN** (needs NEW extraction + a re-index, NOT query-side): (a) **#2930** TOCTOU coupling /
    conditional-overwrite-vs-true-RMW — distinguishing `S.X = f(S.X)` (real RMW) from `S.X = independentValue`
    (conditional overwrite, agent C's dominant `high`-tier FP) needs a fact for whether a write's RHS DEPENDS
    on the read cell; the extractor doesn't capture it today. (b) **#4246** lock-attribution across a
    wrapper/callback boundary — needs cross-method happens-before/span propagation. (c) **#2892** quantified
    per-EP query-count. These are the FR-1 follow-up; until then race_window stays a disclosed candidate.
- **FR-2 — AsyncLocal/ThreadStatic flow + deadlock / lock-ordering. WON'T DO (declined by design).** Motivating
  bugs (!10208 ThreadStatic→AsyncLocal, !7194 SQL background deadlock, #311) stay pinned in the corpus as named
  targets, but detecting them needs AsyncLocal/ThreadStatic *flow* modeling and lock-ordering analysis — both
  beyond the fact-based, query-time reachability model (same boundary as the "no path-sensitive analysis"
  principle). Recorded so it isn't re-attempted; revisit only if rig ever grows a real type/value-flow pass.

---

## Reach post-commit callbacks (`DoWhenCommitted`) — effects fire but aren't reachable from the EP

> **SUPERSEDED / MISDIAGNOSED (verified 2026-06-23).** This item's *cause* is WRONG. `DoWhenCommitted`
> lambdas are NOT sync-cut — a `methodGroup`→lambda edge is walked synchronously (proven: in-repo unit test
> `tests/Rig.Tests/Domain/DoWhenCommittedHandoffTests.cs`, and a recursive walk from the exact
> `AbsenceRecordEntity.Save` node reaches its `~λ0/~λ1` + `LogAbsenceRecordAdded`). The webhook on the
> SaveLetter path is a plain `invocation` from `DocumentEntity.Save`, not a deferred lambda. The real cause
> of `reaches SaveLetter --only webhook,audit = 0` is the **external-virtual-override orphan** below — see
> that section. A `handoffDispatchers` rule for `DoWhenCommitted` would do nothing for it and would *reduce*
> recall (reclassifying currently-walked lambda edges to sync-cut handoffs). Do NOT build the fix described
> here. (The deferred-vs-synchronous *precision* question — should commit callbacks be modeled as deferred
> at all — is a real but separate, lower-priority semantic question.)

Surfaced closing the UX-panel "missing effects" loop (the `webhook:emit` + `audit:write` rules added to
MedDBase `rig.rules.json`, 2026-06-23): both effects are now MODELED and fire at the right sites (e.g.
`DocumentEntity.TriggerDocumentWebhook`, the `auditLogEvent.Log()` sites), but `reaches SmartLetter.SaveLetter
--only webhook,audit` is **0 even with `--async`**. Cause: on the document-save path these run inside
`DoWhenCommitted(() => …)` *deferred transaction-commit callbacks* — the effect's enclosing method is the
commit-callback lambda (`…~λ0`), which today is NOT on a handoff class rig walks, so it's sync-cut and
`--async` doesn't reach it either. So the effect is greppable store-wide but invisible from the entry point
that triggers it.

Likely fix is a **rule, not engine work** (correcting my first take): `DoWhenCommitted(Action)` is the
classic "delegate handed to a dispatcher to run later" handoff shape, so a `handoffDispatchers` entry
(per-repo data) should let the classifier promote the commit-callback lambda to a walked handoff edge —
making its effects reachable under `--async`, tagged as scheduled, exactly like timer/actor/event callbacks.
TO VERIFY before building: (a) confirm `DoWhenCommitted`'s registration is co-located-methodGroup/lambda
shaped (what `handoffDispatchers` matches) vs. needing a delivery-rule or genuine engine support; (b) decide
the semantics tag — it's deferred-but-SAME-THREAD (runs at commit, not cross-thread), so it should walk under
`--async` but ideally not be mislabelled `cross_thread`. Scope: start with the `DoWhenCommitted` dispatcher
on the MedDBase store, calibrate (does `SaveLetter --async` then reach the audit/webhook?), then generalize.

---

## External-virtual-override orphans — first-party overrides unreachable through an external base call

**Root cause of the "DoWhenCommitted" symptom above (verified 2026-06-23, in-repro + MedDBase store).** A call
to a method *declared on an external base class* whose **first-party override** carries the effect:
`document.Save()` (parameterless) statically binds to `M:SD.LLBLGen.Pro.ORMSupportClasses.EntityBase.Save`
(external, `TargetInSource=0`). The graph-load filter (`TargetInSource &&` in `Reads.LoadFactGraphAsync` /
`FactProjection.GraphData`) **drops that edge**, so `NewTextDocument` never reaches
`DocumentEntity.Save(IPredicate,bool)` — the override that fires `webhook`/`audit`/cache-invalidation/
`OnDataChanged`. The 0-arg convenience method trampolines to the virtual `Save(IPredicate,bool)` *inside the
external DLL* (invisible to rig). rig already mines the override chain from the 2-arg virtual down
(`EntityBase.Save(IPredicate,bool) ← CommonEntityBase.Save ← ~114 entity overrides`); **only the 0-arg→2-arg
hop is missing.**

- **`dead` does NOT catch this.** The overrides stay reachable via the parallel 2-arg `.Save(pred,recurse)`
  path (121 sites → `CommonEntityBase.Save` → dispatch fan; all 114 overrides have inbound edges, zero
  orphaned). The gap is PATH-specific (the 0-arg sites miss), not global; dead-code is a zero-reacher signal,
  blind to a missing edge when a parallel path keeps the target alive. (Total orphaning — a codebase using
  *only* parameterless `.Save()` — WOULD surface as a dead-code FP cluster, which is the tell.)
- **Blast radius (heuristic scan, validated 2026-06-23):** external-virtual targets (`TargetInSource=0`,
  first-party receiver) whose same-named method is overridden first-party — a **name-stripped** join
  (`reference_facts` × `dispatch_facts` override; signature stripped so the 0-arg call target matches the
  2-arg override base — exact-DocID would miss it). Top hits on MedDBase: `EntityBase.Save` **1614**,
  `EntityBase.Delete` **320**, `EntityCore.ValidateEntity` 34, `EntityBase.OnFetchComplete` 29, `OnSave` 11,
  `OnDelete` 5, plus framework hooks (`Page.OnInit`, `Hub.OnDisconnected`). Low-value same-signature overrides
  (`ToString`/`GetHashCode`) sort to the bottom by site count; a "reaches an effect" filter drops them.

### Confirmed trampoline map (LLBLGen `SD.LLBLGen.Pro.ORMSupportClasses`, net452 DLL, decompiled 2026-06-23)

Ground-truth from decompiling `EntityBase` (netstandard2.0 copy identical). The **4 redirect candidates** —
all on `EntityBase` (SelfServicing; `EntityBase2`/Adapter has no parameterless `Save`/`Delete`, so every
flagged 0-arg site is necessarily `EntityBase`-derived → anchor rules on `EntityBase` only). Each is a single
direct `this.`-call to the virtual, no reflection/multi-hop:

```
EntityBase.Save()            → EntityBase.Save(IPredicate, bool)   // Save(GetConcurrencyPredicate(...), recurse:false)
EntityBase.Save(bool)        → EntityBase.Save(IPredicate, bool)
EntityBase.Save(IPredicate)  → EntityBase.Save(IPredicate, bool)
EntityBase.Delete()          → EntityBase.Delete(IPredicate)
```

**NOT candidates** (single virtual overload, no convenience form → nothing to bridge): `OnSave`, `OnDelete`,
`OnFetchComplete`, `EntityCore`1.ValidateEntity`, `PreProcessValueToSet`. Why they appeared in the name-stripped
scan but are benign: a call binds to an external **same-signature** virtual only when the *receiver has no
first-party override of it* (else it binds first-party) — so there is nothing to reconnect. **Heuristic
refinement for the skill:** a true candidate requires the receiver to override a **different** overload than
the one called (overload mismatch); same-signature matches are non-orphans and must be excluded.

Two design facts the map forces (see route below): (1) the redirect *target* (`Save(IPredicate,bool)`) is
itself external (`TargetInSource=0`), so the mechanism must KEEP the redirected edge and let receiver-narrowed
dispatch resolve it to the first-party override — not just rewrite the callee (which would re-drop it). (2) the
rule source must match the *specific convenience signatures*, never the virtual target itself (no self-redirect).

### STATUS: Phase A DONE (2026-06-23) — `redirectRules` shipped + calibrated on MedDBase

Implemented end-to-end: `FactRedirectRule` + `RedirectClassifier` (Domain), the `redirectRules` schema +
`FactRedirectRuleProvider` + `RuleSet.Redirect` + loader cascade-merge, and the projection hook in BOTH
`FactGraphProjection.FromAnalysis` (`rig index`) and `Reads.LoadFactGraphAsync` (`rig graph` / EF-fallback),
threaded through `GraphMaterializer`/`TraversalGraphLoader`. The 2 LLBLGen rules
(`EntityBase.Save → Save(IPredicate,bool)`, `EntityBase.Delete → Delete(IPredicate)`) are in MedDBase
`rig.rules.json`. Tests: `ExternalVirtualOverrideOrphanTests` (RED orphan + GREEN reconnect via real
two-assembly extraction), `RuleSetLoaderTests.RedirectRules_round_trip…` (the cascade-merge regression — the
bug real-store calibration caught, since rule-constructing tests bypass the loader). Full suite 565 green.
MedDBase calibration (re-graph): `reaches SmartLetter.SaveLetter --only webhook,audit` **0 → 7** (1 webhook
via `DocumentEntity.TriggerDocumentWebhook` + 6 audit); +1,988 call_edges; redirect edges **2108
receiver-narrowed / 43 null-receiver CHA-fan** (98% precise); `event_cycle` **24** (unchanged — no regression).
Residual: the 43 null-receiver edges over-approximate (standard CHA disclosure); the `dead` detector still
can't see partial orphans (parallel-path-masked) — both noted, not blocking.

### Chosen route: a projection-time `redirectRules` rule (NOT decompilation, NOT `handoffDispatchers`)

A new rule kind that, at the **reference-fact→CallEdge projection** (BEFORE the `TargetInSource` filter — the
edge is already gone post-filter), rewrites a call to external `EntityBase.Save` (any overload) with
first-party receiver `T` → `T`'s `Save(IPredicate,bool)` override (receiver type is already mined → no CHA
fan-out); existing dispatch carries it the rest of the way.
- **Phase A (mechanism):** `redirectRules` schema + the projection hook, proven by an in-memory
  **two-assembly** RED→GREEN repro (external base ⇒ `TargetInSource=0` — the only vehicle that reproduces the
  drop; a single-source fixture would be `TargetInSource=1` and show no bug). Then calibrate on the MedDBase
  store (the scan above = the target set; verify SaveLetter→webhook reconnects; watch `event_cycle`/`impact`
  deltas — adding ~1,900 edges is a large but CORRECT reach increase, so calibrate before on-by-default).

### Backlog items

1. **Pack a rule-extraction skill.** Automate the heuristic scan (external-virtual-override orphans), rank by
   blast radius, propose `redirectRules` JSON with a per-rule reach-delta preview, human-in-the-loop (never
   auto-apply; FP-calibrate like every detector). **Downstream of Phase A** (it proposes rules of a kind the
   engine must already understand). Playbook skill first (`SKILL.md` + the mining query); promote to a
   `rig suggest-rules` native command only if it earns repeated use. Generalizes later to mine other families
   (candidate effects, `handoffDispatchers`). This is "detectors are data, mined from the codebase."
2. **Analyze which external assemblies to decompile for white-box rule extraction.** Investigate the decompile
   route as an *offline rule-GENERATION aid* (not a runtime subsystem): IL-read the external trampolines
   (LLBLGen `ORMSupportClasses`, LanguageExt, Echo, `System.Web`/SignalR lifecycle) to auto-discover
   `X() → callvirt X(args)` self-trampolines and emit `redirectRules`. Keeps runtime rule-based; sidesteps the
   runtime-decompile costs (DocID-identity-at-scale, fact-store bloat, the two-stage-philosophy break — see the
   decompile analysis in session notes). **Deliverable:** a ranked list of assemblies worth decompiling + the
   trampoline patterns each yields.

---

## Generated-code indexing policy: index everything; derive page EPs from source, not generated proxies

**Decision (2026-06-23): index generated code — NO `files.exclude` for generated, and NEVER a `<auto-generated>`
blanket exclusion.** The redirect work surfaced that the MedDBase `files.exclude` glob `**/*EntityBase.cs`
(intended for LLBLGen generated entity bases) also matched the HAND-WRITTEN `MMSHelperClasses/CommonEntityBase.cs`
— the partial holding `Save`/`Delete` + the `DoWhenCommitted` cache/audit/webhook wiring. Once `rig index` was
fixed to honor cwd rules, that exclusion finally applied and silently broke every entity-save reachability chain
(`CommonEntityBase.Save` dropped → hatch had no dispatch fan → redirect dead-ended → SaveLetter→webhook went 0).
Removing the generated exclusions (index everything) fixed it. Measured cost: **+22% index time / +24% store /
+54% symbols**, with NO analysis distortion (`event_cycle` stayed 24; the extra symbols are benign leaf accessors).

**Why no exclusion at all — "generated ≠ noise."** The same `<auto-generated>` header sits on BOTH pure boilerplate
(LLBLGen `*EntityBase.cs` — field/relation accessors, zero EPs/effects, safe to drop) AND behaviorally-essential
code (`*_RequestProxy.g.cs` — the RequestResponseProxyGenerator output that is the source of **554 entry points**,
the client→server page-action seam). So `<auto-generated>` is a bulletproof *is-generated* signal but the WRONG
predicate: there is no clean marker for *is-noise vs is-structure* — that needs per-generator judgment, exactly the
brittleness that bit us. Index-everything is the safe default; any future size-driven exclusion must target a
SPECIFIC proven-noise generator (e.g. LLBLGen entity bases via the `LLBLGen Pro` header, verified to contain zero
EPs/effects) and NEVER a `<auto-generated>` blanket.

**Follow-up (worth pursuing): derive page EPs from SOURCE rules, not the generated proxies.** Today 554 page entry
points are derived from the GENERATED `*_RequestProxy.g.cs`. The proxy is an INDIRECT artifact; the DIRECT signal is
whatever the proxy generator itself keys off in the page SOURCE (page-action declarations / attributes / handler
conventions). If rig's entrypoint rules detected those page-action EPs directly from source — extending the existing
`meddbase.clientaction`/page-EP rules to cover the full set the generator produces — EP detection would be decoupled
from the generated artifact: more robust (survives generator-format changes) AND it would re-open the option of
EXCLUDING the generated proxies for store size WITHOUT losing the 554 EPs. Scope: map what the
RequestResponseProxyGenerator keys off in page source → express as entrypoint rules → verify the 554 EPs reproduce
from source → only then are the proxies excludable. Until then, index everything (the proxies carry the EPs).

---

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
  is reachable from the EP (`Contact/Edit.Delete`) in EITHER sync OR async reach (verified 2026-06-25). But the
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

---

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

---

## Monomorphization rework — pending (session 2026-06-24 wrap-up)

The static-monomorphization rework shipped transitive + lambda-closure materialization (real-store
validated: `DebtorOverride.SaveIncludedServices` 665→38 reach, Haiku-confirmed sound) on a uniform EF load
path behind the `meta` schema-version gate. Open items:

### Re-enable the SQL reads fast path (binding-aware) — ✅ DONE (2026-06-25, `71f35478`)
`SqlReachability.LoadGraphFromReachSetAsync` now re-attaches `DeclaringTypeArgBinding`/`MethodTypeArgBinding`
from `reference_facts` onto the bounded `CallEdge`s (one bulk pass keyed by caller/callee/line, alongside
`TypeArguments`), so the ShapeGraph monomorphization seam fires on the bounded graph. `SqlFastPathEnabled =
true`. The CTE only BOUNDS the load; the in-memory FactPathFinder+ShapeGraph runs over it, and reach_set is
the CHA SUPERSET so it reproduces the full-EF narrowed reach (`Bounded_graph_reproduces_full_graph_reach` +
`Sql_*` equivalence tests). Real-store: `DebtorOverride` → 38 via the SQL path, ~14s vs 22s full-EF.

### Materialize the monomorphized subgraph at `rig graph` time (the next perf lever)
The bounded SQL load above still pulls the receiver-blind **CHA superset** and narrows it IN MEMORY — so for
a high-fan EP (e.g. `DebtorOverride`, narrowed answer 38 but CHA reach 665+) the bounded pull is large and the
win over full-EF is modest (~14s vs 22s, not the old ~8s pure-bounded). The lever: **bake materialization
into the persisted graph at `rig graph` time** — run `GenericInstantiationInventory` + `GenericMonomorphizer`
during `GraphMaterializer.BuildAsync` and persist the `~mono` instantiation nodes + substituted/redirected
edges into `call_edges`/`dispatch_edges` (and a base→mono collapse map for display). Then the CTE walks the
ALREADY-NARROWED graph, so the bounded pull is sized to the narrowed reach (small) — the query-time
inventory/materialize/collapse work disappears too. Cost: graph-build does the materialization once
(amortized), bumps `SchemaVersion.Graph` (re-graph required), and the bounded loader's in-memory ShapeGraph
materialize step is dropped for the SQL path (kept for the EF fallback / `--raw`). Display-collapse must run
on the persisted `~mono` ids (already handled by `MonomorphCollapse`). Validate forward≡reverse + clone count
on the real store before flipping it on.

### Single static SQL connection across the app
Each query currently opens its own `RigDbContext`/connection. Move to ONE shared (static) SQLite connection
app-wide — read pragmas + mmap/cache applied once, warm across queries. (User request.)

### forward ≡ reverse on the real store (the architectural prize)
The 8 parked reverse-dispatch tests are **✅ RECONCILED (2026-06-25, `cc9a529b`)** — un-skipped and fixed to
the narrowed truth: the reverse walk excludes CHA phantoms (forward≡reverse on those seams) and
dispatch-declaration waypoints (interface/base-virtual decls aren't caller-origins), keeping the real
caller/EP assertions. Suite has **zero** skips now. STILL OPEN: validate forward≡reverse on the REAL
MedDBase store (the synthetic tests prove it per-seam; the materialized-graph reverse vs forward at scale is
unmeasured) — pair with the FP-calibration sweep below.

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

# Backlog — `rig` as a bug-finder (grounded in MedDBase production reverts)

Status: **backlog, evidence-grounded** (2026-06-21). Need-first on purpose: the *features* below may be
reshaped, but the *need* — "could rig have caught this class of production regression before it shipped?"
— and the *evidence* (real MedDBase reverts/fixes) outlive any specific mechanism.

This doc came out of a concrete exercise: mine the MedDBase main-app repo for MRs that caused production
issues (reverts + follow-up hotfixes), classify each by bug class, and test on the live index whether
rig's existing facts could reconstruct the failure signal. The corpus is the gold; the features are
derived from where rig fell short of it.

---

## 1. The need

A revert (or a same-week hotfix that says "my refactor broke X") is a production bug that escaped review.
We have a real, dated corpus of them. For each, the question is: **does rig already surface the signal, is
it a cheap data/extraction addition, or is it genuinely new analysis?** That question, answered per
bug-class, *is* the roadmap.

rig's structural strength is reachability + effects, immune to formatting/rename churn. Its blind spots
are runtime ordering, value-flow, and aliasing. The corpus lands squarely across that boundary, which is
why it's useful for prioritising.

---

## 2. The corpus (real reverts/fixes, `mms/meddbase-main-application`)

Found via full-history `Revert` merges + direct-to-main revert commits + follow-up "Fix … broke …" MRs.

| Example (culprit) | What happened | Bug class | rig today |
|---|---|---|---|
| **!10706** | non-atomic RMW on shared `Cache.PatientsForcedOffFastPathLegacyId` (`HashSet.AddRange`) across parallel `FieldEntityModel.Load` → crash + silent data loss | Concurrency: shared-cell RMW in fan-out | region ✅ · mutate-as-effect **✅ proved (data)** · join ❌ |
| **latent** (same file) | sibling pair `PatientsWithPriorFields` (`HashSet`) + `…Loaded` (`bool`) — non-atomic two-field publish; **not** Atom-wrapped by the !10706 fix; same shared Cache, same parallel path | Concurrency: two-field publish (**field assignment**) | region ✅ · mutate **❌ extraction gap** · join ❌ |
| **!10208** | `[ThreadStatic]` → `AsyncLocal<T>`; **reverted next day** (!10224) | Concurrency: context-propagation semantics | **❌ tier-2** (needs flow modeling) |
| **#1646** (!9288) | "Delay in adding Contact Log when a lot of info added at same time"; **reverted** | Concurrency: contention/serialization under load | region ✅ · contention ❌ |
| **#2930** (!9322) | erroneous appointment status change across two tabs; fix = "check status before updating"; **reverted same day** | Concurrency: TOCTOU / lost update (distributed check-then-set) | ❌ |
| **#2892** (!9340, !9380) | Pathways running **4000 queries/min** to read variables (missing cache entries); **reverted twice** | Read amplification / N+1 / missing-cache | partial (`impact +observation`, loop-read tags) |
| **!10281** (fixed by !10418) | Test-Bed import refactor stopped setting Import Instance Id → Data Import broke ("my previous refactor broke the Data Import") | Effect-loss regression (a write no longer happens) | **✅ designed for this** (per-EP `-effect`) |
| **!9624** (reverted by !10413) | labs HL7 → new diagnostic eAPI DB tables; new write surface; live ~6 days | New effect surface | ✅ `+effect` |
| !10243 (NullRef on Restore, reverted) · paket/nuget/pipeline reverts | — | Logic/null-deref · build/config | ❌ out of scope |

Out-of-scope classes are kept in the table on purpose: they bound the claim. rig is not a null-checker or
a build linter, and the corpus shows how much of the revert stream is those (don't oversell coverage).

---

## 3. What was validated live (2026-06-21, HEAD index `ae082702cf`)

Tested the **!10706** precondition against the existing MedDBase index without re-indexing:

- ✅ **The concurrency region is already visible.** `rig reaches FieldEntityModel.Load` surfaces
  `d3  parallel fanout  Tasks.Parallel  <- Tal.RunParallel`.
- ❌ **The shared mutation was invisible.** `rig reaches MarkIntraImportFastPathConflicts` tagged only 8
  `throw` effects — the `.Swap(...)` write on the shared `Atom` was unmodeled.
- ✅ **The mutation is recoverable as pure data.** Adding one `effects` rule
  (`shared_state:mutate`, methods `Swap/SwapAsync/set_Value`, receiver `LanguageExt.Atom`) and re-deriving
  (no re-index) lit it up: `d0  shared_state mutate  LanguageExt.Atom<A>  <- MarkIntraImportFastPathConflicts`.
- ✅ **Both join operands co-occur.** Filtered to `parallel,shared_state`, `reaches Load` shows the
  fan-out and **two** shared mutators (`MarkIntraImportFastPathConflicts`, `ProcessDuplicateData`) at the
  same depth d3.
- ❌ **The handoff is sync-cut.** `rig path Tal.RunParallel MarkIntraImportFastPathConflicts` → "No path";
  the only EP reaching `Load` is `AppStartupProcesses.Startup ▶ background`. The fan-out over `Load`
  crosses a handoff boundary rig does not walk by default.

Conclusion: rig has the **raw material** for the headline concurrency case (region detection + effect
tagging). What's missing is (a) field-write extraction, (b) walking into the concurrency body, and (c) the
aliasing/escape join. That split drives the feature priorities below.

---

## 4. Feature requests

### FR-1 — `rig races`: shared-state-under-concurrency detector  *(headline)*
**Grounds:** !10706, the latent `PatientsWithPriorFields` pair, #1646.
The join rig is missing. Three rungs by cost:
- **(a) data, no re-index — mostly done.** Ship a `shared_state:mutate` effect family for mutation
  *method calls* on INHERENTLY-shared-mutable TYPES: `Atom.Swap/SwapAsync`, `System.Collections.Concurrent.*`
  mutators, `ImmutableInterlocked`. Validated in §3 (one rule lit up MarkIntra + ProcessDuplicateData at d3
  alongside `parallel:fanout`). Remaining: curate the method/type set.
  **Correction (code-verified):** bare collection `.Add/.AddRange` "on static/captured receivers" is NOT
  expressible as a rule — effect rules gate by method name + receiver/declaring TYPE only, so they can't
  tell a shared/static receiver from a local one; a `.Add` rule fires on every local `List` = mass false
  positives. Only types that *are* a shared-mutable contract qualify here. "Mutation of a shared collection
  under fan-out" is gated by (b)/(c), not (a).
- **(b) a DERIVE arm — NO re-index (cheaper than billed).** The field-write facts ALREADY EXIST:
  `FactExtractor` records a field/property assignment as a `RefKinds.Write` ref (`FactExtractor.cs:1174`),
  and the field's static-ness is already in its `SymbolFact.Modifiers`. So the `PatientsWithPriorFields =
  found` write is *not* invisible at the fact layer. What's missing is purely query-side: `FactEffectDeriver`
  consumes only invocation/`MatchConstructor`/`MatchThrow` refs — there is NO arm for Write refs. Add a
  `MatchFieldWrite` rule + a deriver arm turning a Write-ref-to-a-static-field into `shared_state:mutate`
  (join the target field's `Modifiers` for the static gate), keyed to the enclosing method. Derive-side, no
  re-index. *Caveat:* verify which assignment shapes `IsWriteTarget` catches — compound `+=`, indexer
  `x[i]=`, nested writes may not be Write refs; those shapes (only) would need a small extraction follow-up.
- **(c) the analysis.** Flag a `shared_state` effect reachable *within the dynamic extent* of a
  `parallel:*`/handoff region where the receiver is shared/escaping. Requires walking handoff bodies (like
  `--async`) plus an aliasing/escape heuristic ("is this the same cell across ≥2 concurrent flows"). The
  real research bet. Over-approximate and report candidates — consistent with rig's existing posture.

### FR-2 — AsyncLocal / ThreadStatic propagation lint  *(tier-2)*
**Grounds:** !10208.
Model `AsyncLocal`/`ThreadStatic` field decls + their read/write sites as facts; flag a read on a flow
that crosses a thread boundary (`Task.Run`/`Parallel`) from its writer where the value would not
propagate (or where a ThreadStatic→AsyncLocal swap changes inheritance). Needs flow-direction reasoning,
not just reachability — harder than FR-1.

### FR-3 — first-class N+1 / read-amplification per entry point
**Grounds:** #2892 (4000 queries/min, reverted twice).
rig already tags reads under loops (`🔁 [loop]`). Promote to a quantified per-EP observation — "read
inside loop, key varies per iteration, no cache check on path → est. N queries" — and surface in `impact`
as `+observation n+1`. #2892 was a missing-cache read explosion that regressed twice; this is exactly its
shape and reuses existing machinery.

### FR-4 — `impact --expect-no-effect-change` (CI guardrail)  *(cheapest net-new, high value)* — ✅ DONE (`58043d3b`)
**Grounds:** !10281 (lost write), !9624 (new write); locks in rig's already-proven strength.
Per-EP effect-delta works today and is formatting-immune. **Shipped:** `--expect-no-effect-change` exits 1
if any EP's reachable EFFECT set changed (`impactDiff.PerEp.Count` — the header's "N with a changed
behavior"); structural-only reachable-tree ripple does NOT trip it (a refactor is allowed to change reach
without changing effects). Opt-in flag, not default (impact's default job is to *report* changes — finding
them isn't a failure); verdict on stderr so `--format tsv` stdout stays clean; applied on both cold and
warm (cache) paths. Dogfood-validated on the C5/C6 query-path pair (9 structural, 0 behavioral → OK, exit 0)
and unit-tested across off/clean/changed. The CI guardrail for the .NET-standard / Linq2Sql-migration wave.

### FR-5 — contention / serialization hotspot hint  *(speculative)*
**Grounds:** #1646.
Flag a write to a single shared resource (one row / key / lock) reachable from a repeating-background or
high-fan-out EP — a serialization bottleneck. Lowest priority; weakest signal.

### FR-6 — object-store serialization-contract lint
**Grounds:** #1646 / !9288 (stored `Option<T>` into the object store; the serialiser can't round-trip it —
`None` must be `null`; corrupted `OBJECT_HOLDER` rows needed a live cleanup script).
Flag an `object_store:write` whose payload/type-argument is a serialiser-unsupported type (`Option<T>`,
and other LanguageExt/discriminated types lacking a surrogate). rig already tags `object_store` writes and
supports `type_argument` as a resource strategy, so the detector is largely **data** — the work is
enumerating the unsupported type set and capturing the written type at the call site. High yield given how
often LanguageExt idioms recur as failure sources (see RCA corpus §cross-cutting).

---

Per-MR root causes for every example cited here are in [`rca-corpus-meddbase.md`](rca-corpus-meddbase.md).

### FR-7 — cache-invalidation coherence
**Grounds:** !7721 (distributed Redis invalidation reverted for a feedback cycle + billing issues), #2892
(missing-cache N+1), #2057 (read-source swap), !10284 (missing invalidation), and the person-cache
cross-node staleness RCA. Caching is the most repeated failure theme in the corpus.
Two detectors on facts rig already tags (`entity_cache`, `redis` pub/sub):
- **cycle** — an invalidation/subscriber handler that reaches its own invalidation publish (the !7721
  feedback loop) — a reachability cycle over invalidate→publish edges.
- **missing-invalidate** — an `entity_cache:write` to a key with no reachable invalidation publish for that
  key (stale-across-instances candidate). Over-approximate; pair with the deployment/host gate to ask "is
  this cache write visible to an instance that never gets the invalidation."

## 5. Priority

1. **FR-4** — ✅ DONE (`58043d3b`). The refactor guardrail; ship it on the migration wave.
2. **FR-1(a)+(b)** — in flight (agent). (a) done-modulo-curation (shared-mutable-type rules); **(b) is a
   cheap DERIVE-arm, NOT extraction+re-index** (field-write facts already exist as `Write` refs) → moves
   ahead of (c). Together = the `shared_state:mutate` family. Gate on the measured FP rate on MedDBase.
3. **FR-6** — mostly data (existing `object_store` + `type_argument` machinery); high yield.
4. **FR-7** — caching is the most-repeated failure theme; reuses `entity_cache`/`redis` effects + the
   deployment gate. Coherence reasoning is the new work.
5. **FR-1(c)** — the real research bet (region + handoff-body traversal + escape heuristic); the concurrency
   sweet spot, but over-approximate only. Prototype on the !10706 fixture before committing to the product.
6. **FR-3** — reuses `impact` observations; medium effort.
7. **FR-2 (incl. deadlock / lock-ordering), FR-5** — stretch; record deadlock as a known limit.

Honest scope line: FR-1/FR-2/FR-5 chase a bug class (data races) rig can only ever *over-approximate* —
it tags and correlates, it does not prove an interleaving. The value is candidate generation for review,
not a verdict. FR-3/FR-4 are firmer because they sit on the deterministic effect/observation facts rig
already trusts.

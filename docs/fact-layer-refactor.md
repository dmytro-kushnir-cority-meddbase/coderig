# Fact-Layer Refactor (staged pipeline)

Status: in progress. Decisions captured from the design session 2026-06-01.

## Goal

Decouple the expensive Roslyn pass from rule application so that **changing a rule
(entry-point/effect/observation) or a query never re-runs Roslyn** — only changing
the fact-extraction code or the fact *schema* does.

## Stages

- **Stage 0 — Roslyn parse/bind** (expensive). Fused with stage 1 (Roslyn
  compilations don't serialize cheaply; the durable artifact is the *facts*).
- **Stage 1 — fact extraction** → SQLite. Rule-agnostic, resolved, language-level
  structural facts. Per-`ProjectId` rows.
- **Stage 2 — derivation** over stored facts (C# rule engine reading facts, writing
  materialized derived tables: entry points, effects, callgraph). No Roslyn.
- **Stage 3 — read-optimized index** for queries. Cross-project by construction
  (no "latest run", no stitching). FTS5 for symbol/text search; B-tree DocID joins.

## Decisions

1. **(Q1) Facts are rule-agnostic.** Stage 1 stores the resolved structural surface,
   not rig-domain candidates. Corollary: a frozen *rule-fact vocabulary* — the closed
   set of structural predicates rules may reference.
2. **(Q2) Full reference index (find-all-references)**, tagged by refKind
   (decl/invocation/ctor/typeUse/read/write/methodGroup/attributeUse). First-party
   targets always indexed; framework refs opt-in. This is the grep→SQLite replacement.
   Cheap-adds while bound: modifiers, type kind, signature, namespace→type→member
   containment chain, literal/const string arguments.
3. **(Q3) Global SymbolId = DocumentationCommentId (DocID).** Signature-based,
   cross-assembly, excludes param names (rename-stable), collapses multi-target
   sibling DLLs. Cross-project resolution = DocID equijoin (retires the substring
   `.Replace` stitching). Lambdas/locals have **no global id** — host-context only:
   direct invocation → inlined into host node; delegate handoff → call-site-relative.
   Add a `delegate-handoff` reference kind (callSite, receiverType, argPosition,
   target=DocID|callSiteLocalRef). Escaped delegates stay unresolved (no guessing).
4. **(Q4) Per-project upsert keyed by ProjectId = csproj path + TFM.** Drop-and-rebuild
   on mine rerun (no accumulation). Content hash = f(own source, each dependency's
   exported-surface, compiler options, extractor+schema version). Invalidation
   propagates *up* the dependency graph (dependents of a changed project re-extract).
   No history/versioning (deferred).
5. **(Q5) C# rule engine over facts, materialized derived tables.** Callgraph
   reconstructed from facts (direct invocations, delegate-handoffs, single-impl DI
   dispatch as a DocID join). Gated by a **resolver audit**: enumerate every semantic
   decision in CallResolver/CallGraphIndexes and prove each reduces to a stage-1 fact.
6. **(Q6) Thin cross-project read index now**, reachability precomputation deferred.
   No latest-run concept — every entry point/effect/reference is in one DocID-keyed space.
7. **(Q7) Fuse 0+1.** Stage 0 is dual-mode feeding the identical ProjectId-keyed fact
   schema: whole-solution-once (default full mine; live cross-project source; faster
   + more accurate DocID resolution) and per-project (incremental/fallback).

## Why (evidence from the session)

- The WCF over-detection fix required a full re-index/re-mine purely to re-apply a
  changed rule — under this design it's a sub-second stage-2 re-query.
- "All Pages entry points touching Healthcode" could not be answered by `trace`:
  Healthcode lives in Workflows (0 entry points → 0 callgraph nodes → not a stitch
  target), and queries are latest-run-oriented. We fell back to grep — the exact query
  this refactor turns into a SQLite scan. Facts exist for all methods regardless of
  entry points; the read index is cross-project.

## Implementation slices

1. **Fact layer foundation (symbol + reference index)** — schema, fused FactExtractor
   (single visitor pass), persistence, `rig symbols` / `rig refs` cross-project query.
   *(in progress)*
2. Delegate-handoff enrichment (receiverType/argPosition) + background-worker rule.
3. Stage-2 rule engine over facts (port entry-point/effect/observation rules).
4. Callgraph-from-facts + resolver audit.
5. Stage-3 read index (FTS5 + reachability).
6. Dual-mode stage 0 + per-project incremental hashing.

## Current divergence status (review 2026-06-08)

The cutover above has **not happened**, so two detection engines run live and have
**drifted**. `rig index`/`mine` populate the user-facing tables via the Roslyn
extractors (`EffectExtractor`, `EntryPointExtractor`, `EffectObservationExtractor`);
`rig derive`/`reaches`/`path` re-derive via the fact engine (`FactEffectDeriver`,
`FactEntryPointDeriver`). The *same* `rig.rules.json` produces different results
depending on the command:

| Rule field / capability | Roslyn (index) | Fact (derive) |
|---|---|---|
| `containingNamespaces/Types/Methods` | ✅ | ✅ **(P2a)** — parsed from EnclosingSymbolId; no base-walk for containing type |
| `resource` resolution (`receiver_type`/`argument_type`/`string_/http_argument`) | ✅ | ✅ **(P2a)** via P1a/P1b facts; `ef_*` deferred (EF-only) |
| MVC/MinAPI route *facts* captured | ✅ | ✅ **(P1d)** captured; fact-side MVC/MinAPI *deriver* not built yet |
| structural observations (`read_before_commit`, …) | ✅ | facts captured **(P1c)**; deriver = P2b |
| `declaringTypeNameEndsWith` | ❌ → **now ✅ (stopgap)** | ✅ |
| `declaringTypeBaseTypes` (ProxyBase gate) | ❌ → **now ✅ (stopgap)** | ✅ |
| `matchConstructor` / `minArguments` (G5 ctor-fetch) | ❌ → **now ✅ (stopgap)** | ✅ |
| `receiverTypes` | ✅ true static type (base-walk) | ✅ **(P1a/P2a)** receiver OR declaring type, equality/prefix (no base-walk) |

### P1 + P2a status (2026-06-08)

**P1 (fact enrichment) complete** — ReferenceFact now carries ReceiverType (P1a),
First{Argument}Template/Type incl. attribute route literals (P1b/P1d), and the
structural-context columns (P1c). **P2a done** — `FactEffectRule` gained `Resource` +
`containing*` + `treatAsDispatch`; `FactEffectDeriver` resolves the resource from facts
(dropping the effect when unresolvable, like Roslyn) and skips dispatch rules. A P1a
regression was fixed in passing: the `receiverTypes` gate had started probing *only* the
precise receiver, silently dropping calls through a derived receiver (e.g.
`ActionsHelper.RedirectUrl`, declared on the gated `Helper`); it now also matches the
declaring type.

**Real-data parity snapshot** (MedDBase.Pages: `rig derive` vs index `rig effects`=6968):
fact effects 2042 → **4837** after P2a. By provider: `clientpage_nav` **2791 = 2791 exact**,
`clientpage_event` **15 = 15 exact**. Remaining gaps are pre-existing *deriver-design*
divergences, not P2a regressions, deferred to the P3 parity diff:
- `clientpage_proxy` 1504 vs 2767 — Roslyn emits one effect **per matching rule**; the fact
  deriver is **first-match-wins** (`break`). One `ShowDialog()` matching both the
  `ProxyBase`-receiver rule and the `MedDBase.Pages`+`Proxy`-suffix rule = 2 effects in
  Roslyn, 1 in facts.
- `llblgen` vs 1390 — `declaringTypes` gate has no base-walk (e.g. `SaveEntity` resolved to
  a `DataAccessAdapterBase` subtype won't match a `declaringTypes:[…Base]` gate).
Also still open: the **framework-ref opt-in** gap (Q2 — http/efcore/redis effect targets in
`System.*`/framework assemblies are dropped from facts; receiver base-walk also needs first-
party base edges).

**Leakage to move to data during the port:** MVC `EndsWith("Controller")`+route
literals (`EntryPointExtractor.cs`), `parallel_fanout` method list
(`EffectObservationExtractor.cs`), DI registration-kind string branches
(`DiRegistrationExtractor.cs`). The fact derivers are clean (all framework names in
them are comments).

### STOPGAP — EffectExtractor parity gates (THROWAWAY, delete at slice 3/P4)

A second stopgap (2026-06-08) taught `EffectExtractor.Matches` to honor
`declaringTypeBaseTypes` and `declaringTypeNameEndsWith` on the invocation path
(`MatchesDeclaringBaseType` / `MatchesDeclaringTypeNameSuffix`, reusing
`RuleTypeMatcher`'s base-chain walk). Previously the index path ignored both, so
`clientpage_proxy` — gated only on deriving `ProxyBase` — matched *any* `Show`/
`ShowDialog`/`Redirect` and over-fired (e.g. `InvoiceServiceProxy.ShowDialog`, a
non-proxy whose name merely ends in "Proxy"). Guarded by
`PlaygroundAnalysisTests.Clientpage_proxy_effects_are_base_type_gated_at_index_time`.
Same throwaway disposition as the constructor stopgap below.



`EffectExtractor.FindConstructorEffects` / `MatchesConstructorTypeGate` were added
2026-06-08 so `rig index` emits llblgen ctor-fetch effects (`new XxxEntity(pk)`) — it
previously only walked `InvocationExpressionSyntax`, so `rig effects` silently showed
**zero** fetches across a whole MedDBase mine while the green
`Llblgen_entity_constructor_fetches_are_derived` test exercised only the fact path.
This is a deliberate bolt-on to the engine we intend to delete; it exists purely to
unblock real-mine effect output. **Remove it when slice 3 makes the fact engine the
single source of truth.** Guarded by the index-level test
`PlaygroundAnalysisTests.Llblgen_entity_constructor_fetch_is_extracted_at_index_time`
(survives the deletion — it should then assert the unified output).

### Unification target (keep the FACT engine, delete the Roslyn detectors)

Order matters — the fact layer's rule-change-without-recompile + cross-project
reachability are load-bearing, so the Roslyn detectors are the ones to retire:

- **P0** — add index-level parity tests for the only-fact capabilities (ctor-fetch ✅
  done, ProxyBase, `declaringTypeNameEndsWith`). They pin behaviour before the move.
- **P1** — enrich stage-1 facts to cover the only-Roslyn capabilities: receiver
  static type, literal/const args (kills the `receiverTypes` approximation + enables
  `resource` resolution), enclosing loop/try/retry context (enables observations),
  MinAPI/MVC facts.
- **P2** — port the only-Roslyn detectors + the leakage above into the fact engine as
  data.
- **P3** — repoint `rig effects/entrypoints/callgraph/trace` to materialized
  fact-derived tables; `index` and `derive` then agree by construction.
- **P4** — delete `EffectExtractor`/`EntryPointExtractor`/`EffectObservationExtractor`
  (incl. the stopgap) and the duplicate `EffectRule` fields. `mine` vs `index` differ
  only in scope.

Open risks: observations need AST context stage-1 facts don't yet carry; resource
resolution fidelity from facts; two callgraph constructions (`CallGraphBuilder` vs
`FactPathFinder`) need the slice-4 resolver audit before unifying.

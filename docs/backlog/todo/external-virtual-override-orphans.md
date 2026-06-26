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

   **✅ VALIDATED on the MedDBase store (2026-06-26 probe) — mechanically discoverable, ZERO new extraction.**
   The mining query is [docs/queries/external-virtual-override-seam-mining.sql](../../queries/external-virtual-override-seam-mining.sql).
   - **Critical heuristic correction (build to THIS, not the prose above):** the scan MUST be
     **receiver-type-scoped**, not target-level same-NAME matching. Naive name-match both floods with FPs
     (unrelated types sharing a name: `PredicateExpression.Add`⨝`HL7Component.Add` 6316, `List.Add`,
     `Option.Match`…) AND **wrongly excludes the real Save/Delete seams**. The seam fires iff the call site's
     receiver type (or an ancestor up its base chain) overrides the called simple name with a DIFFERENT
     signature and NO override on that chain has the SAME signature. That **overload-mismatch (`same_sig=0`)
     test is the true/benign separator** and dissolves the `ToString`/`GetHashCode`/`OnSave` false hits without
     a separate effect filter (they're all same-signature). Needs `reference_facts(TargetInSource, ReceiverType)`
     ⨝ `dispatch_facts(Kind='override')` ⨝ `type_relation_facts(RelationKind='base')` ⨝ `symbol_facts` — all present.
   - **Results:** reproduces the 2 shipped rules as the top candidates (`EntityBase.Save` 322 recv-types,
     `.Delete` 162) AND surfaces uncovered ones: `EntityBase.Save(bool)` (8), `ModelInfoProviderBase.GetEntityFieldCoreArray` (7),
     and a genuine **non-LLBLGen** candidate — `DataContext.SubmitChanges(IMeasureProvider)` →
     `DbDataContext.SubmitChanges(ConflictMode,IMeasureProvider)` (LINQ-to-SQL; `Db.cs:124`, carries an
     `OnChangesSubmitted()` post-commit effect — verified in source).
   - **Already half-surfaced:** `rig dispatch-fans --cause external-or-unbound` ranks the same hubs (Save #1,
     Delete #2 as "actionable") — the skill consumes that + the overload-mismatch SQL to propose the `redirectTo`.
   - **Residual FN (~4%):** external invocations with NULL `ReceiverType` (fluent/chained receivers) are invisible
     to receiver-scoping; recover via the dispatch graph, not `reference_facts` alone.
2. **Analyze which external assemblies to decompile for white-box rule extraction.** Investigate the decompile
   route as an *offline rule-GENERATION aid* (not a runtime subsystem): IL-read the external trampolines
   (LLBLGen `ORMSupportClasses`, LanguageExt, Echo, `System.Web`/SignalR lifecycle) to auto-discover
   `X() → callvirt X(args)` self-trampolines and emit `redirectRules`. Keeps runtime rule-based; sidesteps the
   runtime-decompile costs (DocID-identity-at-scale, fact-store bloat, the two-stage-philosophy break — see the
   decompile analysis in session notes). **Deliverable:** a ranked list of assemblies worth decompiling + the
   trampoline patterns each yields.

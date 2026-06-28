# Guarded `throw` arms carry no guard — the normal-completion model excludes dead-end blocks

**Status:** SHIPPED 2026-06-28 (`e0fac644`) — ComputeGuards' two-model merge (normal + virtual-exit, merged by IsAbnormalExit) gives a reachable guarded throw its gating predicate without disturbing the must-run spine; `OnThrow` wires the guard onto the Throw ref. Proven by the differential oracle + guarded-throw fixtures + a capture test; the vacuous-post-dominance regression still passes. · **Priority: LOW-MED** · **Found:** 2026-06-28 (dogfooding `tree`, `ClassFactory.AssemblyResolver` L41-43) · **Family:** effect-precision / control-dependence-model

## Validated end-to-end (2026-06-28)
Re-indexed the MedDBase primary checkout and confirmed live: `ClassFactory.AssemblyResolver`'s
`if (asm == null) throw …` now renders `↯ throw:raise System.Exception ⎇ [asm == null] :43`, while the
catch's unconditional rethrow at `:49` correctly stays bare. Two extra fixes surfaced during validation:
- **Try/catch throw fix (`74106eda`):** split IsAbnormalExit (escaping → virtual edge) from EndsInThrow (any
  reachable throw → read the virtual pass), so a caught throw reached via its catch→rethrow chain still gets
  its predicate. (Roslyn happened to route THIS throw straight to method-exit, but the fix is correct for
  genuinely-caught throws.)
- **SQL fast-path fix (`eb4cd90d`):** the real reason the leaf stayed bare after re-index — the graph-backed
  `SqlReachability` input queries (used by tree/reaches when graph views exist) didn't `SELECT EnclosingGuards`
  for invocation/throw refs. Card A had wired only the EF-fallback loaders.

## Residual (NOT done)
- Nested-guard throws carry only their innermost direct guard (the documented direct-CD limit — see [[guard-set-direct-vs-transitive-control-dependence]]).
- No unit test pins the SQL-fast-path guard selection (validated live; would need an indexed-store fixture).
**Related:** [[branch-aware-effects-shipped]] (the feature) · [[guards-not-shown-on-view-full-promoted-leaves]] (a guarded throw ALSO hits that rendering gap — two reasons it shows bare)

## Symptom
A `throw` that is clearly gated by an `if` renders with no `⎇`. E.g. `ClassFactory.AssemblyResolver`:
```csharp
Assembly asm = AssemblyCache.LoadFile(...);   // L40
if (asm == null)                               // L41
    throw new Exception("...");                // L43  → tree shows `↯ throw:raise … :43`, no guard
```
The throw plainly fires **only when `asm == null`** — exactly the kind of conditional effect
branch-aware-effects exists to surface — but the guard is absent.

## Proven to be a FACT-level gap (not just rendering)
Spike (`CfgSpike`-style): `ComputeGuards` for `if (asm == null) throw new Exception("boom"); return asm;`
returns **`(none)`** for the throw block. So even if the render plumbed effect leaves
([[guards-not-shown-on-view-full-promoted-leaves]]), the guard does not exist to show.

## Root cause — by design
`ControlDependence.ComputeGuards` (`Rig.Analysis/Extraction/ControlDependence.cs:231`) uses the
**normal-completion model**: a block that cannot reach the Exit (`rpoIndexR[b] < 0`) is excluded from the
reversed/post-dominator traversal (the fork guard `rpoIndexR[a.Ordinal] < 0` at ~:254 and the region guard
in `MarkRegion` at ~:269). Throw arms (and infinite loops) are dead-ends → absent → no guard. This is the
SAME gate that fixed the vacuous-post-dominance bug (a switch-expression's no-match throw arm vacuously
"post-dominated" everything, spuriously guarding the must-run spine). So it's load-bearing for must-run
soundness; we can't just drop it.

## What a fix looks like (don't break must-run)
The clean model: control dependence over a CFG augmented with a **virtual exit** that both normal-return and
abnormal-termination (throw) edges target. Then a throw arm post-dominates nothing it shouldn't, yet IS
control-dependent on its gating branch — so the throw block gets `asm == null = True` while the must-run
spine stays unpolluted (the original vacuous-post-dominance bug does NOT return, because the throw arm no
longer falsely post-dominates the normal path; they diverge at the virtual exit).
- Cheaper interim: special-case throw blocks to **inherit their immediate predecessor branch's predicate**
  (the gating `if`), without touching the general algorithm. Less principled (misses throws nested under
  multiple guards — same direct-vs-transitive limit as [[guard-set-direct-vs-transitive-control-dependence]]),
  but covers the common single-guard guard-clause throw.
- Either way: keep `MustRunBlocks` on the strict normal-completion model (a throw is NOT must-run); only
  `ComputeGuards` gains the abnormal-termination arm. Regression pair: the vacuous-post-dominance
  switch-expression throw-arm fixture (must NOT regress) + a new guarded-throw fixture (must now carry its
  predicate).

## Why LOW-MED
Useful (guarded throws are real conditional effects — validation/guard-clause throws are everywhere), but
bounded and not blocking: today it under-discloses (silent), never mis-discloses. Sequence it after the
cheaper rendering reconnect (#1 in the sibling card), and design the virtual-exit model deliberately rather
than bolting on the predecessor-inherit shortcut.

# Bug: `rig tree --full` emits spurious `↺seen` footer lines for single-reference lambda nodes

**Status:** FIXED (commit on feat/tree-cache) — see "Actual root cause" below; the original hypothesis was wrong.  
**Repro DB:** `C:\Git\meddbase-analysis`  
**Affected command:** `rig tree --full`

---

## Actual root cause (corrected)

There is **no separate footer generator**. The column-0 `↺seen` lines are not a footer — they are
**additional roots**. `FactPathFinder.BuildTree` roots at *every* node matching the `from` pattern
(`Contains(n, fromPattern)`), and a method's synthetic inline lambdas have ids of the form
`{methodId}~λ{ordinal}`, which embed the method name — so `tree "…RefreshMedicareVerificationInfoPanel"`
matches the method **and** `~λ0`/`~λ1`. The method root is expanded first (adding the lambdas to the
`expanded`/seen set as inline children); the lambda roots are then popped, found already-expanded, and
rendered as top-level `↺seen` (Truncated) nodes.

**Fix:** in `BuildTree` root selection, drop a matched lambda (`…~λN`) when its **container** also matched
(`IsContainedLambdaOfMatched`). It still renders inline under its container. A lambda whose container did
NOT match (e.g. a promoted async-handoff entry point targeted directly) stays a legitimate root.
Tests: `ConcreteReceiverPropagationTests.Inline_lambda_whose_container_also_matches_is_not_a_separate_root`
and `…Lambda_whose_container_is_not_matched_stays_a_root`.

---

## Symptom

```
rig tree "DetailsLive.RefreshMedicareVerificationInfoPanel" --full
```

```
▶ action DetailsLive.RefreshMedicareVerificationInfoPanel  ⟦MedDBase (iis)⟧
├─ ...
├─ DetailsLive.RefreshMedicareVerificationInfoPanel~λ0
│  └─ DvaCardLookup.GetDisplayString
└─ DetailsLive.RefreshMedicareVerificationInfoPanel~λ1
DetailsLive.RefreshMedicareVerificationInfoPanel~λ0 ↺seen
DetailsLive.RefreshMedicareVerificationInfoPanel~λ1 ↺seen
```

Two `↺seen` footer lines are emitted for `~λ0` and `~λ1` even though neither node is referenced more than once in the tree.

Additionally, `~λ1` appears as an empty leaf in the tree body (no children), then also appears as `↺seen` in the footer — making it look as though its subtree was filtered out, when in fact it legitimately has no callees.

---

## Why the children are correct

The source method (DetailsLive.cs:3219–3220) contains:

```csharp
Person.MedicareAuDvaCardType.Match(
    t  => DvaCardLookup.GetDisplayString(t),  // ~λ0 — one callee
    () => string.Empty)                        // ~λ1 — no callees; string.Empty is a field, not a method
```

- `~λ0` correctly shows `DvaCardLookup.GetDisplayString` as a child.
- `~λ1` correctly shows **no children** — `string.Empty` is a BCL string field and produces no call edge.

The tree body is accurate. Only the footer is wrong.

---

## Root cause hypothesis

The traversal adds every visited node to a `seen` set for cycle detection. The footer generator then emits `↺seen` for all members of that set, rather than only for nodes where an **inline** `↺seen` substitution was actually made during tree rendering.

Fix: track a separate `back_referenced` set, populated only when an inline `↺seen` is emitted (i.e. the node was about to be expanded but was already in `seen`). Drive footer output from `back_referenced`, not from `seen`.

---

## Impact

- `~λ1` (empty body + footer `↺seen`) reads as "children were filtered" when they are genuinely absent. Misleading for any zero-callee lambda in `--full` mode.
- Any lambda node in a `--full` tree produces a spurious footer entry, adding noise proportional to the number of inline lambdas in the method.

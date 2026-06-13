# Bug: `rig callers "X"` floods to 3,708 entry points (was: "seeds handlers of X")

**Status:** ✅ FIXED via graph-shaping consolidation (load-time `ShapeGraph`). `callers` on `PersonModelCacheFieldUpdated --entrypoints --async` went **3,708 → 3** entry points (the two actor inboxes + the test-bed registrar) and is now consistent with `path`/`reaches`. The original parameter-type-seed hypothesis was **disproven**; the seed-strip was reverted.  
**Repro DB:** `C:\Git\meddbase-analysis` (full solution, run `0dc27496`, 326k symbols)  
**Affected commands (before fix):** `callers`, `callers --entrypoints`, `callers --roots`, and `path` — every reverse BFS plus the unshaped forward `path`.

---

## ✅ Resolution (2026-06-13) — the hypothesis below is disproven; this is what was actually wrong & fixed

**Disproof of the seed hypothesis (measured on the repro store):** stripping the parameter-type seed changed the reverse-closure size by **zero** — `%PersonModelCacheFieldUpdated%` seeds went 4 → 3 (handler dropped) but the closure stayed **18,684**. The `#ctor` *alone* reverse-reaches 18,684 nodes / 3,708 entry points. So the seed was never the cause; the seed-strip has been reverted.

**Actual root cause — inconsistent graph SHAPING across commands.** `rig` has three graph-shaping rule families (in `rig.rules.json`):

| Rule family | tames | `reaches`/`tree` | `path` (before) | `callers` (before) | `dead` |
|---|---|---|---|---|---|
| `genericFactories` (monomorphize `Entity.New<Account>`→`Account.New`) | entity-factory hubs | ✅ | ❌ | ❌ | ❌ (by design) |
| `traversalCuts` (`ProvideService``1`, `IService.Startup`, `CreateService`) | reflection/service-locator seams | ✅ | ❌ | ❌ | ❌ (by design) |
| `contextDispatch` (workflow state-family) | cross-family dispatch | ✅ | ❌ | ❌ | ❌ (by design) |

`callers` walked the **raw** `call_edges ∪ dispatch_edges` (via `SqlReachability.ReachedWithDepthAsync`); `path` walked an **unshaped** bounded graph. So the reverse BFS sailed straight through the `ProvideService``1` service-locator seam (1,040 callers) and fanned to the whole actor system — while `path`/`reaches`, which cut that seam, reported "no path". That divergence WAS the bug (and the `path`↔`callers` "contradiction").

Frontier evidence (reverse BFS from the `#ctor`): stays 2–6 nodes through depth 11, then explodes 6 → 1,047 at depth 12 on `ProvideService``1`. Blocking just the three configured cut patterns in reverse: **18,684 → 26**.

**The fix (architecture):** shaping is now a property of the **graph**, not the traversal. `FactPathFinder.ShapeGraph(graph, factory, cut, context)` is applied once at load (`LoadShapedTraversalGraphAsync` / `LoadEffectReachInputsAsync`); cut + context rules are carried on `FactGraphData` and read by `BuildIndex`, so **every** traversal — forward `Successors` and reverse `Predecessors` — honours the identical shaping. The reverse traversal became cut-aware (`Predecessors` drops a predecessor matching a cut rule — the exact reverse of the forward leaf-stop). `callers`/`path` now load through the shaped graph like `reaches`/`tree`; `dead` deliberately stays unshaped (it needs the sound CHA superset, or it would over-report dead code). `--raw` bypasses rule shaping on all four.

See: `FactPathFinder.ShapeGraph` / `BuildIndex` / `Predecessors`, `CliApplication.LoadShapedTraversalGraphAsync`, tests `TraversalCutTests.Cut_rule_stops_ReachedBy_at_cut_node` + `…keeps_a_cut_node_off_the_no_predecessor_roots`.

---

## Symptom

```
# from C:\Git\meddbase-analysis
rig callers "PersonModelCacheFieldUpdated" --entrypoints --async
# → 3,708 entry points (expected: ~3–5 publishers)

rig path "AI/SmartLetter.EditLetter" "PersonModelCacheFieldUpdated" --async
# → No path (contradiction: callers claims SmartLetter reaches it)
```

---

## Root cause

C# XML-doc method IDs include full parameter types in the signature:

```
M:MedDBase.Processes.Person.PersonModelTransactions.UpdateCacheWithField(
    MedDBase.Processes.Person.PersonModelCacheFieldUpdated)
```

`SeedSql` and `BuildReachSetAsync` both do an unrestricted LIKE match on the full DocID:

```sql
-- SqlReachability.cs line 212
SELECT sym FROM nodes WHERE sym LIKE $pat ESCAPE '\'

-- SqlReachability.cs line 555
SELECT ToSym FROM call_edges WHERE ToSym LIKE $pat ESCAPE '\'
```

Pattern `%PersonModelCacheFieldUpdated%` matches two distinct classes of node:

| Match | Why it matches | Role |
|---|---|---|
| `PersonModelCacheFieldUpdated.#ctor(...)` | Type name is the method name | **Target** — what we want |
| `PersonModelTransactions.UpdateCacheWithField(PersonModelCacheFieldUpdated)` | Type name appears in parameter list | **Handler** — should be excluded |

The handler `UpdateCacheWithField` is called by `PersonModelTransactions.Inbox`. Seeding it in the reverse BFS propagates through the inbox → actor spawner → most of the codebase, producing 3,708 false entry points.

**Note:** This is NOT about `typeUse` edges in `call_edges`. `LoadFactGraphAsync` (`Reads.cs:279`) already filters `reference_facts` to `invocation | methodGroup | ctor` only — `typeUse` refs never enter `call_edges`. The inflation happens because method DocIDs embed parameter types.

---

## Affected code

### `SqlReachability.SeedSql` (line 210)

```csharp
private static string SeedSql(bool hasNodes) =>
    hasNodes
        ? "SELECT sym FROM nodes WHERE sym LIKE $pat ESCAPE '\\'"
        : """
            SELECT FromSym FROM call_edges     WHERE FromSym LIKE $pat ESCAPE '\'
            UNION SELECT ToSym FROM call_edges WHERE ToSym   LIKE $pat ESCAPE '\'
            ...
            """;
```

### `SqlReachability.BuildReachSetAsync` (line 535)

```sql
seeds(sym) AS (
    SELECT FromSym FROM call_edges WHERE FromSym LIKE $pat ESCAPE '\'
    UNION SELECT ToSym FROM call_edges WHERE ToSym LIKE $pat ESCAPE '\'
    ...
)
```

Both perform full-DocID LIKE, matching parameter types.

---

## Fix direction

Strip the parameter list before the LIKE comparison. In SQLite, `instr(sym, '(')` gives the position of the first `(` (or 0 if absent). A method whose declaring name matches the pattern has the pattern in `substr(sym, 1, instr(sym, '(')-1)`:

```sql
-- nodes path
SELECT sym FROM nodes
WHERE sym LIKE $pat ESCAPE '\'
  AND (
    instr(sym, '(') = 0
    OR substr(sym, 1, instr(sym, '(') - 1) LIKE $pat ESCAPE '\'
  )

-- edge-column fallback (same secondary filter on each leg)
SELECT ToSym FROM call_edges
WHERE ToSym LIKE $pat ESCAPE '\'
  AND (instr(ToSym, '(') = 0 OR substr(ToSym, 1, instr(ToSym, '(') - 1) LIKE $pat ESCAPE '\')
```

The outer LIKE keeps the index scan fast; the `substr` check filters out the false positives from parameter types. The secondary predicate runs only over the (small) pre-filtered set.

Apply the same fix in both `SeedSql` and `BuildReachSetAsync`.

### Refinement applied during the fix: suppress the strip for signature patterns

The naïve strip above breaks when the **pattern itself** is a full DocID carrying a parameter list — e.g. the overload-disambiguation pattern `M:NS.IFoo.Register(System.Int32,NS.ControllerTask)`. There the param list IS the discriminator, and the in-memory oracle (`FactPathFinder`'s plain substring `Contains`) keeps the match; stripping the candidate's params makes SQL diverge from the oracle (caught by `Sql_depth_reachability_matches_the_cha_oracle…`). So the strip is gated on the pattern having no `(` of its own:

```sql
col LIKE $pat ESCAPE '\'
AND (
    instr($pat, '(') > 0                                    -- pattern targets a signature → plain match
    OR instr(col, '(') = 0                                  -- candidate has no params → keep as-is
    OR substr(col, 1, instr(col, '(') - 1) LIKE $pat ESCAPE '\'
)
```

Both call sites share the single `DeclaringNameMatch(col)` helper in `SqlReachability`.

---

## Test to add

**File:** `tests/Rig.Tests/Analysis/SqlReachabilityTests.cs`

Fixture:
- Type `Msg` with a `#ctor`
- `Publisher.Send()` calls `new Msg(...)` — ctor edge to `Msg.#ctor`
- `Handler.Process(Msg m)` — receives a `Msg` parameter; its DocID contains "Msg" in the param list

Assertion: `callers "Msg" --roots` finds `Publisher.Send` but NOT `Handler.Process`.

Also verify the contradiction: `path "Publisher.Send" "Msg"` returns a valid path after the fix.

---

## Secondary bug: `rig path` returns empty graph for some patterns

When `path "AI/SmartLetter.EditLetter" "PersonModelCacheFieldUpdated"` returns "0 call edges", it's a separate (but related) issue — the `LoadBoundedGraphAsync` forward seed from `SmartLetter.EditLetter` doesn't find the target within `maxDepth` bounds. The fix above does not address this; track separately.

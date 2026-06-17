# Perf: `rig callers --entrypoints` scans the whole method table twice

**Status:** ✅ FIXED (2026-06-17, branch `perf/entrypoint-review-fixes`) — sourced `reachableSites` from the
already-loaded `graph.Methods` (same `Kind==Method` set, deduped by `SymbolId`) instead of a second
`LoadDeadCodeMethodsAsync` scan. NOTE: the originally-proposed `epData.Methods` was rejected — it dedupes by
`(FilePath, Line)`, not `SymbolId`, a ~0.02% false-negative risk; `graph.Methods` is exact and needs zero
I/O. **Measured (Debug, MedDBase 214k symbols, `callers --entrypoints`):** 6071ms→5768ms median (~300ms,
~5%); output byte-identical. 276/276 tests pass. Originally verified against source at `:264`/`:268`.
**Kind:** redundant same-context whole-table read (not a correctness bug).
**Affected command:** `rig callers <to> --entrypoints` (the `RunEntryPointsAsync` path only).

---

## Summary

`RunEntryPointsAsync` (`src/Rig.Cli/Commands/CallersCommand.cs:235`) issues two independent full scans of
`SymbolFacts WHERE Kind == Method` on the same read context:

```csharp
var methods = await Reads.LoadDeadCodeMethodsAsync(context);                 // :264
var reachableSites = methods.Where(m => reachable.Contains(m.SymbolId))
    .Select(m => (m.FilePath, m.Line)).ToHashSet();                          // :265 — uses only FilePath, Line
...
var epData = await Reads.LoadFactEntryPointDataAsync(context);               // :268
```

- `LoadDeadCodeMethodsAsync` (`Reads.cs:451`) projects `(SymbolId, Name, Modifiers, FilePath, Line, IsOverride)`.
- `LoadFactEntryPointDataAsync` (`Reads.cs:579`) projects the same method table again to `MethodSymbol`
  `(SymbolId, Name, ContainingSymbolId, Signature, FilePath, Line, IsOverride)`.

Both marshal the entire method set (~217k rows on the MedDBase store). `LoadFactEntryPointDataAsync`'s
method list is a near-superset of what `:265` needs — `reachableSites` reads only `FilePath` and `Line`,
both present in `epData.Methods`. So the first scan is avoidable.

## Fix direction

Derive `reachableSites` from `epData.Methods` (already loaded at `:268`) instead of issuing the separate
`LoadDeadCodeMethodsAsync` scan — or, if the `Modifiers`/`IsGenerated` fields are needed elsewhere on this
path (they are not, for `reachableSites`), have the two share one method materialization. Removes one
whole-method-table scan from every `--entrypoints` invocation.

## Not a finding (static-trace artifacts, ruled out)

- `FactPathFinder.ReachedBy` appears at `CallersCommand.cs:197` and `:250`, but the `--entrypoints`
  early-return at `:132-146` returns before `:197` ever runs — only one reverse traversal executes per
  invocation.
- `RunAsync` and `RunEntryPointsAsync` do **not** each load the context/graph — both are loaded once in
  `RunAsync` and passed by reference into `RunEntryPointsAsync`.
- `epData` is loaded once and passed into `DeriveEntryPointsAsync` (the documented share at
  `EntryPointContext.cs:42`); no intra-command epData re-derivation.

## Test to add

A counting assertion that `rig callers <x> --entrypoints` materializes the method table once, not twice.

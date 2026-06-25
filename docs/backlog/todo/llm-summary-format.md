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

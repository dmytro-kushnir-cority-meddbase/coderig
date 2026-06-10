# Incremental / cached indexing — desired end state

> Status: **design intent only, not built.** Captured 2026-06-10. Cache invalidation here is the hard
> problem — a correct implementation has to partially replicate MSBuild's project-evaluation and
> reference-resolution model. Do not start until that cost is accepted.

## Goal

`rig index` re-runs Roslyn over *every* in-scope project on every invocation. On the MedDBase closure
(135 C# projects) the design-time builds alone are ~5 min. The desired end state: a re-index after a
small source change costs proportional to **what changed**, not the whole closure — reusing cached
facts for untouched projects.

## What we'd cache

Per-project facts — the project's slice of `symbol_facts` / `reference_facts` / `type_relation_facts`
(+ its DI registrations). Facts are already DocID-keyed and run-agnostic (post legacy-drop), so a cache
entry is just "these rows belong to project P at content-version V", and merging cached + freshly
extracted facts is a set union with no stitching.

## Why local content-hashing is necessary but NOT sufficient (the "oh boy")

A project's facts are **not a function of its own source alone.** Cross-project binding means a call's
resolved DocID, base/interface edges, and override-dispatch all depend on *referenced* projects' public
surface. So if dependency B adds an overload or changes a signature, a dependent P whose source is
**byte-identical** can bind differently — its cached facts are silently stale. (This session proved the
flip side: per-project isolated compilation in `mine` *dropped* the A2/A3 bindings that one shared
workspace recovered. Binding is non-local; a cache key that pretends it's local will be wrong.)

## Sound invalidation key

```
key(P) = hash(P.sources, P.csproj, LangVersion/defines/Nullable/AllowUnsafe)
       + hash(public-API surface of each TRANSITIVE in-set dependency of P)
```

- The **public-API surface hash** per project is derived from its own `symbol_facts` (exported type/
  member signatures only — the things a dependent can bind against). Private bodies don't affect
  dependents, so editing a method body invalidates only that project, not its dependents.
- Cascade: when a dependency's surface hash changes, invalidate its dependents up the reference graph
  (transitively). A project is **stale** if its own content changed OR any dependency's surface changed.
- Coarser-but-always-correct fallback (ship first, refine later): treat *any* dependency change as
  invalidating dependents. Over-invalidates (re-does more than strictly needed) but is never stale.

## Execution model (changed-as-source, unchanged-as-metadata)

1. Compute `key(P)` for every project; partition into **stale** and **fresh**.
2. Build ONE Roslyn workspace containing **stale projects as source** + **fresh projects as metadata
   DLLs** (their on-disk bins). Cross-edges from stale→fresh bind via metadata (DocIDs are
   assembly-qualified, so they still join). Recall *within* the stale set is intact (they're source
   together).
3. Extract facts for the stale set; **reload fresh projects' facts from cache**; union; publish
   (atomic temp→rename, as today).
4. Persist the freshly extracted stale-set facts back into the cache under their new keys.

This is the precise form of the long-standing "unify index/mine — per-project incremental extraction +
content-hash + replace-not-append" item. It keeps the single-workspace recall guarantee for the part
being re-analysed while paying only metadata cost for the rest.

## The MSBuild replication cost (why this is hard)

To compute `key(P)` and the partition *before* building, we need, without a full design-time build:

- the **project→project reference graph** (have it cheaply already: `DependencyGraph` parses
  `<ProjectReference>` from XML);
- each project's **compilation-affecting properties** (LangVersion, DefineConstants, Nullable,
  AllowUnsafe, TargetFramework) and its **resolved source file set** — these come from MSBuild
  evaluation (globs, conditional `Compile` items, imported `.props`/`.targets`). Reproducing them
  without MSBuild means partially replaying item/property evaluation, or caching the *evaluated* inputs
  from the last full build and trusting them until the `.csproj`/imports change.

A pragmatic first cut: cache the evaluated inputs from the previous full design-time build keyed on the
`.csproj` + imported-file hashes; only re-evaluate (run the design-time build) for projects whose
`.csproj`/imports changed. Source-only edits then skip evaluation entirely and just re-extract.

## Non-goals / open questions

- Not aiming for sub-second incrementality; aiming for "small change ≠ 5-minute rebuild."
- Source generators (the `<Page>Proxy` generator) complicate the surface hash — generated public types
  are part of the surface; the cache must include generated trees, not just hand-written source.
- Cache location/format (per-project SQLite slices vs a content-addressed blob store) is unspecified.
- Interaction with `--from` scoping: the cache is keyed per project, independent of which closure asked
  for it, so closures share cache entries.

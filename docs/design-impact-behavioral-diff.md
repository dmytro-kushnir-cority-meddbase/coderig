# Design — `rig impact` as a behavioral diff (immutable DB per commit)

Status: **design agreed, implementation starting** (2026-06-16). This document is need-first on
purpose: the *design* below may be reconsidered wholesale, but the *need* it serves will not. If a future
reader throws away the mechanism, keep the need section and re-derive.

---

## 1. The need (this outlives any design)

`rig impact` answers "what does this change affect?" Today it answers a **weaker** question than the one
that matters, and the gap is structural, not a bug.

### 1.1 What it does today (file-granular blast radius)

`rig impact --base <ref>` (see `Rig.Cli/Commands/ImpactCommand.cs`):
1. `git diff --name-only base...HEAD` → changed `.cs` files.
2. **Every method declared in a changed file** becomes a seed (file-granular — there is no method
   end-line fact to map a hunk line to its enclosing symbol, so the whole file is the unit).
3. Reverse-reach the seeds → affected entry points, grouped by deployed service.
4. Forward-reach the seeds ∩ derived effects → effects + risky observations + a risk headline.

### 1.2 Why that is not good enough

- **5k-LOC files dominate this codebase.** When a changed file has 141 methods (`Company/Edit.cs` in MR
  !10645) and you edited 3, the seed set is 141. The blast radius (882 EPs for !10645) is "the whole
  file's reach," not "your change's reach." It is an upper bound so loose that the headline number stops
  being information.
- **Formatting triggers false positives.** csharpier reflow / line shifts make a method look changed
  when nothing semantic moved. A line-granular fix (map hunks to enclosing symbols) does *not* solve this
  — reformatting still changes the lines. You would need trivia-insensitive comparison, which is an
  AST/token operation, and the query side is deliberately Roslyn-free (only `Rig.Analysis` references
  `Microsoft.CodeAnalysis`). So "be line-precise" runs straight into "be formatting-immune," and the
  honest fix for *both* is to stop diffing text.
- **Blast radius is not behavior.** Even a perfect changed-symbol set answers "what could this text
  reach," not "what behaves differently now." The reviewer's real question is the latter: *did this EP's
  effects change? did a DB read just become an n+1? did a lock disappear? did a new external call appear?*

### 1.3 What we actually want

A **behavioral diff**: for each entry point the change touches, *what is different about what it does* —
expressed as a delta over the things `rig` already derives (effects, reachability, the effect tree). The
tool surfaces a **compact, high-value signal per EP** and a way to **drill deeper on demand**; the
*interpretation* — judging the delta against the actual code change — is left to the agent or human who
holds that context. The tool gives signal, not verdicts.

This need is invariant. Whether we implement it as a tree diff, a set diff, a token-aware diff, or hand
two artifacts to an agent, the job is: *show me what changed in behavior, not in text.*

---

## 2. The key insight that shapes everything

**Diff the derived graph between two commits, not the source text.**

If you compare the *derived facts* (effects / reachability / effect tree) of the same EP across two
indexes — branch and base — then **formatting immunity is free and total**: a reformatted method produces
byte-identical derived facts, so its contribution to the diff is empty. You never need an end-line fact, a
`--unified=0` hunk parser, a token hash, or any of the line-mapping machinery. The entire
"how do we suppress formatting" subtree disappears, because the layer we diff at is one where formatting
does not exist.

The price is that you need **two indexes** (an index of the branch tip and an index of the base). We
accept that price and make it cheap by treating indexes as immutable, cacheable artifacts (Section 4).

---

## 3. The diff model (committed shape; internals deliberately pluggable)

### 3.1 Two scopes

- **EP add / remove** — a dumb set difference over *all* derived entry points in each store. Cheap, runs
  over the whole store, no reachability needed.
- **EP changed** — the tree / effect / reachability delta, computed **only for paired EPs in the
  file-scoped candidate set**. This is where the existing file-granular over-approximation earns its
  keep: it is no longer the *output*, it is the **candidate bound** that keeps the (expensive) per-EP tree
  diff from running over every EP in the store. Over-approx in → behavioral diff filters → precise out.

### 3.2 Pairing key: `(Kind, Route)`

To diff "the same EP on both sides" you must pair EPs across stores. The pairing key is
**`(Kind, Route)`** from `DerivedEntryPoint` (`Rig.Domain/Data/Facts.cs`). Why this key:

- `Route` is built from the enclosing symbol's FQN with params stripped and namespace prefix removed
  (`FactEntryPointDeriver.BuildActionRoute`), e.g. `M:MedDBase.Pages.SmartLetter.EditLetter(...)` →
  `SmartLetter.EditLetter`. It carries **no line and no parameter list**, so it survives both formatting
  shifts and signature edits (MR !10645 adds `ITransaction optionalTransaction = null` to setters — that
  changes the DocID but **not** the Route).
- The rendered tree header *is* kind + route (`▶ action SmartLetter.EditLetter`), so pairing is literally
  "match trees with the same header line."

Note the asymmetry that drove this choice: `DerivedEntryPoint.Route` is param-free and line-free, but
`DerivedEffect.EnclosingSymbolId` is the full param-qualified DocID. Pair EPs on Route; if/when we key
effects for a set-diff, strip the param list (`Type.Method`) so a pure signature change does not churn.

Residual: a *renamed/moved* EP reads as remove+add rather than "changed." Acceptable — the agent has the
code diff and will see it is a rename.

### 3.3 Output contract: compact hint + drill-down

- **Compact:** one line per changed EP — a tri-lens delta `(effects ±, observations ±, reach ±)` plus a
  stable handle (`(Kind, Route)`). `--format tsv` so an agent can sort/scan programmatically. Keep this
  layer dumb — no judgment baked in.
- **Drill-down:** given a handle, compute the *semantic* delta for that one EP (the specific added/removed
  `(provider, op, resource)` effects, introduced/removed observations — "became an n+1" — and one example
  path per newly-reachable effect). Raw tree *shape* is delegated to the existing `tree`/`path`/`reaches`
  commands run against each store; the agent reads both. No new engine.

### 3.4 The lossy render already normalizes node identity

The tree renderer prints `Type.Method` (no params, no namespace, no line) — see the sample in
Section 8. That lossiness means a *text* diff of two rendered trees is already immune to DocID
param-churn and formatting. The renderer is the normalizer; we did not have to build one. The one thing
the lossy render does *not* fix is **sibling ordering** (today children are emitted in source-line order —
`FactPathFinder` BuildTree, "preserves source order"), which is line-derived and therefore can reorder
across revisions. See deferred item D1.

### 3.5 Symbol-level diff = an optimization, not a rival

Per-symbol fact-delta (a symbol is "changed" iff its own outgoing edges + own effects, keyed without
line, differ between stores) is kept as an **optimization** to find changed symbols cheaply without
recomputing the world. It is not an alternative design; it accelerates candidate discovery.

---

## 4. The substrate: immutable DB per commit

### 4.1 Decision

**An index is an immutable SQLite store keyed by the source commit it was built from.** Many such stores
coexist; commands address one by commit ref. The default is the latest-mined store.

### 4.2 Why commit is a DB boundary, NOT a `RunId`

The store already has a tenant dimension — `RunId` (on every fact row). But `RunId` is **deliberately
blended**: `GraphMaterializer` joins edges cross-run by DocID with *no RunId* filter ("Global
(cross-run): the edges are deduped facts joined by DocID, with no RunId"), so multiple solutions of the
*same checkout* merge into one graph. That is the point of runs.

Commit is an **orthogonal** axis and must be a **hard boundary**: blending two commits' `Foo()` by DocID
would merge them into one meaningless node. Mapping commit onto `RunId` would mean re-threading
run-scoping through `GraphMaterializer` and every read to *defeat* the cross-run dedup the engine is built
around — deep, against-the-grain work.

So:
- **`RunId`** = solution(s) within one checkout — *meant to blend.* Untouched.
- **Commit** = the checkout itself — a separate immutable DB. Today's `.rig` is *already* exactly "one
  checkout's blended runs"; we just have many of them, addressed by hash.

### 4.3 "Immutable" pays for itself

Once a commit's DB is written it is never mutated. Therefore: re-indexing the same commit is a no-op or a
clean rebuild (**this retires the append-double gotcha** documented elsewhere); DBs are safe to cache,
copy between machines, or share; **GC is the only mutation in the system.**

### 4.4 Layout + addressing (target shape)

- Store layout: `…/.rig/<commit>/…` (or `<commit>.rig`), each a self-contained snapshot in today's format.
- Every command grows `--commit <ref>` (default = latest-mined, by index timestamp — *not* git topology,
  since you may index an old commit later). `--commit` accepts a branch name / `HEAD`, resolved via the
  source repo's git → hash → the matching store, so `impact --base main` keeps reading naturally.
- `impact --commit <branch> --base <main>` opens two immutable DBs → the diff of Section 3.

### 4.5 The enabling primitive (build FIRST — this is step 1)

**Stamp every index with its source commit at `rig index` time.** Not captured today (the only SHA in the
tree is the rules fingerprint). Capture, into run/store meta:
- `SourceCommit` — `git rev-parse HEAD` in the source repo.
- `SourceBranch` — `git rev-parse --abbrev-ref HEAD`.
- `SourceDirty` — true if `git status --porcelain` is non-empty (working tree had uncommitted edits at
  index time — the store is NOT at a clean commit; address it as `<hash>-dirty`).

This one fact unblocks: addressing-by-hash, "latest mined," and **auto-coherence** — `impact` can assert
"this store's commit == the diff base" instead of the manual coherence dance done by hand for MR !10645.
It is useful and shippable on its own, before any diff or layout work lands.

---

## 5. What is deliberately deferred to measurement

We argued these to a standstill and chose to decide them on numbers from a real reformatted MR, not up
front. Build them switchable; do not hardcode a winner.

- **D1 — sibling order:** `source` (today) vs `canonical` (lexical, line-independent). Every text-diff
  strategy inherits this; canonical may be needed to stop order-noise, or source may be stable enough.
  One `.OrderBy` toggle at the child level — expose `--order=source|canonical` and measure.
- **D2 — the differ itself:** effect set-diff / DP tree-edit-distance / plain text diff / token-aware diff
  / hand both artifacts to an agent. Any can be the signal; which wins is an experiment.
- **D3 — annotation noise-stripping:** `×N calls` counts, dispatch-basis tags (`«impl-dispatch»`,
  `«via …»`). Keep effect tags (signal); tune the rest, re-measure.
- **D4 — base provenance:** cached *current-main* (cheap, but main-since-fork churn shows as "removed" —
  label it, don't silently list it as branch-removed) vs *main-at-merge-base* (pure branch contribution,
  but a fresh re-index every run). Default to current-main + label; `--base <ref>` can point at any
  commit's DB for merge-base precision.

---

## 6. Operational must-haves (not optional, just not yet built)

- **Retention / GC.** N × ~2 GB (MedDBase) accumulates fast. Need `rig gc` (keep last N + pinned). Decide
  the policy before a disk fills, not after.
- **Dirty working tree.** A WIP-branch index is not at a clean commit — stamp `<hash>-dirty` (or a
  worktree content hash) so it is addressable and never silently aliases the clean commit.
- **TODO(investigate) — incremental indexing to avoid a full rebuild per commit.** A per-commit immutable
  store costs a full re-index per commit (~12–40 min on MedDBase) — the dominant cost of the whole model.
  Investigate building commit B's store *incrementally* from an existing parent/merge-base store A: take
  `git diff A..B` (the same changed-file set `impact` already computes), re-extract Roslyn facts **only for
  changed files**, and copy A's facts verbatim for everything else into the new immutable B store. Facts
  are file-attributed (`SymbolFact.FilePath`, reference `FilePath`), so a file→facts partition is feasible.
  Open questions to resolve before committing: (1) cross-file invalidation — a changed file's *callers/
  overriders/DI registrations* in unchanged files may need re-derivation (signature changes ripple), so
  "only changed files" likely under-extracts; scope the true invalidation set. (2) The assembly registry is
  already content-addressed by fact digest (re-mine of an unchanged assembly is a no-op) — lean on that:
  maybe the unit of reuse is the **assembly**, not the file (re-extract only assemblies whose digest
  changed). (3) Determinism — the incrementally-built store must be byte-identical (or fact-identical) to a
  from-scratch index of B, or the diff layer inherits skew. Likely lands as a `rig index --from-store <A>`
  fast path. Big potential win (turns the per-commit model from minutes to seconds); non-trivial because of
  the ripple/invalidation correctness. Sequence AFTER steps 3–4 prove the diff is worth optimizing for.
- **LIMITATION(verified) — the change-level diff is enclosing-keyed and PATH-INSENSITIVE, so removing one
  caller's path to a still-reachable shared effect-sink is invisible.** Found on MR !10645 ("move healthcode
  off object store"): the diff showed **no** removed object_store reads/writes even though the MR removed
  explicit `srv.SetMedicalPersonSettings(...)` → `Save()` → object_store-write paths (e.g.
  `Doctor/Personal/EditLive.cs`, `Company/Edit.cs`). The effect IS modeled — but it's attributed to the
  generic **sink** `WorkflowMasterBase.Save` (and `ListObjectStoreProxy.SaveObject` / `ObjectStore.Upsert`),
  not to the healthcode method. `effect_removed = base_keys − branch_keys` keyed on
  `(provider, op, resource, Type.Method-no-params)`; because the branch STILL reaches `WorkflowMasterBase.Save`
  via other paths (the `Master` still `Save()`s its queue/other state), that key is present on the branch, so
  it can never be reported removed — even though the healthcode-specific path to it was deleted. (My first
  pass mis-diagnosed this as a reflection/attribute blind spot; that was wrong — the reverse-run "proof" was
  degenerate because the source clone was checked out on the branch, making `--base <branch-sha>` an empty
  diff. The effect is modeled and present on the branch; it's masked, not unmodeled.)
  **Fix = the deferred per-EP attribution (step 4 refinement):** diff each EP's reachable-effect set
  individually — `EditLive`'s save EP reaches the object_store write in main and not in the branch, which a
  per-EP diff surfaces exactly where the global set-diff masks it. (A secondary, smaller gap also exists for
  truly reflection/attribute-driven persistence — object-store-backed `{ get; set; }` properties without
  `[ObjectStoreIgnore]` — which is genuinely unmodeled; but it is NOT the cause here.)

---

## 7. Build order (roadmap; spans multiple contexts)

1. **Commit-stamp primitive** (Section 4.5) — capture + store + surface in `rig runs`. ✅ **DONE**
   (`GitProvenanceProbe` → `Writes.SaveAsync(provenance:)` → `runs` cols → `rig runs` shows `commit=`).
2. **Per-commit `…/.rig/<store-id>/` layout + latest resolution** — ✅ **DONE** (`StoreLayout`: writes go to
   `.rig/<commit-or-ts>/`, a `.rig/LATEST` pointer + newest-by-mtime drives read resolution, legacy flat
   `.rig/rig.db` is moved to `.legacy.bak` on the next index; back-compat intentionally broken). STILL TODO
   in this step: per-command `--commit <ref>` selection + branch→hash resolution (deferred until step 3
   actually needs to address a non-latest store — today every read transparently uses LATEST).
3. **Open-two-stores plumbing + EP add/remove diff** — ✅ **DONE**. `impact` gains `--base-store <path|dir>`;
   absent that, `--base <ref>` is resolved to a commit sha (`git rev-parse`) and matched to an indexed
   per-commit store (`StoreLayout.ResolveStoreDirByRef`, full-sha/short-prefix/exact-id/`-dirty`-stem).
   EPs are derived on both stores (same rules, no query cache → safe on a 2nd store) and set-diffed on
   `(Kind, Route)`; rendered as `+ <kind> <route>` / `- <kind> <route>` (TSV: `ep_added`/`ep_removed`
   rows). Skips cleanly with an "index the base commit" hint when no base store resolves. Tests:
   `StoreLayoutTests` (resolver) + `ImpactEpDiffTests` (identity→empty, different-source→non-empty).
4. **Behavioral delta — the effects/observations reachable from the changed methods, branch vs base** —
   ✅ **DONE** (change-level). `impact` now also reports what the change *does*: it forward-reaches from the
   changed methods on BOTH stores and diffs the reachable effect set + observation set. Base is seeded by
   *param-free* method identity (so a signature-changed method seeds its pre-change self); effects are keyed
   `(provider, op, resource, Type.Method-no-params)` — line/param-free, so formatting and signature edits
   don't churn, only genuine behavior moves. Output: `reach: N methods (±Δ)`, `effects: +X/-Y` (e.g. a new
   DB write / the retired object_store read), `observations: +A/-B` (e.g. **became an n+1**). Human + TSV
   (`effect_added`/`effect_removed`/`obs_added`/`obs_removed`). Tests: `ImpactBehavioralDeltaTests`
   (identical→empty delta; base-lacking-the-methods→effects surface as added).
   *Refinement still open:* per-INDIVIDUAL-EP attribution (which specific common EP sees which part of the
   delta) — the delta is currently change-level (shared across the affected EPs, since they all funnel
   through the changed methods). Per-EP attribution + the tree-shape diff are the measurement-driven tail.
5. Drill-down (`--explain <handle>`); canonical-order toggle (D1); start the differ experiments (D2).
6. Retention/GC; dirty handling; auto-coherence assertion.

---

## 8. Reference: a sample effect tree (the artifact being diffed)

```
▶ action SmartLetter.EditLetter  {⚡ clientpage_proxy:redirect BrowserComponent.HtmlEdit2Proxy}  ⟦MedDBase (iis)⟧
├─ CheckoutService.Checkout «via Document.ICheckoutService»
│  └─ CheckoutService.Checkout
│     ├─ DefaultTransactionFactory.Snapshot «via Core.ITransactionFactory»
│     │  ├─ Transaction.New
│     │  │  └─ Transaction.#ctor
│     │  │     └─ Transaction.PushThis
│     │  │        ├─ IPerformanceLogger.Log
│     │  │        │  └─ InMemoryPerformanceLogger.Log «impl-dispatch»
│     │  │        │     └─ InMemoryPerformanceLogger.AddOpenItem  {⚡ actor:tell OpenItems}
│     │  │        │        └─ Process.tell<T> «opaque: Echo actor framework»
│     │  │        ├─ PerformanceLogger.get_Factory  {⚡ reflection:load MMS.ClassFactory}
│     │  │        │  └─ ClassFactory.CreateInstance
│     │  │        ⋮
```

Node identity is `Type.Method` (param/namespace/line stripped — already normalized). Effect tags
`{⚡ … 🔒 … 📁 …}` are the signal. Sibling order is source-line today (see D1).

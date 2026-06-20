# Design-time-build cache (`rig index --reuse-build-cache`)

> Status: **built, opt-in, validated on playgrounds.** Since 2026-06-20: per-project Paket invalidation
> (`PaketClosure`), a functional-core refactor of the cache (pure `Of`/`Material`/`Decide` + thin shell),
> and the `--verify-build-cache` guardrail have ALL shipped (see "Shipped 2026-06-20" below). **Still NOT
> trusted on MedDBase** until `--verify-build-cache` is actually *run* there (`0 MISMATCH`) — that run is
> the gate. Open 6694-error investigation below. Captured 2026-06-18, updated 2026-06-20.

## Shipped 2026-06-20
- **Per-project Paket invalidation (`PaketClosure`).** Supersedes the `rootManifests`-invalidate-all idea in
  "Config-driven invalidation (planned)" below for the Paket case: instead of hashing the whole root
  `paket.lock`/`paket.dependencies` into every project, each project folds only the resolved entries in its
  transitive closure (derived from COMMITTED inputs — its `paket.references` closed over the lock graph — NOT
  the racy `obj/*.paket.props`), plus the global resolution settings. A bump invalidates only the projects
  that resolve it (measured on MedDBase: a leaf-package bump went `0 hit/136 miss` → `134 hit/2 miss`).
  Conservative: dependency-edge framework restrictions are not applied, so the closure is a superset of
  Paket's resolution → can only over-invalidate, never replay stale.
- **Per-project Central Package Management invalidation (`CpmClosure`).** The same scoping for the NuGet
  central version list: `Directory.Packages.props` removed from the wholesale ancestor walk, folded per
  project (referenced `<PackageVersion>`s + the props' global part). See "Per-project scoping … CPM" below.
  The general `rig.rules.json` config design is still unbuilt, but both central version lists (Paket + CPM)
  are now scoped; the remaining allowlist entries (`Directory.Build.props`, `packages.config`, nuget.config)
  are either correctly global or classic-NuGet wholesale.
- **Functional core / imperative shell.** The correctness-bearing logic is now pure and unit-tested without a
  filesystem: `BuildInputFingerprint.Of(BuildInputs)` (gathered by the impure `Gather`), `PaketClosure.Material`
  / `.ComputeMaterial(text)`, and the hit/miss verdict `BuildCacheDecision.Decide(fp, stored)`.
  `BuildResultCache.Load` is now a pure IO port (no compare). `BuildOrLoad` reads prepare → decide → commit.
- **`--verify-build-cache`** — see below (now BUILT).

## Why

`workspace-build` — the out-of-process MSBuild design-time builds — is the dominant indexing cost
(~53% of a MedDBase Pages index; ~140–270s wall). Its output that rig consumes (resolved references,
source files, compile options) is a **function of the build configuration, not source content**, so it's
invariant under ordinary code edits. Caching it lets a re-index skip the build phase and pay only
Roslyn parse/bind/extract — and it sidesteps the parallel-build nondeterminism (resolve once, replay).

## What's cached

Per project, the slice of the design-time build that `BuildWorkspaceFromResults` consumes — a plain,
serializable `ProjectBuildInfo`: `ProjectFilePath`, `References`, `ProjectReferences`, `SourceFiles`,
`AnalyzerReferences`, `PreprocessorSymbols`, `Properties`. Step 1 decoupled `BuildWorkspaceFromResults`
from Buildalyzer's `IAnalyzerResult` so the data can come from a build *or* the cache.

Sidecars: `.rig/dtb-cache/<hash-of-project-path>.json` = `{ fingerprint, ProjectBuildInfo }`, **outside**
the per-commit store so they persist/share across indexes. Best-effort: any IO/JSON failure → miss
(rebuild). On a hit the design-time build is skipped; Roslyn still reads source + reference bytes fresh.

## The fingerprint (cache key)

`BuildInputFingerprint.Compute(projectPath)` — pessimistic, fast, reads **no source/reference content**
(content is Roslyn's job; it re-reads on every index). It hashes only build-STABLE inputs the build
itself never rewrites, so it can't race the build's obj output:

- the `.csproj` (stat: len+mtime),
- a dependency-manifest **allowlist** walked up to the repo root (stat): `Directory.Build.props`/
  `.targets`, `Directory.Packages.props`, `global.json`, `paket.lock`, `paket.dependencies`,
  `paket.references`, `packages.config`, `nuget.config`, and `.paket/Paket.Restore.targets`,
- the **set** of `*.cs` paths under the project dir (sorted, paths only — detects add/remove/rename;
  removals also self-heal via the `File.Exists` filter; edits stay a HIT).

### Decisions learned the hard way (measured)
- **`*.cs` glob ≈ 1.9s / 143 projects** — cheap (parallel → sub-second); the spook was unfounded.
- **Do NOT fingerprint build outputs** (`obj/project.assets.json`, `project.nuget.cache`,
  `obj/*.paket.props`). Hashing/stat-ing them took multiple indexes to converge because the
  out-of-process build rewrites them and the post-build read races the flush. Inputs don't race →
  converges in **one** run (cold 0/7 → warm 7/7, `design-time-builds` → 0.0s on DeepChain).
- **`assets.json` content-hash = 4.5s; stat = 16ms** — but stat churns (mtime), so we use neither;
  build-stable inputs only.

### Dependency-mechanism reality
MedDBase mixes **Paket + classic NuGet + GAC + "god knows what else."** Versions are NOT in the csproj
(0 `PackageReference`); Paket holds them in `paket.lock` (root) + per-project `paket.references`, wired
via a single `<Import …\.paket\Paket.Restore.targets>` in each csproj which generates `obj/*.paket.props`
at restore. Hence the allowlist above. **Known gap:** a version pinned by a mechanism not in the
allowlist won't flip the key — which is why `--reuse-build-cache` is opt-in.

## Config-driven invalidation (planned)

The allowlist is hardcoded today; it should be repo-declared. Home: `rig.rules.json`, new `buildCache`
section (defaults = current allowlist, so zero-config = today's behaviour):

```json
{
  "buildCache": {
    "invalidation": {
      "rootManifests":    ["paket.lock", "paket.dependencies", "global.json",
                           "Directory.Packages.props", ".paket/Paket.Restore.targets"],
      "projectManifests": ["*.csproj", "paket.references", "packages.config",
                           "Directory.Build.props", "Directory.Build.targets"],
      "sourceSet":        ["**/*.cs"],
      "excludeDirs":      ["obj", "bin", ".vs"],
      "hashMode":         { "default": "stat", "content": [".paket/Paket.Restore.targets"] }
    }
  }
}
```

- **rootManifests** invalidate all projects; **projectManifests** invalidate one; **sourceSet** = path-set.
- **Mechanism detectors**: auto-extend the set on markers (`paket.lock` → Paket; `packages.config` →
  NuGet classic; `Directory.Packages.props` → CPM), so zero-config covers the common cases and config
  only names the exotic.
- Threading: `BuildCacheConfig` on `AnalysisRuleSet` → `AnalyzeAsync` → `LoadAsync` → `BuildWorkspace` →
  `BuildInputFingerprint.Compute(projectPath, config)`.

### `--verify-build-cache` (the guardrail) — BUILT 2026-06-20
Builds every project anyway (ignoring hits) and **diffs the freshly-built `ProjectBuildInfo` against what a
hit would have replayed** (`BuildInfoEquivalence.Compare`, set-compared so parallel-build ordering isn't a
false mismatch), reporting any drift — i.e. an input the fingerprint failed to fold (a latent stale-hit).
Refreshes the sidecar either way; prints `build-cache verify: M match, N MISMATCH, K no-baseline`. Catches
the completeness gap no fingerprint unit test can. Refreshes the sidecar either way.

**Validated on MedDBase 2026-06-20.** First run flagged **134/136 on `Properties`** while EVERY consumed
input (References/ProjectReferences/SourceFiles/AnalyzerReferences/PreprocessorSymbols) matched — because
Buildalyzer's `Properties` dict carries hundreds of environment/path/timestamp entries rig never reads. So
`BuildInfoEquivalence` was calibrated to compare ONLY the consumed property slice (the 5 keys
`BuildProjectInfo` reads). Re-run: **135 match, 1 MISMATCH, 0 no-baseline**. The lone mismatch
(`MedDBase.ReferralSystemService.Common: References (+0/-1)`) is one reference resolving in the cached build
but not the fresh one with NO input change — i.e. parallel-MSBuild reference-resolution NONDETERMINISM (the
very thing the cache neutralises by resolve-once-replay), not an under-specified fingerprint. **Conclusion:
the fingerprint — incl. the new per-project Paket scoping — captures every consumed build input; the cache is
sound on MedDBase.** Caveat: verify can't distinguish a nondeterministic build from a fingerprint gap (both
show as a diff), so a small, non-reproducing `References ±N` with no input change reads as build flakiness.

## Per-project scoping of the other central version list: CPM (shipped) vs Directory.Build.props (no)
The PaketClosure technique (scope a central version list per project so a bump invalidates only the projects
that resolve it) was applied to the other mechanisms in the allowlist:

- **`Directory.Packages.props` (Central Package Management) — SHIPPED 2026-06-20 (`CpmClosure`).** It's the
  NuGet equivalent of `paket.lock`: `<PackageVersion Include="X" Version=".."/>` centrally, version-LESS
  `<PackageReference Include="X"/>` per project. It used to be hashed WHOLESALE into every project under it, so
  one `<PackageVersion>` bump invalidated all — the exact problem PaketClosure fixed for Paket. Now removed
  from `AncestorConfigFiles` and folded PER PROJECT: `CpmClosure` parses the props into a package→version map
  + a "global blob" (everything that ISN'T a `<PackageVersion>` — properties, imports, `<GlobalPackageReference>`),
  reads the project's referenced package names from its csproj (+ ancestor `Directory.Build.props`), and folds
  only the referenced versions + the global blob. SIMPLER than Paket — no transitive walk, because CPM versions
  only a project's DIRECT references (transitive versions are NuGet-resolved into the racy `obj/project.assets.json`,
  which we never read). Conservative fallbacks: `<CentralPackageTransitivePinningEnabled>` (versions govern
  transitive deps too → fold ALL versions) and any unparseable csproj/props (fold all). **MedDBase reality:**
  CPM is used ONLY in `src/audits/` (30 `<PackageVersion>`, no transitive pinning), which isn't in the
  `MedDBase.Site` `--from` closure rig indexes — so the win is ~zero for the indexed set today; built for
  correctness + other repos / future scopes. Tests: `CpmClosureTests`.

- **`Directory.Build.props` / `.targets` — NO, correctly global (left as-is in the allowlist).** It's arbitrary
  MSBuild (properties, items, imports, targets), not a version list — a change can affect any project under it
  in ways not tied to a package, so there's no sound per-package scoping. It IS already scoped by DIRECTORY
  LOCALITY via the ancestor walk: a project only folds the `Directory.Build.props` files in ITS ancestor chain,
  so a leaf-subtree props only invalidates that subtree; only a repo-ROOT one invalidates everyone, correctly
  (MedDBase's root sets `LangVersion`, `Deterministic`, … — genuinely global to every compile).

> **One-time invalidation:** removing `Directory.Packages.props` from the wholesale ancestor walk drops its
> per-dir absent-markers from the hash, so EVERY project's fingerprint shifts once — the next index after this
> change is a full cold rebuild that repopulates the cache (then stable). Same one-time cost the Paket scoping
> already incurred. Expected; it's a cache.

### Phasing (general config — still planned; Paket already scoped via PaketClosure)
1. Extract allowlist → `BuildCacheConfig` (current defaults; no behaviour change).
2. Wire from `rig.rules.json`.
3. Per-pattern `hashMode`.
4. Mechanism detectors + custom globs.
5. ~~`--verify-build-cache` guardrail~~ — **DONE**.

## TODO(investigate) — per-project compilation/fact cache across branches (approach C, NOT built)
The `--reuse-build-cache` work above caches the design-time BUILD output (references/options/source-set) and
skips the ~53% build phase; the remaining ~47% — Roslyn parse/bind/**extract** — still runs in full every
index, even for projects whose source is byte-identical to a prior branch's. Approach C would cache the
EXTRACTED FACTS per project and replay them across branches when nothing relevant changed.

**Why it's hard (the reason it's deferred, not the reason to skip it):** facts are extracted from ONE
cross-project Roslyn workspace (dependencies are live `ProjectReference`s over the transitive closure — see
`BuildWorkspaceFromResults`), so the relational facts (call references, type relations, dispatch/devirt)
resolve symbols ACROSS project boundaries. A sound per-project cache key therefore cannot be just the
project's own source content — it must fold in the transitive closure's **public-API (ABI) surface** + the
metadata-reference identities + source-generator inputs/versions. Only the `Symbols` (declaration) tier is
soundly cacheable on own-source alone. Get the ABI key wrong and you replay stale cross-project facts →
wrong reachability, the product's core. This is essentially incremental compilation.

**Sibling work:** `docs/design-impact-behavioral-diff.md` already carries `TODO(investigate) — incremental
indexing` (re-extract only `git diff`-changed files, copy the rest from a parent store). That is the same
family — reuse facts for unchanged units — but FILE-attributed within one commit lineage; C is PROJECT/ABI-
attributed and shared across branches. Whichever lands first should establish the cross-unit invalidation
discipline (the ripple set: a changed unit's callers/overriders/DI in OTHER units) the other will reuse.

**Sequencing:** do this LAST. Lower-risk, already-shipped wins (impact cache, Paket scoping) and the
`--verify-build-cache` MedDBase run matter more first. When taken on: start with the trivially-sound tier
(cache `Symbols` on own-source content, measure), and only then tackle the ABI-closure key for the relational
facts — gated on a verify-style differential harness (re-extract and diff) exactly as the build cache was.

## OPEN: 6694 consistent compilation errors with `--reuse-build-cache` (MedDBase Pages, Release, p16)

Symptoms: ~6694 `CS0246`/`CS0234`/`CS1061` — missing F#/cross-project types
(`ClientDataTransformationTools.Common.Mapping.Parsing/Tal/Dynamic`, `ITalTableSchema`,
`IDynamicImportEntity`, LanguageExt `Option.IfNoneFail`). **Consistent** (not the earlier flaky 0→161).

Hypotheses, in order:
1. **F#/VB output DLLs not present in `bin/`** → `NonCSharpProjectReferenceDlls`/`ResolveBuiltOutputDll`
   can't resolve them → `CS0246` for every C# file using those types. rig design-time-builds only C#
   projects; F#/VB DLLs must be pre-built. This would be consistent and **cache-independent**.
2. **Cache poisoned by a raced cold populate** → consistently replays incomplete references.
3. **Cache stores references that lose the F#/VB resolution** that `BuildWorkspaceFromResults` would
   recompute from a live `IAnalyzerResult`.

Disambiguation: (a) run WITHOUT `--reuse-build-cache` — if errors drop, cache is the cause (→ #2/#3);
if unchanged, it's a baseline F# recall gap (→ #1). (b) Clear `.rig/dtb-cache`, repopulate at
`--parallelism 1`. (c) Check whether the affected dependency projects are `.fsproj` and whether their
output DLLs exist on the tree.

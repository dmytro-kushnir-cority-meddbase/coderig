# Design-time-build cache (`rig index --reuse-build-cache`)

> Status: **built, opt-in, validated on playgrounds; NOT trusted on MedDBase yet** (open 6694-error
> investigation, below). Captured 2026-06-18.

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

### `--verify-build-cache` (the guardrail)
A mode that builds everything anyway (ignoring hits) and **diffs freshly-built `ProjectBuildInfo`
against the cached one per project**, reporting mismatches. Catches an under-specified invalidation
config (silent recall gap) AND a poisoned cache. Land this **before** flipping `--reuse-build-cache` on
by default.

### Phasing
1. Extract allowlist → `BuildCacheConfig` (current defaults; no behaviour change).
2. Wire from `rig.rules.json`.
3. Per-pattern `hashMode`.
4. Mechanism detectors + custom globs.
5. `--verify-build-cache` guardrail.

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

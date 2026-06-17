# Perf: `rig tree` runs an unconditional run/deployment EF query on the warm (cache-hit) path

**Status:** ✅ FIXED (2026-06-17, branch `perf/entrypoint-review-fixes`) — gated `LoadDeploymentsAsync`
on `File.Exists(deployments.json)` before resolving the solution path. **Measured (Debug, dogfood warm
full-hit):** deployment lap 570ms→1ms, total 638ms→70ms (**9×**); Release maps ~476ms→~66ms. Deployments-
present path re-verified (service chip renders, attribution intact). 276/276 tests pass.
Originally: highest-impact finding of the 2026-06-17 entry-point self-review.
**Kind:** avoidable latency on the common path (not a correctness bug).
**Repro store:** `C:\temp\rig-dogfood` (no `deployments.json` present — the common case).
**Affected command:** `rig tree` (most visible on a full cache hit; the same eager query also sits in every command that calls `LoadDeploymentsAsync`).

---

## Summary

`TreeCommand.RunAsync` (`src/Rig.Cli/Commands/TreeCommand.cs:298`) calls `LoadDeploymentsAsync`
**unconditionally**:

```csharp
var deployments = await LoadDeploymentsAsync(context, workingDirectory);
```

`LoadDeploymentsAsync` (`EntryPointContext.cs:22-27`) eagerly awaits the solution-path argument *before*
the deployment map can short-circuit:

```csharp
internal static async Task<DeploymentMap> LoadDeploymentsAsync(RigDbContext context, string workingDirectory, TextWriter? log = null) =>
    await DeploymentMap.LoadAsync(
        workingDirectory: workingDirectory,
        solutionPath: await PrimaryDeploymentSolutionPathAsync(context),   // <-- EF query runs here, always
        log: log);
```

`PrimaryDeploymentSolutionPathAsync` (`EntryPointContext.cs:35-36`) issues `Reads.ListRunsAsync(context)`,
which first does `context.Database.CanConnectAsync()` then materializes the Runs query. But
`DeploymentMap.LoadAsync` short-circuits to `Empty` the instant `deployments.json` is absent — so on the
common no-deployments path the run query is computed and its result discarded.

The sting: **on a full `rig tree` cache hit, the graph is never loaded** (the forest + effects come from
`.rig/cache.db`), so this run query is the *first* EF touch on the context and absorbs EF's cold
model-build + first-connection cost. Measured on the dogfood store: a warm full-hit run was ~484ms total,
of which **~410ms** was this deployment/run lap, with no `deployments.json` present. That lap is the
dominant warm-query cost and is entirely avoidable.

## Fix direction

Gate the run query behind the `deployments.json` existence check that `DeploymentMap.LoadAsync` already
does internally — i.e. don't compute `PrimaryDeploymentSolutionPathAsync` unless the file exists. Options:

1. In `LoadDeploymentsAsync`, `if (!File.Exists(Path.Combine(workingDirectory, "deployments.json"))) return DeploymentMap.Empty;` before touching the context.
2. Or pass a lazy solution-path resolver (`Func<Task<string?>>`) into `DeploymentMap.LoadAsync` so it only
   resolves the path after it has confirmed the file exists.

Option 1 is the smallest change and eliminates the entire lap on the default path. It helps **every**
command that calls `LoadDeploymentsAsync` (tree/derive/callers/path/reaches), not just tree — the warm
cache hit just makes it most visible here.

## Verification notes

- `DeploymentMap.LoadAsync` empty-short-circuit on missing `deployments.json`: confirmed.
- The trace's EP-site derivation under `LoadOrDeriveEpSiteKindAsync` does NOT run on this path (gated on
  `!deployments.IsEmpty`, `EntryPointContext.cs:151`) — so the cost is purely the eager run query, not EP
  derivation.

## Test to add

A timing/behavioral assertion that a `rig tree` run with no `deployments.json` issues **zero** EF queries
against the Runs table (or that `LoadDeploymentsAsync` returns `Empty` without opening the context). The
existing tree cache tests are the natural home.

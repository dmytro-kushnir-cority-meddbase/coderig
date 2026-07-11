# PerformanceLogger.Factory — double Startup() on lost lazy-init

**Detector:** `lazy_init_race` · **Verified:** 2026-06-26 (source read by orchestrator) · **Severity:** medium (needs concurrency)
**Site:** `MMS.Diagnostics.PerformanceLogger.get_Factory` — `src/mms/MMS.Diagnostics/PerformanceLogger.cs:14-34`

## Defect
Unguarded lazy singleton (**no lock**) whose init has a **side effect** — `instance.Startup()`. A lost double-init runs
`Startup()` twice, and a concurrent reader can see the published `instance` before `Startup()` completes.

```csharp
if (instance == null)                       // lock-free
{
    ...resolve assembly/class from AppSettings...
    instance = (IPerformanceLogger)factory.CreateInstance(assembly, name);  // publish
    if (instance == null) instance = new NullPerformanceLogger();
    instance.Startup();                      // ← side-effecting; runs on every racing initializer
}
return instance;                             // a racer can return the instance pre-Startup
```

Two concurrent callers both see `instance == null`, both construct, both call `Startup()`. The loser's instance is
overwritten but its `Startup()` already ran — a side-effecting startup executed twice, not an idempotent cache fill.
This is why `lazy_init_race` should not be uniformly `low`: side-effecting / partial-publish inits are higher impact
than the idempotent regex/dict lazy-inits.

## Fix sketch
`Lazy<IPerformanceLogger>` / `LazyInitializer.EnsureInitialized`, or a lock + recheck with `Startup()` inside the lock and
the field published last (and `volatile` if any lock-free read remains).

## Status
Not yet filed in GitLab.

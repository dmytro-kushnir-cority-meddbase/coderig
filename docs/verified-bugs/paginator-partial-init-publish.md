# Paginator.Initialise — partial-init publish race

**Detector:** `lazy_init_race` · **Verified:** 2026-06-26 (source read by orchestrator) · **Severity:** medium (needs concurrency)
**Sites:** `PdfService.Paginator.Initialise` — `src/pdf/PdfService/Paginator.cs:33`; `PdfService2.Converters.Paginator.Initialise` — `src/pdf2/PdfService2/Converters/Paginator.cs:87`

## Defect
The static `paginatorSections` array reference is **published before the fill loop runs**, so a concurrent thread can
observe a non-null but partially-populated array and read `null`/garbage sections.

```csharp
private static void Initialise(string host)
{
    if (paginatorSections == null)                       // lock-free, no lock
    {
        paginatorSections = new string[markups.Length + 1];   // ← reference published HERE
        var paginator = Resource.ReadAllText<Paginator>(...)...;
        var parts = paginator.Split(markups, ...);
        for (var i = 0; i <= markups.Length; i++)
            paginatorSections[i] = parts[i];                  // ← filled AFTER publish
    }
}
```

Thread T1 assigns the array then starts filling it; thread T2 hits `if (paginatorSections == null)`, sees **non-null**,
skips init, and reads a half-filled array. No lock, so this is not even a double-checked-lock — it's an unguarded
lazy-init with publish-before-fill.

## Fix sketch
Build the array into a local, fill it fully, then publish (`paginatorSections = local;`) as the last step — or guard the
whole init with a lock + recheck and publish-last. Worst-case benign path is a double rebuild; the bug is the torn read.

## Status
Not yet filed in GitLab.

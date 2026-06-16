# Bug: `rig derive` omits an entry point that `rig tree` labels `▶ action`

**Status:** RESOLVED — NOT a derivation miss. `RefreshMedicareVerificationInfoPanel` IS a correctly-derived
action entry point (confirmed in `rig derive --format tsv` and `rig tree`'s fresh `--no-cache` chip; there
are 5,486 action EPs). The original hypothesis (a predecessor pre-filter in `FactEntryPointDeriver`) is
WRONG — `DeriveActions` has no caller/predecessor gate. The real cause: the **human-readable `rig derive`
output prints only a per-kind SAMPLE** (`Take(limit/4 + 1)`) with no truncation marker, so `rig derive |
grep <name>` is a silent false negative for any EP past the sample. Fix: derive now prints a `… +N more
<kind> (sample shown; rig derive --format tsv lists all)` line when a kind is truncated. For a complete,
greppable list use `rig derive --format tsv`. No EP-derivation change.

**Repro DB:** `C:\Git\meddbase-analysis`  
**Affected commands:** `rig derive` (default output only)

---

## Symptom

```powershell
# Shows ▶ action — EP detected at render time
rig tree "DetailsLive.RefreshMedicareVerificationInfoPanel" --full
# → ▶ action DetailsLive.RefreshMedicareVerificationInfoPanel  ⟦MedDBase (iis)⟧

# Not in derived EP table
rig derive | Select-String "RefreshMedicare"
# → (no output)

# Consequence: callers --entrypoints silently omits it
rig callers "SomeTarget" --entrypoints
# → RefreshMedicareVerificationInfoPanel absent even when relevant
```

`rig tree` applies the `[ClientAction]` EP rule at render time and labels the method `▶ action`. `rig derive` does not include it in the stored entry-point table. The two EP detection paths disagree.

---

## Method under test

`MedDBase.Pages.Patient.DetailsLive.RefreshMedicareVerificationInfoPanel`  
Source: `MedDBase.Pages/Patient/DetailsLive.cs:3211`

```csharp
[ClientAction]
public void RefreshMedicareVerificationInfoPanel()
{
    Response.LateActions.UpdateHtml("PatientMemberNumber", Person.PatientMemberNumber);
    // ... further UpdateHtml calls ...
}
```

It has a C# predecessor: `OnFirstInitialise` calls it directly (DetailsLive.cs:261), and `MedicareVerification` references it via `nameof` (DetailsLive.cs:3208).

---

## Root cause hypothesis

`rig derive` uses an offline EP derivation pass. For `[ClientAction]`-style rules the most likely failure mode is a **predecessor pre-filter**: if the derive pass prunes EP candidates that have any C# callers before applying attribute rules, `RefreshMedicareVerificationInfoPanel` is dropped because `OnFirstInitialise` calls it.

`rig tree` re-evaluates EP rules on the fly against the fact DB when a method is supplied as the root, without that pre-filter. So the render-time check correctly applies the `[ClientAction]` rule and emits `▶ action`.

The predecessor pre-filter is a performance optimisation that is semantically incorrect for attribute-driven EP rules: a `[ClientAction]` method is an entry point unconditionally — having an internal C# caller (e.g. for page initialisation) does not change its reachability from the client.

---

## Impact

`rig callers X --entrypoints` is supposed to return all rule-detected entry points that reach `X`. Because `RefreshMedicareVerificationInfoPanel` is absent from the derived EP table, it can never appear in `--entrypoints` output regardless of what `X` is. The miss is silent — no error, just an incomplete EP list. Discovery requires knowing the method name and running `rig tree` on it directly.

---

## Fix direction

Attribute-driven EP rules (`[ClientAction]`, `[HttpGet]`, etc.) must be evaluated **without** a predecessor pre-filter. Only heuristic/structural rules (e.g. "no C# callers" as a proxy for "externally invoked") should consider predecessor counts. Separate the two rule families in the derive pass and skip the pre-filter for attribute-driven rules.

---

## Test to add

- Fixture: a class with a `[ClientAction]`-attributed method that is also called from another method in the same class (simulating a page that calls the action from `OnFirstInitialise`).
- Assert: `rig derive` includes the `[ClientAction]` method as an entry point.
- Assert: `rig tree` and `rig derive` agree on its EP status.

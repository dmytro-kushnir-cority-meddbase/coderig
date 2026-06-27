# `non-transactional-write-loop`: a write batch with no enclosing transaction (partial-failure hazard)

**Status:** todo · **Found:** 2026-06-26 (appointment-lifecycle deep-dive, debt-chase champion) · **Family:** detector-recall (new candidate)

## The gap
rig's loop-related detectors (`n_plus_1`, `dual_write`) fire on **read** loops (per-iteration `llblgen:read`/`fetch`).
There is **no detector for a WRITE-class effect under a loop with no enclosing transaction** — the classic non-atomic
batch: a `foreach … { x.Save(); }` where each iteration commits independently, so a throw at iteration K leaves 1..K-1
committed and K..N not, with no rollback.

## Evidence (verified in source, appointment-lifecycle dive)
`LetterView.PrintAndMarkSent` (debt-chase) → `MarkAllSentConfirm` (`LetterView.cs:75`):
```csharp
foreach (var workflow in Main.GetControllersForCreditor(pkCreditor))
    workflow.MarkStateLetterSent();      // Controller.cs:388 — parameterless, NO ITransaction
```
Each `MarkStateLetterSent()` does fetch + `AuditLog…Log()` + `Save()` as its own implicit transaction. Preceded by
`PrintAll()` which renders/uploads PDFs for ALL letters up front → a partial failure leaves printed-but-not-marked
letters → on retry they re-print/re-send (the `if (LetterSent) return;` guard only protects the *marked* ones) →
**double-chase to the patient.** rig surfaced the suspicious shape (all-`[loop]`-fanned write counts) but **no detector
flagged it** — the missing-transaction hazard was found only by reading the three methods. This is the lone
non-transactional save-path of the five appointment-lifecycle champions (booking/invoice/billing-item/change-invoice
all thread a transaction).

## Shape to detect
A write-class effect (`llblgen:write`/`save`/`delete`/`bulk_write`/`add`, `audit:write`, `object_store:write`) reachable
**under a loop** (rig already has the 🔁 loop annotation) on a path from the EP where **no
`RequireTransaction`/`RequireTransaction.Retry`/`ITransaction`-threaded frame encloses the write**.

## Calibration is the whole problem — transaction context is established UPSTREAM
The four other lifecycle save-paths are TRUE NEGATIVES — they thread a transaction, but the `RequireTransaction.Call/Retry`
sits at the EP/orchestrator frame and the `tm`/`ITransaction` flows DOWN into deep callees (e.g.
`SaveFinal`→`Book`→`GenerateInvoices`→`AddServiceCharges` all take `tm`). So the detector cannot look at the writing
method alone — it must know whether a **transaction span encloses the write on the call PATH**.
- **Reuse the span machinery.** rig already emits `transaction_spans_effect` (`FactResourceSpanRule`) — the same signal
  that downgrades `race_window`. A write whose path is inside a transaction span → suppress (or downgrade). A write under
  a loop with NO transaction span on the path → flag.
- This is the SAME "context-on-the-path" problem as the lock-span in [[lazy-init-race-precision-tightening]] and the
  must-run propagation in [[branch-aware-effects]] — build the path-context substrate once, all three ride it.
- **Confidence LOW + disclosed.** FP-calibrate against the 4 transactional save-paths (must NOT fire) and the debt-chase
  loop (MUST fire) as the regression pair.

## Why it matters
Non-atomic write batches are a real, shipping defect class (the verified debt-chase double-chase) that is **invisible
today** — the existing loop detectors only see reads. A recall gap on a correctness-class hazard, in the user's core
daily platform. Pairs with the transaction-span substrate and [[branch-aware-effects]].

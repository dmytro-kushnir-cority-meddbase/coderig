using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Stage-2 HAZARD derivation: a post-pass over the already-derived effects that matches PATTERNS across
// effects (vs. an Effect, which is a single point fact). The first hazard is `race_window` — a
// read-modify-write / TOCTOU candidate: a READ of a shared cell followed by a WRITE of the SAME cell in
// the same method, the lost-update / check-then-act shape (RCA #2930: two users read appointment status,
// both write, one clobbers the other).
//
// This is a CODED matcher, not a pattern DSL. What is DATA is only which provider:operation is the "read"
// leg and which is the "write" leg (defaulted to shared_state:read / shared_state:mutate — the legs the
// FR-1 static-field arms emit). The pairing logic — same enclosing method, same cell, read-before-write
// line order, transaction-bracket tiering — is code.
//
// Annotate-only: it ADDS a race_window observation to the qualifying WRITE (mutate) effect and returns the
// effect list otherwise untouched. It removes/suppresses no effect and no observation. The race_window is
// modeled as an EffectObservationInfo (a note on an effect), reusing the existing observation model so it
// surfaces wherever observations already do.
//
// v1 is INTRA-METHOD: read and write must share an enclosing method, so the line order is an exact,
// cheap proof of "read happens-before write" on a straight-line path. Cross-method (path-level) pairing —
// a read in one method and a write in a callee/caller reached from it — is a follow-up that needs the call
// graph + a happens-before relation over it; deliberately not built here.
public static class FactHazardDeriver
{
    // Default legs: the FR-1 static-field arms. shared_state:read is the "check" (read of a shared cell);
    // shared_state:mutate is the "act" (write of a shared cell). DATA, not code — overridable by the caller
    // / a future config so the matcher can be retargeted without touching the pairing logic.
    public const string DefaultReadProvider = "shared_state";
    public const string DefaultReadOperation = "read";
    public const string DefaultWriteProvider = "shared_state";
    public const string DefaultWriteOperation = "mutate";

    // The observation TYPE this hazard emits, and the two disclosure tiers. We ALWAYS emit for an RMW pair
    // (a transaction does NOT make it safe — read-committed isolation does not prevent lost updates) and
    // DISCLOSE the isolation context via the Reason/Confidence rather than suppressing the bracketed case.
    public const string RaceWindowType = "race_window";
    private const string ReasonNoIsolation = "rmw_no_isolation_on_path";
    private const string ReasonInTransaction = "rmw_in_transaction_verify_isolation";
    private const string ConfidenceHigh = "high";
    private const string ConfidenceMedium = "medium";
    private const string Basis = "fact_derived";

    // The span observation whose presence on BOTH legs downgrades the race_window to the "in a transaction,
    // verify isolation" tier. Mirrors FactResourceSpanRule's emitted ObservationType for a using(tx) scope.
    private const string TransactionSpanType = "transaction_spans_effect";

    // Returns `effects` with a race_window observation appended to every WRITE-leg effect that is preceded
    // (earlier line, same enclosing method) by a READ-leg effect on the SAME cell. The read/write legs are
    // matched on (Provider, Operation); the cell is the effect's ResourceType (the field-slot DocID under
    // slot-precise field rules, so `read TypeX.A` and `write TypeX.B` do NOT falsely pair). The input list
    // is not mutated; effects without a new finding are returned by reference.
    public static IReadOnlyList<DerivedEffect> DeriveRaceWindows(
        IReadOnlyList<DerivedEffect> effects,
        string readProvider = DefaultReadProvider,
        string readOperation = DefaultReadOperation,
        string writeProvider = DefaultWriteProvider,
        string writeOperation = DefaultWriteOperation
    )
    {
        // Reads grouped by (enclosing method, cell) so each write looks up its candidate prior reads in O(1).
        // Key on the enclosing id + the cell; a null enclosing id can never be an intra-method pair (it is
        // not a call-graph node) so such effects are skipped on both legs.
        var readsByCell = new Dictionary<(string Enclosing, string Cell), List<DerivedEffect>>();
        foreach (var e in effects)
        {
            if (
                e.EnclosingSymbolId is not null
                && string.Equals(e.Provider, readProvider, StringComparison.Ordinal)
                && string.Equals(e.Operation, readOperation, StringComparison.Ordinal)
            )
            {
                var key = (e.EnclosingSymbolId, e.ResourceType);
                if (!readsByCell.TryGetValue(key, out var list))
                {
                    list = [];
                    readsByCell[key] = list;
                }
                list.Add(e);
            }
        }

        if (readsByCell.Count == 0)
        {
            return effects; // no reads to pair — nothing to annotate
        }

        var result = new List<DerivedEffect>(effects.Count);
        foreach (var e in effects)
        {
            var isWrite =
                e.EnclosingSymbolId is not null
                && string.Equals(e.Provider, writeProvider, StringComparison.Ordinal)
                && string.Equals(e.Operation, writeOperation, StringComparison.Ordinal);
            if (!isWrite || !readsByCell.TryGetValue((e.EnclosingSymbolId!, e.ResourceType), out var priorReads))
            {
                result.Add(e);
                continue;
            }

            // The earliest read strictly before this write is the pairing partner — naming the earliest
            // check is the most useful site to surface (the window opens there). A read on the SAME line as
            // the write (e.g. `x = x + 1` compiled to one site) is not a "read THEN write" ordering, so we
            // require strict line precedence.
            DerivedEffect? pairedRead = null;
            foreach (var read in priorReads)
            {
                if (read.Line < e.Line && (pairedRead is null || read.Line < pairedRead.Line))
                {
                    pairedRead = read;
                }
            }

            if (pairedRead is null)
            {
                result.Add(e);
                continue;
            }

            // Tier by isolation — DISCLOSE, never suppress. Both legs bracketed by a transaction => the
            // lower-confidence "verify isolation" tier (a transaction is NOT a guarantee against lost
            // updates under read-committed). Otherwise the high-confidence unbracketed tier.
            var bracketed = CarriesTransactionSpan(pairedRead) && CarriesTransactionSpan(e);
            var observation = new EffectObservationInfo(
                Type: RaceWindowType,
                Context: e.ResourceType,
                Detail: $"{pairedRead.FilePath}:{pairedRead.Line}",
                Confidence: bracketed ? ConfidenceMedium : ConfidenceHigh,
                Basis: Basis,
                Reason: bracketed ? ReasonInTransaction : ReasonNoIsolation
            );

            IReadOnlyList<EffectObservationInfo> observations = e.Observations is null ? [observation] : [.. e.Observations, observation];
            result.Add(e with { Observations = observations });
        }

        return result;
    }

    private static bool CarriesTransactionSpan(DerivedEffect effect) =>
        effect.Observations is { } obs && obs.Any(o => string.Equals(o.Type, TransactionSpanType, StringComparison.Ordinal));
}

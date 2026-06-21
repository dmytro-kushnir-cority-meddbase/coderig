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

    // The lazy-init / do-once SPLIT (calibration: ~179 race_window candidates were structurally real but
    // dominated by lazy singletons `if (_x == null) _x = new X();` and `static bool initialised` guards —
    // a low-severity archetype, not the high-signal "clobber existing shared state" residual). When a
    // read→write pair matches the lazy-init SHAPE (see LooksLikeLazyInit) we emit a DISTINCT lazy_init_race
    // finding at LOW confidence, disclosed as a heuristic; everything else stays race_window at its tx-tier.
    // This is CLASSIFICATION, not suppression — both are observations added to the same mutate effect.
    public const string LazyInitRaceType = "lazy_init_race";
    private const string ReasonLazyInitHeuristic = "lazy_init_heuristic";
    private const string ConfidenceLow = "low";

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

        // Per-(method, cell) WRITE count — a signal in the lazy-init classification (the classic init shape
        // writes the cell exactly once). Built once here so the per-write classification is O(1).
        var writeCountByCell = new Dictionary<(string Enclosing, string Cell), int>();
        foreach (var e in effects)
        {
            if (
                e.EnclosingSymbolId is not null
                && string.Equals(e.Provider, writeProvider, StringComparison.Ordinal)
                && string.Equals(e.Operation, writeOperation, StringComparison.Ordinal)
            )
            {
                var key = (e.EnclosingSymbolId, e.ResourceType);
                writeCountByCell[key] = writeCountByCell.TryGetValue(key, out var n) ? n + 1 : 1;
            }
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

            // CLASSIFY the pair. The lazy-init / do-once archetype (lazy singletons, do-once init flags) is a
            // separate, LOW-severity finding (lazy_init_race) — disclosed as a heuristic. Everything else is
            // the higher-signal race_window ("check-then-act on state that already has a value"), tiered by
            // isolation. Both are observations added to the SAME mutate effect — classification, not suppression.
            var singleWrite = !writeCountByCell.TryGetValue((e.EnclosingSymbolId!, e.ResourceType), out var writeCount) || writeCount == 1;
            EffectObservationInfo observation;
            if (LooksLikeLazyInit(enclosingId: e.EnclosingSymbolId!, cell: e.ResourceType, singleWrite: singleWrite))
            {
                observation = new EffectObservationInfo(
                    Type: LazyInitRaceType,
                    Context: e.ResourceType,
                    Detail: $"{pairedRead.FilePath}:{pairedRead.Line}",
                    Confidence: ConfidenceLow,
                    Basis: Basis,
                    Reason: ReasonLazyInitHeuristic
                );
            }
            else
            {
                // Tier by isolation — DISCLOSE, never suppress. Both legs bracketed by a transaction => the
                // lower-confidence "verify isolation" tier (a transaction is NOT a guarantee against lost
                // updates under read-committed). Otherwise the high-confidence unbracketed tier.
                var bracketed = CarriesTransactionSpan(pairedRead) && CarriesTransactionSpan(e);
                observation = new EffectObservationInfo(
                    Type: RaceWindowType,
                    Context: e.ResourceType,
                    Detail: $"{pairedRead.FilePath}:{pairedRead.Line}",
                    Confidence: bracketed ? ConfidenceMedium : ConfidenceHigh,
                    Basis: Basis,
                    Reason: bracketed ? ReasonInTransaction : ReasonNoIsolation
                );
            }

            IReadOnlyList<EffectObservationInfo> observations = e.Observations is null ? [observation] : [.. e.Observations, observation];
            result.Add(e with { Observations = observations });
        }

        return result;
    }

    private static bool CarriesTransactionSpan(DerivedEffect effect) =>
        effect.Observations is { } obs && obs.Any(o => string.Equals(o.Type, TransactionSpanType, StringComparison.Ordinal));

    // DISCLOSED STRUCTURAL HEURISTIC for the lazy-init / do-once archetype. We do NOT have the if-condition
    // value (no new extraction), so we classify on shape only and flag it heuristic. A pair is lazy-init when:
    //
    //   (1) the do-once SHAPE holds — the cell is written EXACTLY ONCE in this method (`_x = new X();` /
    //       `init = true;` fired at most once is the signature of a first-time init; a cell written more than
    //       once is being driven, not initialised), AND
    //   (2) a CONTEXT signal corroborates it — the enclosing method is a property getter (`get_*`), an
    //       init-shaped method (name contains Init/Initialise/Initialize), or a constructor (`.#ctor`/`.cctor`),
    //       OR the cell's simple name looks init-ish (`instance`/`initialised`/`initialized`).
    //
    // Shape alone (1) is necessary but not sufficient — `if (Cache.Status == 0) Cache.Status = 1;` also writes
    // once yet is a real mutate-existing race. The context signal (2) is what separates first-time lazy init
    // from check-then-act on already-valued state, so a plain counter/status mutate stays a race_window. We do
    // NOT gate on read count: the classic getter (`if (_x == null) … return _x;`) reads the cell more than
    // once, so a single-read requirement would miss it. The cell-name arm is a WEAK corroborator only
    // (brittle), never the sole signal — it must still co-occur with the single-write shape.
    private static bool LooksLikeLazyInit(string enclosingId, string cell, bool singleWrite)
    {
        if (!singleWrite)
        {
            return false; // written more than once — being driven, not initialised. Leave it a race_window.
        }

        return EnclosingLooksInitLike(enclosingId) || CellNameLooksInitLike(cell);
    }

    // The enclosing method's CONTEXT signal: a property getter, an init-named method, or a constructor. Parses
    // the simple method name out of the DocID (`M:Ns.Type.Member(...)` — strip the `M:`, the param list, and
    // the namespace/type qualifier). Roslyn DocIDs spell ctors `.#ctor` / `.#cctor`.
    private static bool EnclosingLooksInitLike(string enclosingId)
    {
        var name = SimpleMemberName(enclosingId);
        return name.StartsWith("get_", StringComparison.Ordinal)
            || name.Equals("#ctor", StringComparison.Ordinal)
            || name.Equals("#cctor", StringComparison.Ordinal)
            || name.Contains("Init", StringComparison.OrdinalIgnoreCase);
    }

    // WEAK corroborator: the written cell's simple name reads init-ish. Only ever consulted alongside the
    // single-read/single-write shape (never alone), since name heuristics are brittle.
    private static bool CellNameLooksInitLike(string cell)
    {
        var name = SimpleMemberName(cell).TrimStart('_');
        return name.Equals("instance", StringComparison.OrdinalIgnoreCase)
            || name.Equals("initialised", StringComparison.OrdinalIgnoreCase)
            || name.Equals("initialized", StringComparison.OrdinalIgnoreCase);
    }

    // The trailing member name from a DocID: strip the `K:` kind prefix and any `(params)` suffix, then take
    // the segment after the last `.`. (`M:App.Cache.get_Thing` -> `get_Thing`; `F:App.S._instance` -> `_instance`.)
    private static string SimpleMemberName(string docId)
    {
        var s = docId;
        if (s.Length > 2 && s[1] == ':')
        {
            s = s[2..];
        }

        var paren = s.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            s = s[..paren];
        }

        var dot = s.LastIndexOf('.');
        return dot >= 0 ? s[(dot + 1)..] : s;
    }
}

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

    // The [ThreadStatic] REROUTE. A read→write on a `[ThreadStatic]` cell is NOT a cross-thread race — each
    // thread owns its own copy, so the read/write can never interleave across threads (suppressing the
    // race_window/lazy_init_race FALSE POSITIVE that thread-confined lazy-init/RMW shapes would otherwise
    // produce). But it IS the canonical FR-2 context-propagation surface: a `[ThreadStatic]` value is
    // silently lost when an async continuation resumes on a DIFFERENT thread (the production-reverted
    // !10208 ThreadStatic→AsyncLocal migration — transaction context, perf loggers — is exactly this).
    // So we RECLASSIFY (not suppress) to a disclosed thread_local_context candidate. Confidence is LOW: we
    // cannot prove the read actually crosses an await/thread boundary (that needs flow modeling, FR-2
    // tier-3) — the value is corpus-grounding, not a per-path proof. The set of [ThreadStatic] cell DocIDs
    // is supplied by the caller (derived from the existing `[ThreadStatic]`-attribute ctor references —
    // Reads.LoadThreadStaticFieldIdsAsync); empty/null disables the reroute (legacy callers unchanged).
    public const string ThreadLocalContextType = "thread_local_context";
    private const string ReasonThreadStaticContext = "thread_static_state_may_be_lost_across_async_boundary";

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
        // Cell DocIDs (static field/auto-property slots) carrying [ThreadStatic]. A read→write on one of
        // these is rerouted from race_window/lazy_init_race to thread_local_context (see ThreadLocalContextType).
        // Null/empty = no reroute (every pair classifies as race_window/lazy_init_race, the pre-reroute behavior).
        IReadOnlySet<string>? threadStaticCells = null,
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

            // STATIC CONSTRUCTOR EXEMPTION: the CLR type-init lock serializes #cctor execution — only one
            // thread ever runs a given #cctor, so a read→write pair inside a static constructor cannot race.
            // Emit NO observation; add the effect unchanged and move on.
            // NOTE: instance .#ctor, getters, and Init-named methods are NOT exempted — they classify normally.
            if (SimpleMemberName(e.EnclosingSymbolId!) == "#cctor")
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
            if (threadStaticCells is not null && threadStaticCells.Contains(e.ResourceType))
            {
                // THREAD-CONFINED cell — not a cross-thread race (suppress the race_window/lazy_init_race FP);
                // reroute to the disclosed FR-2 context-propagation candidate. See ThreadLocalContextType.
                observation = new EffectObservationInfo(
                    Type: ThreadLocalContextType,
                    Context: e.ResourceType,
                    Detail: $"{pairedRead.FilePath}:{pairedRead.Line}",
                    Confidence: ConfidenceLow,
                    Basis: Basis,
                    Reason: ReasonThreadStaticContext
                );
            }
            else if (LooksLikeLazyInit(enclosingId: e.EnclosingSymbolId!, cell: e.ResourceType, singleWrite: singleWrite))
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

    // ---------------------------------------------------------------------------------------------------------
    // FR-8 `dual_write` — a DISTRIBUTED-CONSISTENCY hazard: one enclosing method performs durable WRITES to
    // ≥2 DIFFERENT system classes (DB + queue, DB + search index, DB + cache, DB + external HTTP, …) in a
    // single unit of work. If the second write fails after the first commits, the systems diverge with no
    // atomicity; the classic mitigation is an outbox/inbox/CDC. Without one it is a dual_write candidate.
    //
    // Like DeriveRaceWindows this is a CODED matcher over the flat effect list, and ANNOTATE-ONLY: it appends
    // a dual_write observation to ONE representative write effect of the qualifying method and returns the list
    // otherwise untouched — it suppresses nothing and changes no effect. What is DATA is only the SYSTEM-CLASS
    // MAP (which write `provider:operation` belongs to which durable system class) — defaulted to
    // DefaultSystemClassMap and overridable by the caller, so the grouping can be retargeted without touching
    // the pairing logic.
    //
    // v1 is INTRA-METHOD (consistent with race_window): the ≥2 distinct systems must be written in ONE
    // enclosing method, so the co-occurrence is an exact, cheap proof. The widening — cross-method / per-EP
    // dual-write (a write in a repo and a publish in a called service, joined over the call graph + a
    // happens-before relation) — is deliberately NOT built here; it needs the reach closure the bounded
    // queries don't carry. v1 ALSO does not check for the presence of an outbox/CDC mitigation (a tier-2
    // ABSENCE refinement — "suppress when an outbox is present"); it flags the co-occurrence and DISCLOSES
    // via the Reason (dual_write_no_outbox_checked) that no outbox was looked for.
    public const string DualWriteType = "dual_write";
    private const string ReasonDualWriteNoOutbox = "dual_write_no_outbox_checked";

    // The DEFAULT system-class map: each durable WRITE/MUTATION `provider:operation` mapped to the SYSTEM
    // CLASS it commits to. DATA (a grouping table), not pairing logic — the pairing (≥2 distinct classes in
    // one method) is the code below. Only writes/mutations are listed (NEVER reads): a read of two systems is
    // not a dual write. Keyed `provider:operation`; a provider with no operation-specific entry is matched by
    // a bare `provider` fallback. Covers the providers that exist in the shipped builtin-rules.json PLUS the
    // MedDBase-local overlay providers (llblgen / entity_cache / echo_publish / eventbus / queue) — entries
    // that never match any effect are harmless.
    public static readonly IReadOnlyDictionary<string, string> DefaultSystemClassMap = new Dictionary<string, string>(
        StringComparer.Ordinal
    )
    {
        // db — relational durable store
        ["db_command:execute"] = "db",
        ["efcore:commit"] = "db",
        ["efcore:pending_write"] = "db",
        ["efcore:raw_sql"] = "db",
        ["repository:write"] = "db",
        ["yessql:write"] = "db",
        ["yessql:delete"] = "db",
        ["llblgen:write"] = "db",
        ["llblgen:delete"] = "db",
        ["llblgen:tx_commit"] = "db",
        // queue — async messaging / event publish / actor mailbox
        ["rabbitmq:publish"] = "queue",
        ["mediatr:publish"] = "queue",
        ["actor:tell"] = "queue",
        ["actor:ask"] = "queue",
        ["echo_publish"] = "queue",
        ["eventbus"] = "queue",
        ["queue"] = "queue",
        // search — search index
        ["elasticsearch:write"] = "search",
        ["elasticsearch:delete"] = "search",
        ["elasticsearch:index_write"] = "search",
        ["elasticsearch:index_delete"] = "search",
        ["azure_search:write"] = "search",
        ["azure_search:delete"] = "search",
        ["azure_search:index_write"] = "search",
        ["azure_search:index_delete"] = "search",
        // cache — distributed / in-proc cache
        ["redis:write"] = "cache",
        ["redis:delete"] = "cache",
        ["inproc_cache:write"] = "cache",
        ["inproc_cache:delete"] = "cache",
        ["entity_cache:write"] = "cache",
        // http — external HTTP mutation (NOT GET)
        ["http:POST"] = "http",
        ["http:PUT"] = "http",
        ["http:PATCH"] = "http",
        ["http:DELETE"] = "http",
        ["soap:invoke"] = "http",
        ["soap:submit"] = "http",
        // blob — blob storage
        ["aws_s3:write"] = "blob",
        ["aws_s3:delete"] = "blob",
        ["azure_blob:write"] = "blob",
        ["azure_blob:delete"] = "blob",
        // object_store — keyed object store
        ["object_store:write"] = "object_store",
        // email — outbound mail
        ["smtp:send"] = "email",
    };

    // Returns `effects` with a dual_write observation appended to ONE representative write effect of every
    // enclosing method whose durable writes span ≥2 DISTINCT system classes (per `systemClassMap`). The
    // representative is the LAST write by line (the latest commit point — the most useful anchor). Context is
    // the sorted, '+'-joined system set (e.g. "db+queue"); Detail is the comma-joined write SITES; Confidence
    // is medium; Reason is dual_write_no_outbox_checked. The input list is not mutated; effects without a new
    // finding are returned by reference. A null enclosing id is skipped (not a call-graph node, never pairs).
    public static IReadOnlyList<DerivedEffect> DeriveDualWrites(
        IReadOnlyList<DerivedEffect> effects,
        IReadOnlyDictionary<string, string>? systemClassMap = null
    )
    {
        var map = systemClassMap ?? DefaultSystemClassMap;

        // Per enclosing method, collect the write effects with their system class — preserving list order so
        // the "last by line" representative is found in one pass. Methods touching <2 distinct classes drop out.
        var writesByMethod = new Dictionary<string, List<(DerivedEffect Effect, string SystemClass)>>(StringComparer.Ordinal);
        foreach (var e in effects)
        {
            if (e.EnclosingSymbolId is null)
            {
                continue;
            }

            if (!TryClassify(map, provider: e.Provider, operation: e.Operation, out var systemClass))
            {
                continue;
            }

            if (!writesByMethod.TryGetValue(e.EnclosingSymbolId, out var list))
            {
                list = [];
                writesByMethod[e.EnclosingSymbolId] = list;
            }
            list.Add((e, systemClass));
        }

        // The qualifying methods: ≥2 DISTINCT system classes among their durable writes. Map each to its
        // chosen representative effect (last write by line) so the annotation is a single O(1) lookup below.
        var findingByRepresentative = new Dictionary<DerivedEffect, EffectObservationInfo>();
        foreach (var (_, writes) in writesByMethod)
        {
            var distinctClasses = writes.Select(w => w.SystemClass).Distinct(StringComparer.Ordinal).ToList();
            if (distinctClasses.Count < 2)
            {
                continue; // single system (or single class with many ops) — not a dual write
            }

            var representative = writes[0].Effect;
            foreach (var w in writes)
            {
                if (w.Effect.Line > representative.Line)
                {
                    representative = w.Effect;
                }
            }

            distinctClasses.Sort(StringComparer.Ordinal);
            var systems = string.Join('+', distinctClasses);
            var sites = string.Join(',', writes.OrderBy(w => w.Effect.Line).Select(w => $"{w.Effect.FilePath}:{w.Effect.Line}"));
            findingByRepresentative[representative] = new EffectObservationInfo(
                Type: DualWriteType,
                Context: systems,
                Detail: sites,
                Confidence: ConfidenceMedium,
                Basis: Basis,
                Reason: ReasonDualWriteNoOutbox
            );
        }

        if (findingByRepresentative.Count == 0)
        {
            return effects;
        }

        var result = new List<DerivedEffect>(effects.Count);
        foreach (var e in effects)
        {
            if (findingByRepresentative.TryGetValue(e, out var observation))
            {
                IReadOnlyList<EffectObservationInfo> observations = e.Observations is null
                    ? [observation]
                    : [.. e.Observations, observation];
                result.Add(e with { Observations = observations });
            }
            else
            {
                result.Add(e);
            }
        }

        return result;
    }

    // Classify a write effect to its durable system class: try the precise `provider:operation` key first,
    // then a bare `provider` fallback (for providers whose every operation is a write, e.g. eventbus/queue).
    // Returns false for reads and any effect not in the map — those are not durable writes for dual-write.
    private static bool TryClassify(IReadOnlyDictionary<string, string> map, string provider, string operation, out string systemClass) =>
        map.TryGetValue($"{provider}:{operation}", out systemClass!) || map.TryGetValue(provider, out systemClass!);

    // DISCLOSED STRUCTURAL HEURISTIC for the lazy-init / do-once archetype. We do NOT have the if-condition
    // value (no new extraction), so we classify on shape only and flag it heuristic. Two arms:
    //
    //   (A) a strong CONTEXT signal — the enclosing method is a property getter (`get_*`), an init-shaped
    //       method (name contains Init/Initialise/Initialize), or a constructor (`.#ctor`/`.cctor`). This is
    //       the do-once context, and it does NOT require a single write: a lazy singleton getter routinely
    //       assigns the cell on SEVERAL mutually-exclusive branches inside `if (cell == null)` — null-
    //       fallbacks, if/else picks — yet is still initialised at most once at runtime. (Calibration:
    //       `PerformanceLogger.Factory` writes `instance` on 4 branches; the old single-write veto left all 4
    //       as high race_window noise — the dominant high-tier FP. The getter/ctor CONTEXT is a stronger
    //       do-once signal than the write count.) OR
    //   (B) the WEAK cell-name signal — the cell's simple name looks init-ish (`instance`/`initialised`/
    //       `initialized`). A name heuristic is brittle, so this arm STILL requires the single-write do-once
    //       SHAPE: a multi-write cell named "instance" outside an init context is being driven, not initialised.
    //
    // Everything else stays race_window: `if (Cache.Status == 0) Cache.Status = 1;` in a plain method is a real
    // mutate-existing check-then-act (no init context, non-init cell name). We do NOT gate on READ count: the
    // classic getter (`if (_x == null) … return _x;`) reads the cell more than once.
    //
    // Trade (disclosed): a getter that conditionally mutates ALREADY-VALUED shared state also lands here as
    // lazy_init_race(low) rather than race_window — still emitted, just down-ranked. Getter shared-state
    // mutation is overwhelmingly lazy/caching, so this is the precision-over-recall call for the default surface.
    private static bool LooksLikeLazyInit(string enclosingId, string cell, bool singleWrite)
    {
        // (A) strong init context — multi-branch init is still do-once, so write count does NOT disqualify.
        if (EnclosingLooksInitLike(enclosingId))
        {
            return true;
        }

        // (B) weak cell-name corroborator — only with the single-write do-once shape.
        return singleWrite && CellNameLooksInitLike(cell);
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

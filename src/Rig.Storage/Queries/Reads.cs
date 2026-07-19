using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Storage;

namespace Rig.Storage.Queries;

public static class Reads
{
    // Returns null when the DB doesn't exist or has no runs yet.
    // DI registrations as a run-agnostic fact: read across all runs and dedupe by
    // (service, implementation, file, line). Returns null only when the store is unreachable.
    public static async Task<IReadOnlyList<DiRegistrationInfo>?> LoadDiRegistrationsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            return null;
        }

        var rows = await context
            .DiRegistrations.Select(x => new DiRegistrationInfo(
                ServiceType: x.ServiceType,
                ImplementationType: x.ImplementationType,
                Lifetime: x.Lifetime,
                RegistrationKind: x.RegistrationKind,
                FilePath: x.FilePath,
                Line: x.Line,
                Confidence: x.Confidence,
                Basis: x.Basis,
                Reason: x.Reason,
                Evidence: x.Evidence
            ))
            .ToListAsync(cancellationToken);

        return rows.GroupBy(d => (d.ServiceType, d.ImplementationType, d.FilePath, d.Line))
            .Select(g => g.First())
            .OrderBy(d => d.ServiceType, StringComparer.Ordinal)
            .ThenBy(d => d.ImplementationType, StringComparer.Ordinal)
            .ToList();
    }

    // Skipped source files, run-agnostic: deduped by file path across all runs.
    public static async Task<IReadOnlyList<SourceFileInfo>?> LoadSkippedSourceFilesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            return null;
        }

        var rows = await context
            .SourceFiles.Where(x => x.Status == "skipped")
            .Select(x => new SourceFileInfo(
                ProjectName: x.ProjectName,
                FilePath: x.FilePath,
                Status: x.Status,
                Confidence: x.Confidence,
                Basis: x.Basis,
                Reason: x.Reason,
                Evidence: x.Evidence
            ))
            .ToListAsync(cancellationToken);

        return rows.GroupBy(f => f.FilePath, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(f => f.FilePath, StringComparer.Ordinal)
            .ToList();
    }

    public static async Task<IReadOnlyList<RunSummary>> ListRunsAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            return [];
        }

        return await context
            .Runs.OrderByDescending(run => run.CreatedAtUtcText)
            .ThenByDescending(run => run.Id)
            .Select(run => new RunSummary(
                Id: run.Id,
                CreatedAtUtc: DateTimeOffset.Parse(run.CreatedAtUtcText, CultureInfo.InvariantCulture),
                SolutionPath: run.SolutionPath,
                SymbolCount: run.SymbolCount,
                ReferenceCount: run.ReferenceCount,
                DiRegistrationCount: run.DiRegistrationCount,
                ProjectIdentity: run.ProjectIdentity,
                SourceProjectPath: run.SourceProjectPath,
                SourceCommit: run.SourceCommit,
                SourceBranch: run.SourceBranch,
                SourceDirty: run.SourceDirty
            ))
            .ToListAsync(cancellationToken);
    }

    // --- Stage-3 fact queries: cross-project (all runs), DocID-keyed. No latest-run concept. ---

    // Substring symbol search. Uses the trigram FTS index (symbol_fts, built by `rig graph`) when it
    // exists and the pattern is >=3 chars — an index-accelerated MATCH with the SAME mid-token,
    // case-insensitive substring semantics as the LIKE it replaces. Falls back to the EF LIKE scan for
    // old stores (no FTS) or short patterns (trigram needs >=3 chars).
    public static async Task<IReadOnlyList<SymbolSearchHit>> SearchSymbolsAsync(
        RigDbContext context,
        string pattern,
        string? kind,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        // symbol_fts is built with the graph (GraphMaterializer); GraphAvailableAsync is the single
        // graph-presence check (replaces the per-table probe). The LIKE fallback below stays for the
        // graph-absent case (a --no-graph store still searches) and for short (<3-char) patterns.
        if (pattern.Length >= 3 && await SchemaGate.GraphAvailableAsync(connection, cancellationToken))
        {
            var hits = new List<SymbolSearchHit>();
            await using var command = connection.CreateCommand();
            // symbol_fts is pre-deduped by SymbolId, so LIMIT applies directly. MATCH searches both
            // indexed columns (symbolid, name) = the two columns the LIKE OR'd. kind is UNINDEXED but
            // still filterable. ORDER BY symbolid mirrors the old OrderBy(SymbolId).
            command.CommandText =
                "SELECT symbolid, kind, signature, filepath, line, assembly FROM symbol_fts "
                + "WHERE symbol_fts MATCH $q"
                + (kind is null ? "" : " AND kind = $kind")
                + " ORDER BY symbolid LIMIT $lim;";
            AddParam(command, "$q", FtsPhrase(pattern));
            if (kind is not null)
            {
                AddParam(command, "$kind", kind);
            }

            AddParam(command, "$lim", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(
                    new SymbolSearchHit(
                        SymbolId: reader.GetString(0),
                        Kind: reader.GetString(1),
                        Signature: reader.IsDBNull(2) ? "" : reader.GetString(2),
                        FilePath: reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Line: ReadInt(reader, 4),
                        DefiningAssembly: reader.IsDBNull(5) ? "" : reader.GetString(5)
                    )
                );
            }

            return hits;
        }

        var like = $"%{pattern}%";
        // AsNoTracking is CRITICAL here (NOT a no-op like the projecting reads): this LIKE fallback
        // materializes raw SymbolFact ENTITIES, so without it EF snapshots all 5000 rows into the change
        // tracker — pure overhead on a read-only query, and it grows the working set for nothing.
        var query = context
            .SymbolFacts.AsNoTracking()
            .Where(s =>
                EF.Functions.Like(matchExpression: s.Name, pattern: like) || EF.Functions.Like(matchExpression: s.SymbolId, pattern: like)
            );
        if (kind is not null)
        {
            query = query.Where(s => s.Kind == kind);
        }

        // Dedupe by SymbolId across runs (multi-target siblings / re-indexed projects) on the CLIENT: the
        // dup ratio is small (~2% — see docs/query-strategy.md) and this is over a bounded Take(5000), so a
        // server-side GROUP BY would add a sort without meaningfully cutting rows; one HashSet pass is cheaper.
        var rows = await query.OrderBy(s => s.SymbolId).Take(5000).ToListAsync(cancellationToken);
        return rows.GroupBy(s => s.SymbolId, StringComparer.Ordinal)
            .Take(limit)
            .Select(g => g.First())
            .Select(s => new SymbolSearchHit(
                SymbolId: s.SymbolId,
                Kind: s.Kind,
                Signature: s.Signature,
                FilePath: s.FilePath,
                Line: s.Line,
                DefiningAssembly: s.DefiningAssembly
            ))
            .ToList();
    }

    // Substring reference search. When ref_target_fts exists and the pattern is >=3 chars, a trigram
    // MATCH resolves the substring to exact target ids, and reference_facts is fetched via its
    // TargetSymbolId index (the IN-subquery) — replacing the full-table LIKE scan while keeping the
    // firstParty/refKind filters and ordering. Falls back to the EF LIKE scan otherwise.
    public static async Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(
        RigDbContext context,
        string pattern,
        bool firstPartyOnly,
        string? refKind,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        // ref_target_fts is built with the graph; GraphAvailableAsync is the single graph-presence check.
        // The LIKE fallback below stays for the graph-absent (--no-graph) case and short patterns.
        if (pattern.Length >= 3 && await SchemaGate.GraphAvailableAsync(connection, cancellationToken))
        {
            var hits = new List<ReferenceHit>();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT r.TargetSymbolId, r.RefKind, r.EnclosingSymbolId, r.FilePath, r.Line, r.TargetInSource "
                + "FROM reference_facts r "
                + "WHERE r.TargetSymbolId IN (SELECT symbolid FROM ref_target_fts WHERE ref_target_fts MATCH $q)"
                + (firstPartyOnly ? " AND r.TargetInSource = 1" : "")
                + (refKind is null ? "" : " AND r.RefKind = $kind")
                + " ORDER BY r.TargetSymbolId, r.FilePath, r.Line LIMIT $lim;";
            AddParam(command, "$q", FtsPhrase(pattern));
            if (refKind is not null)
            {
                AddParam(command, "$kind", refKind);
            }

            AddParam(command, "$lim", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(
                    new ReferenceHit(
                        TargetSymbolId: reader.GetString(0),
                        RefKind: reader.GetString(1),
                        EnclosingSymbolId: reader.IsDBNull(2) ? null : reader.GetString(2),
                        FilePath: reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Line: ReadInt(reader, 4),
                        TargetInSource: !reader.IsDBNull(5) && reader.GetInt64(5) != 0
                    )
                );
            }

            return hits;
        }

        var like = $"%{pattern}%";
        // AsNoTracking is CRITICAL: this fallback materializes raw ReferenceFact ENTITIES (no projection),
        // so without it EF change-tracks every one of the `limit` rows — needless work on a read path.
        var query = context.ReferenceFacts.AsNoTracking().Where(r => EF.Functions.Like(matchExpression: r.TargetSymbolId, pattern: like));
        if (firstPartyOnly)
        {
            query = query.Where(r => r.TargetInSource);
        }

        if (refKind is not null)
        {
            query = query.Where(r => r.RefKind == refKind);
        }

        var rows = await query
            .OrderBy(r => r.TargetSymbolId)
            .ThenBy(r => r.FilePath)
            .ThenBy(r => r.Line)
            .Take(limit)
            .Select(r => new ReferenceHit(
                TargetSymbolId: r.TargetSymbolId,
                RefKind: r.RefKind,
                EnclosingSymbolId: r.EnclosingSymbolId,
                FilePath: r.FilePath,
                Line: r.Line,
                TargetInSource: r.TargetInSource
            ))
            .ToListAsync(cancellationToken);
        return rows;
    }

    // --- raw-ADO helpers for the FTS search paths (FTS5 isn't expressible in EF LINQ) ---

    // FTS5 query for a literal substring: wrap in double quotes (a string token) and double any embedded
    // quotes, so DocID punctuation (. : ( ) etc.) is treated as content, not FTS query syntax. Trigram
    // then matches the substring's 3-grams.
    private static string FtsPhrase(string pattern) => "\"" + pattern.Replace(oldValue: "\"", newValue: "\"\"") + "\"";

    private static void AddParam(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    // FTS5 stores payload columns as text; coerce the line column back to int defensively.
    private static int ReadInt(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        try
        {
            return reader.GetInt32(ordinal);
        }
        catch (InvalidCastException)
        {
            return int.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
    }

    // Loads the fact-derived call graph for cross-project path finding (stage 2 over facts).
    // No Roslyn, no entry-point anchoring — every method's call edges, across all runs.
    //
    // When `handoffRules` is supplied, dispatcher-consumed method-group edges are reclassified to
    // Kind="handoff" by HandoffClassifier BEFORE the graph is returned — the SINGLE shared place
    // classification happens, so the in-memory oracle (this graph), the SQL materializer (which calls
    // this, then writes call_edges with the classified Kind), and the EF-fallback query paths all agree
    // by construction. Null/empty rules => no classification (raw method-group edges).
    public static async Task<FactGraphData> LoadFactGraphAsync(
        RigDbContext context,
        IReadOnlyList<FactHandoffRule>? handoffRules = null,
        IReadOnlyList<FactRedirectRule>? redirectRules = null,
        CancellationToken cancellationToken = default
    )
    {
        // First-party callees only. The fact store now keeps ALL method-call refs (incl. BCL/runtime)
        // so any effect rule can match them at derive time without a re-mine — but the call GRAPH
        // (reaches/tree/callers/dead) must stay first-party, or it floods with BCL leaves (every
        // .ToString()/.Add()/LINQ call). BCL targets have no source/symbol anyway, so they are leaves
        // that add width, not reach. TargetInSource filters them out of the graph; effects on those
        // calls still surface because the effect deriver keys them to their first-party ENCLOSING
        // method (see LoadInvocationRefsAsync, which intentionally does NOT filter).
        var callRows = await context
            .ReferenceFacts.AsNoTracking()
            .Where(r =>
                r.EnclosingSymbolId != null
                && r.TargetInSource
                && (r.RefKind == RefKinds.Invocation || r.RefKind == RefKinds.MethodGroup || r.RefKind == RefKinds.Ctor)
            )
            .Select(r => new CallEdge(
                Caller: r.EnclosingSymbolId!,
                Callee: r.TargetSymbolId,
                Kind: r.RefKind,
                FilePath: r.FilePath,
                Line: r.Line,
                LoopKind: r.EnclosingLoopKind,
                LoopDetail: r.EnclosingLoopDetail,
                ReceiverType: r.ReceiverType,
                HandoffDispatcher: null,
                TypeArguments: r.TypeArguments,
                DelegateConsumer: r.DelegateConsumer,
                DeclaringTypeArgBinding: r.DeclaringTypeArgBinding,
                MethodTypeArgBinding: r.MethodTypeArgBinding,
                DeliveryPrecision: null,
                NonVirtual: r.NonVirtual,
                EnclosingGuards: r.EnclosingGuards
            ))
            .ToListAsync(cancellationToken);

        var callEdges = callRows.Distinct().ToList();

        // Redirect (external-virtual-override-orphan fix, docs/backlog.md): a call binding to an EXTERNAL
        // convenience overload (TargetInSource=0, so dropped by the WHERE above) is rewritten to the virtual
        // hatch it trampolines into INSIDE the external DLL and KEPT, so receiver-narrowed dispatch resolves
        // it to the first-party override. RedirectClassifier can't translate to SQL, so fetch the (few)
        // external rows each rule targets — by its stripped-method prefix — and rewrite client-side. Mirrors
        // FactGraphProjection.FromAnalysis's in-memory redirect so the two projections stay field-identical.
        foreach (var rule in redirectRules ?? [])
        {
            var openParen = rule.Method + "(";
            var redirectRows = await context
                .ReferenceFacts.AsNoTracking()
                .Where(r =>
                    r.EnclosingSymbolId != null
                    && !r.TargetInSource
                    && (r.RefKind == RefKinds.Invocation || r.RefKind == RefKinds.MethodGroup || r.RefKind == RefKinds.Ctor)
                    && r.TargetSymbolId != rule.RedirectTo
                    && (r.TargetSymbolId == rule.Method || r.TargetSymbolId.StartsWith(openParen))
                )
                .Select(r => new CallEdge(
                    Caller: r.EnclosingSymbolId!,
                    Callee: rule.RedirectTo,
                    Kind: r.RefKind,
                    FilePath: r.FilePath,
                    Line: r.Line,
                    LoopKind: r.EnclosingLoopKind,
                    LoopDetail: r.EnclosingLoopDetail,
                    ReceiverType: r.ReceiverType,
                    HandoffDispatcher: null,
                    TypeArguments: r.TypeArguments,
                    DelegateConsumer: r.DelegateConsumer,
                    DeclaringTypeArgBinding: r.DeclaringTypeArgBinding,
                    MethodTypeArgBinding: r.MethodTypeArgBinding,
                    DeliveryPrecision: null,
                    NonVirtual: r.NonVirtual,
                    EnclosingGuards: r.EnclosingGuards
                ))
                .ToListAsync(cancellationToken);
            callEdges.AddRange(redirectRows);
        }
        callEdges = callEdges.Distinct().ToList();

        // Project straight to the domain record in the SELECT (EF builds it in the materializer) — no
        // anonymous intermediate, no second client mapping pass. (These are PROJECTIONS, so AsNoTracking is
        // a no-op — EF tracks nothing but entities — kept only as an explicit read-only signal.)
        // Dedup stays CLIENT-side deliberately: measured dup ratios are tiny (~2% methods, ≤10% on these
        // small relation tables — see docs/query-strategy.md), so a SQL DISTINCT would sort the full result
        // without cutting the rows marshalled; a one-pass HashSet/GroupBy over the fetched records is
        // cheaper and exactly equivalent. Marshalling volume — not dedup — is the cost here.
        var implEdges = (
            await context
                .TypeRelationFacts.AsNoTracking()
                .Where(t => t.RelationKind == RelationKinds.Interface)
                .Select(t => new ImplementsEdge(ImplType: t.TypeSymbolId, InterfaceType: t.RelatedSymbolId))
                .ToListAsync(cancellationToken)
        )
            .Distinct()
            .ToList();

        var baseEdges = (
            await context
                .TypeRelationFacts.AsNoTracking()
                .Where(t => t.RelationKind == RelationKinds.Base)
                .Select(t => new BaseEdge(SubType: t.TypeSymbolId, BaseType: t.RelatedSymbolId))
                .ToListAsync(cancellationToken)
        )
            .Distinct()
            .ToList();

        var methodRows = await context
            .SymbolFacts.AsNoTracking()
            .Where(s => s.Kind == SymbolKinds.Method)
            .Select(s => new MethodRef(
                SymbolId: s.SymbolId,
                Name: s.Name,
                ContainingTypeId: s.ContainingSymbolId,
                IsOverride: s.IsOverride,
                FilePath: s.FilePath,
                Line: s.Line
            ))
            .ToListAsync(cancellationToken);

        var methods = methodRows.GroupBy(m => m.SymbolId, StringComparer.Ordinal).Select(g => g.First()).ToList();

        var classifiedEdges = HandoffClassifier.Classify(callEdges, handoffRules);
        // #2: hand the (now-open) EF connection to the dispatch-facts loader so it neither re-resolves it nor
        // re-applies the read pragmas — the single sqlite_master existence probe is the only irreducible cost.
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        var minedDispatch = await LoadDispatchFactsAsync(context, connection, cancellationToken);
        return FactDelegateFieldJoin.Apply(new FactGraphData(classifiedEdges, implEdges, methods, baseEdges, minedDispatch));
    }

    // The type-param-name source for static monomorphization (Phase 4, docs/design-dispatch-precision.md):
    // an `id -> Signature` map over every METHOD and TYPE symbol. MethodRef/TypeSymbol on the graph carry no
    // Signature, so ShapeGraph mines the ordered type-param names from these signatures (via
    // GenericSubstitution.ParseTypeParameterNames) at materialize time. Dedupe on SymbolId first-wins,
    // mirroring the method dedupe in LoadFactGraphAsync; a null Signature is stored as "".
    public static async Task<IReadOnlyDictionary<string, string>> LoadSymbolSignaturesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await context
            .SymbolFacts.AsNoTracking()
            .Where(s => s.Kind == SymbolKinds.Method || s.Kind == SymbolKinds.Type)
            .Select(s => new { s.SymbolId, s.Signature })
            .ToListAsync(cancellationToken);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            map.TryAdd(row.SymbolId, row.Signature ?? "");
        }

        return map;
    }

    // Static monomorphization is UNCONDITIONAL (went live 2026-06-25 — the toggle was removed after the A/B
    // calibration + an independent adversarial soundness check passed). The in-memory ShapeGraph materializes
    // reachable generic instantiations into ~mono nodes so type-parameter dispatch narrows to the concrete
    // override instead of CHA-fanning; it fires on BOTH load paths (the SQL fast path re-attaches the
    // reference_facts type-arg bindings onto the bounded edges). MEASURED sound on the real store:
    // DebtorOverride.SaveIncludedServices 7861 -> 175 reachable methods — the narrowed virtuals
    // (CommonEntityBase.Delete's change-log hooks, overridden by 32 entities) pin to a LEAF entity that
    // overrides none of them, so the dropped entity families (Person/Invoice/Company...) are genuinely
    // No-path; non-generic targets are byte-unchanged. Base-virtual fans (the irreducible CHA residual, see
    // `rig dispatch-fans`) are intentionally NOT collapsed. This loads the symbol-signature map (the
    // type-param-name source) the loader passes into ShapeGraph's `monomorphizeSignatures`. To A/B-calibrate
    // OFF there is no longer a runtime toggle — make this return null in a temporary local edit.
    public static async Task<IReadOnlyDictionary<string, string>?> LoadMonomorphizationSignaturesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    ) => await LoadSymbolSignaturesAsync(context, cancellationToken);

    // The FULLY in-memory-shaped graph: handoff-classified load → ShapeGraph (factory rewrite +
    // cut/context metadata) → MarkEventSubscriptionHandoffs → AddDeliveryEdges. The SINGLE entry point
    // for all in-memory consumers that need the complete shaped graph, so classify→factory→delivery→
    // cut/context is defined once and callers stop hand-rolling the sequence.
    //
    // Sync reach is BYTE-IDENTICAL to the pre-consolidation per-consumer results: delivery edges are
    // handoff edges, which are sync-cut by default (walked only under --async). The --async path for
    // impact and cycle detection now also walks delivery edges — the intentional gap closure.
    public static async Task<FactGraphData> LoadShapedGraphAsync(RigDbContext context, RuleSet rules, CancellationToken ct = default)
    {
        var graph = await LoadFactGraphAsync(
            context: context,
            handoffRules: rules.Handoff,
            redirectRules: rules.Redirect,
            cancellationToken: ct
        );
        var monoSigs = await LoadMonomorphizationSignaturesAsync(context, ct);
        graph = FactPathFinder.ShapeGraph(
            graph: graph,
            factoryRules: rules.Factory,
            cutRules: rules.Cut,
            contextRules: rules.Context,
            monomorphizeSignatures: monoSigs
        );
        // ORDER IS LOAD-BEARING: AddDeliveryEdges resolves an event's handlers by joining event-read sites to
        // co-located `methodGroup` subscription edges (`someEvent += H`), so it MUST run while those edges are
        // still methodGroup. MarkEventSubscriptionHandoffs reclassifies exactly those subscription edges to
        // `handoff` — run it AFTER, or AddDeliveryEdges finds zero handlers and event delivery (event_raise)
        // edges vanish (event_cycle drops to 0).
        graph = FactPathFinder.AddDeliveryEdges(
            graph: graph,
            sites: await LoadDeliverySitesAsync(context: context, deliveryRules: rules.Delivery, cancellationToken: ct)
        );
        return FactPathFinder.MarkEventSubscriptionHandoffs(
            graph: graph,
            eventSites: await EventSubscriptionSitesAsync(context: context, cancellationToken: ct)
        );
    }

    // Call SITES (EnclosingSymbolId, FilePath, Line) that contain an EVENT read — a "read" ref whose
    // target is an event (DocID "E:" prefix). A `someEvent += Handler` records both the event read and
    // the handler method-group at one site, so intersecting these sites with method-group edges
    // identifies event subscriptions (FactPathFinder.MarkEventSubscriptionHandoffs). Cheap: events are
    // few. Used at query time so the handler subtree is treated as a deferred handoff, not a sync call.
    // EventSubscriptionSite lives in Rig.Domain (the shaping consumer is a Domain function).
    public static async Task<ISet<EventSubscriptionSite>> EventSubscriptionSitesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await context
            .ReferenceFacts.Where(r => r.EnclosingSymbolId != null && r.RefKind == RefKinds.Read && r.TargetSymbolId.StartsWith("E:"))
            .Select(r => new EventSubscriptionSite(Caller: r.EnclosingSymbolId!, FilePath: r.FilePath, Line: r.Line))
            .ToListAsync(cancellationToken);

        return rows.ToHashSet();
    }

    // The SINGLE rule-driven loader of publish→consumer DELIVERY sites — the uniform input to the framework-
    // BLIND join (FactPathFinder.AddDeliveryEdges, baked into call_edges at graph build). Replaces the former
    // per-framework LoadEventDeliverySitesAsync + LoadActorDeliverySitesAsync pair: a codebase declares each
    // mechanism in DATA (the `deliveryRules` rule section), and this loader is generic over it. The actor case
    // is no longer inferred from the `actor:*` effect rules; both events and actors are pure rule data, each
    // composing the two identity primitives ("symbol" / "path") this loader implements.
    //
    // It scans each fact source ONCE regardless of how many rules use it:
    //   - event-symbol rules (Producer.Source == "event-symbol"): event-read refs (RefKind=read, target "E:").
    //     The event's `E:` DocID is the channel IdentityToken — an EXACT binding. Every `someEvent += H` AND
    //     every raise (`someEvent?.Invoke()`) reads the event, so the role is ByColocation; the join decides
    //     subscription (a co-located method-group ⇒ that handler) vs raise (none ⇒ producer). One site per read
    //     per such rule (normally one event rule).
    //   - arg rules (Producer.Source == "arg"): invocation refs whose (declaringType, method) match a rule's
    //     Registration or Producer endpoint Methods×DeclaringTypes. The channel identity is the argument the
    //     rule's `resolve` selects, both GATED to a member path (Contains('.'): a bare-variable name like
    //     `tell(pid, …)` is not a stable cross-method identity and collides spuriously, so it is skipped):
    //     "path" keeps the full member path; "leaf" keeps the LAST segment (bridging parallel registries that
    //     share a leaf but differ by class prefix — e.g. tell `ProcessDns.X` ↔ spawn `ProcessNames.X`). An
    //     invocation matching a Registration endpoint gets Role=Registration; a Producer endpoint, Role=
    //     Producer. The process-name string identity is ~heuristic (more so for leaf).
    //
    // ArgumentIndex > 0 is not yet supported by the facts (only FirstArgumentName/arg0 is captured), so a
    // "path" rule with ArgumentIndex != 0 falls back to arg0 — see the in-memory pass below.
    public static async Task<IReadOnlyList<DeliverySite>> LoadDeliverySitesAsync(
        RigDbContext context,
        IReadOnlyList<DeliveryRule> deliveryRules,
        CancellationToken cancellationToken = default
    )
    {
        var sites = new List<DeliverySite>();

        // --- event-symbol rules: one event-read scan, regardless of rule count (normally one event rule). ---
        var eventRules = deliveryRules
            .Where(rule => string.Equals(rule.Producer.Source, "event-symbol", StringComparison.Ordinal))
            .ToList();
        if (eventRules.Count > 0)
        {
            var eventReads = await context
                .ReferenceFacts.Where(r => r.EnclosingSymbolId != null && r.RefKind == RefKinds.Read && r.TargetSymbolId.StartsWith("E:"))
                .Select(r => new
                {
                    r.EnclosingSymbolId,
                    r.FilePath,
                    r.Line,
                    r.TargetSymbolId,
                })
                .ToListAsync(cancellationToken);

            foreach (var rule in eventRules)
            {
                foreach (var r in eventReads)
                {
                    sites.Add(
                        new DeliverySite(
                            Caller: r.EnclosingSymbolId!,
                            FilePath: r.FilePath,
                            Line: r.Line,
                            IdentityToken: r.TargetSymbolId,
                            Tag: rule.Tag,
                            Role: DeliveryRole.ByColocation
                        )
                    );
                }
            }
        }

        // --- arg rules: combined (declaringType, method) -> (Tag, Role) map across ALL arg rules, so one
        //     invocation-ref scan serves every actor-shaped mechanism. The Registration endpoint's
        //     Methods×DeclaringTypes map to Role=Registration; the Producer endpoint's to Role=Producer. A
        //     method appearing under both (none today) resolves to whichever rule is listed last. ---
        var argMethods =
            new Dictionary<(string Type, string Name), (string Tag, DeliveryRole Role, string Resolve, string? HandlerDispatcher)>();
        foreach (var rule in deliveryRules)
        {
            AddArgEndpoint(argMethods, rule.Tag, rule.Registration, DeliveryRole.Registration);
            AddArgEndpoint(argMethods, rule.Tag, rule.Producer, DeliveryRole.Producer);
        }

        if (argMethods.Count > 0)
        {
            // Coarse SQL filter: actor-shaped calls are invocations with a captured first-argument name (the
            // process name) inside a method, whose target is a method DocID. The in-memory pass refines by the
            // declaring-type+method gate — such calls are few, so the unrefined set is small.
            var rows = await context
                .ReferenceFacts.Where(r =>
                    r.EnclosingSymbolId != null
                    && r.FirstArgumentName != null
                    && r.RefKind == RefKinds.Invocation
                    && r.TargetSymbolId.StartsWith("M:")
                )
                .Select(r => new
                {
                    r.EnclosingSymbolId,
                    r.FilePath,
                    r.Line,
                    r.FirstArgumentName,
                    r.TargetSymbolId,
                })
                .ToListAsync(cancellationToken);

            foreach (var r in rows)
            {
                var parsed = ParseInvocationTarget(r.TargetSymbolId);
                if (parsed is not { } method || !argMethods.TryGetValue(method, out var tagRole))
                {
                    continue;
                }

                // Both arg resolvers GATE to a member path (contains a '.', e.g. `ProcessDns.AccountService`):
                // a bare-variable name (`tell(pid, …)`) is not a stable cross-method identity and collides
                // spuriously with framework internals, so it never becomes a delivery site.
                if (!r.FirstArgumentName!.Contains('.', StringComparison.Ordinal))
                {
                    continue;
                }

                // `path` keeps the full member path (`ProcessDns.AccountService`); `leaf` takes the LAST segment
                // (`AccountService`) — the bridge across PARALLEL registries that share a leaf but differ by
                // class prefix (e.g. a tell through `ProcessDns.X` and the spawn through `ProcessNames.X` name
                // the same process X). `leaf` is more ~heuristic — a leaf shared by two unrelated channels
                // over-joins — so it is opt-in per rule (the resolve field), calibrated, and disclosed.
                var token = string.Equals(tagRole.Resolve, "leaf", StringComparison.Ordinal)
                    ? r.FirstArgumentName![(r.FirstArgumentName!.LastIndexOf('.') + 1)..]
                    : r.FirstArgumentName!;

                sites.Add(
                    new DeliverySite(
                        Caller: r.EnclosingSymbolId!,
                        FilePath: r.FilePath,
                        Line: r.Line,
                        IdentityToken: token,
                        Tag: tagRole.Tag,
                        Role: tagRole.Role,
                        HandlerDispatcher: tagRole.HandlerDispatcher
                    )
                );
            }
        }

        return sites;
    }

    // Folds one arg-source endpoint's Methods×DeclaringTypes into the combined (type, method) -> (Tag, Role)
    // map. Non-arg endpoints (e.g. event-symbol) are skipped here — they are handled by the event scan.
    // NOTE on ArgumentIndex: only FirstArgumentName/arg0 is captured as a fact today, so the `path` resolver
    // always reads arg0; an endpoint declaring ArgumentIndex != 0 is treated as arg0 (no crash) — an
    // extraction limitation to lift when nth-argument names are captured.
    private static void AddArgEndpoint(
        Dictionary<(string Type, string Name), (string Tag, DeliveryRole Role, string Resolve, string? HandlerDispatcher)> map,
        string tag,
        DeliveryEndpoint endpoint,
        DeliveryRole role
    )
    {
        if (!string.Equals(endpoint.Source, "arg", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var declaringType in endpoint.DeclaringTypes ?? [])
        {
            foreach (var name in endpoint.Methods ?? [])
            {
                map[(declaringType, name)] = (tag, role, endpoint.Resolve, endpoint.HandlerDispatcher);
            }
        }
    }

    // "M:Echo.Process.tell``1(Echo.ProcessId,…)" -> ("Echo.Process", "tell"). A generic "(declaringType,
    // method) from an M: DocID" parser. Mirrors FactEffectDeriver's ParseMethod (declaring type's arity
    // markers stripped is unnecessary for the actor types, which are non-generic, so we keep the declaring
    // type verbatim and only trim the method-level "``N"). Null when the DocID is not a method id or has no
    // dot before the member name.
    private static (string DeclaringType, string Name)? ParseInvocationTarget(string docId)
    {
        if (!docId.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        var searchEnd = docId.IndexOf('(');
        if (searchEnd < 0)
        {
            searchEnd = docId.Length;
        }

        var lastDot = docId.LastIndexOf('.', searchEnd - 1);
        if (lastDot < 2)
        {
            return null;
        }

        var declaring = docId.Substring(startIndex: 2, length: lastDot - 2);
        var methodStart = lastDot + 1;
        var backtick = docId.IndexOf('`', startIndex: methodStart, count: searchEnd - methodStart);
        var methodEnd = backtick >= 0 ? backtick : searchEnd;
        if (methodEnd <= methodStart)
        {
            return null;
        }

        return (declaring, docId.Substring(startIndex: methodStart, length: methodEnd - methodStart));
    }

    // Loads the exact Roslyn-mined dispatch facts (dispatch_facts) into FactGraphData.MinedDispatch.
    // Probed (not assumed): a store indexed before dispatch facts existed has no table — return null
    // so FactPathFinder degrades to the pre-mining name/arity CHA (flagged heuristic) instead of
    // throwing "no such table" on a read-only connection that can't migrate.
    private static async Task<IReadOnlyList<DispatchFact>?> LoadDispatchFactsAsync(
        RigDbContext context,
        DbConnection? connection,
        CancellationToken cancellationToken
    )
    {
        // Reuse the caller's already-open connection when supplied (the LoadFactGraphAsync hot path); otherwise
        // open it here. dispatch_facts is a FACT table guaranteed by the open-time index gate (part of the v1
        // fact schema), so no per-table probe — an old store fails fast at open. The return stays nullable for
        // the caller's contract, but this path no longer returns null for a missing table.
        connection ??= await StorageProbes.OpenConnectionAsync(context, cancellationToken);

        // Direct DispatchFact projection (no anonymous intermediate / second pass; AsNoTracking is a no-op
        // on a projection, kept as intent). dispatch_facts has a higher dup ratio (~25%), but it's a small
        // table (~28k rows), so the dedup cost is negligible either way — kept CLIENT-side for consistency
        // with the other reads rather than fragmenting into a server-side DISTINCT here.
        return (
            await context
                .DispatchFacts.AsNoTracking()
                .Select(d => new DispatchFact(SourceMember: d.SourceMember, TargetMember: d.TargetMember, Kind: d.Kind))
                .ToListAsync(cancellationToken)
        )
            .Distinct()
            .ToList();
    }

    // Loads first-party method metadata for the dead-code finder: every declared method symbol with
    // the accessibility/abstract/virtual modifiers, file/line, override flag, and a generated-file
    // heuristic. SymbolFacts are source-declared (first-party) by construction, so this is exactly the
    // universe the unreachable-symbol finder ranges over. Deduped by SymbolId.
    public static async Task<IReadOnlyList<DeadCodeFinder.MethodMeta>> LoadDeadCodeMethodsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await context
            .SymbolFacts.Where(s => s.Kind == SymbolKinds.Method)
            .Select(s => new
            {
                s.SymbolId,
                s.Name,
                s.Modifiers,
                s.FilePath,
                s.Line,
                s.IsOverride,
            })
            .ToListAsync(cancellationToken);

        return rows.GroupBy(s => s.SymbolId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(s => new DeadCodeFinder.MethodMeta(
                SymbolId: s.SymbolId,
                Name: s.Name,
                Modifiers: s.Modifiers,
                FilePath: s.FilePath,
                Line: s.Line,
                IsOverride: s.IsOverride,
                IsGenerated: IsGeneratedPath(s.FilePath)
            ))
            .ToList();
    }

    // SymbolId -> the declaration's END line, for the method symbols only. Used by `rig impact` to overlap
    // each changed method's source extent [Line, EndLine] against a git diff's changed line ranges. Read
    // via raw ADO and GUARDED by ColumnExists: the EndLine column was added after the original schema, so a
    // store indexed by an older rig won't have it — there we return an EMPTY map and `impact` degrades to
    // its file-granular gate (correct, just less precise). Method symbols only, deduped by SymbolId (the
    // same universe LoadDeadCodeMethodsAsync ranges over).
    public static async Task<IReadOnlyDictionary<string, int>> LoadMethodEndLinesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        var endLines = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SymbolId, EndLine FROM symbol_facts WHERE Kind = $kind;";
        var p = command.CreateParameter();
        p.ParameterName = "$kind";
        p.Value = SymbolKinds.Method;
        command.Parameters.Add(p);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var end = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            // First write wins, mirroring LoadDeadCodeMethodsAsync's GroupBy(SymbolId).First().
            endLines.TryAdd(id, end);
        }

        return endLines;
    }

    // SymbolId -> the symbol's declaration BODY HASH (SymbolFact.BodyHash). Used by `rig impact` to detect an
    // IN-PLACE body edit — a reachable method whose text changed but whose call structure (and thus the
    // reachable-set diff) did not. Read via raw ADO and GUARDED by ColumnExists: the BodyHash column was added
    // after the original schema, so a store indexed by an older rig won't have it — there we return an EMPTY
    // map and `impact` silently skips the in-place signal (the structural diff still works). Mirrors
    // LoadMethodEndLinesAsync's backward-compat pattern. ALL symbol kinds (not just methods) so reachable
    // lambdas/accessors are covered; rows with an empty hash (no body) are skipped. First write wins.
    public static async Task<IReadOnlyDictionary<string, string>> LoadSymbolBodyHashesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SymbolId, BodyHash FROM symbol_facts;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(1))
            {
                continue;
            }

            var hash = reader.GetString(1);
            if (hash.Length > 0)
            {
                hashes.TryAdd(reader.GetString(0), hash);
            }
        }

        return hashes;
    }

    // Heuristic: a file is generated when it carries the conventional generated-source markers or the
    // synthetic source-generator path the loader assigns. Such members are reached via the generator /
    // build, not first-party calls, so the dead-code finder must not flag them.
    private static bool IsGeneratedPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var p = filePath.Replace(oldChar: '\\', newChar: '/');
        return p.Contains("<generated>", StringComparison.Ordinal)
            || p.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }

    // Derives handoff (delegate / method-group) entry points from facts — a category the structural
    // entry-point rules miss. First-party targets only (TargetInSource). No re-index. When
    // `handoffRules` are supplied, each handoff is CLASSIFIED (Dispatcher + Kind set for
    // dispatcher-consumed delegates; null for the unclassified residual) by running the same
    // HandoffClassifier the graph layer uses — so the listing here and the cut in traversal agree.
    // Returns classified handoffs first, then the residual; capped at `limit`.
    public static async Task<IReadOnlyList<HandoffEntryPoint>> DeriveHandoffEntryPointsAsync(
        RigDbContext context,
        int limit,
        IReadOnlyList<FactHandoffRule>? handoffRules = null,
        CancellationToken cancellationToken = default,
        FactGraphData? graph = null
    )
    {
        var rules = handoffRules ?? [];
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);

        // Fast path: `rig graph` already classified every edge and persisted Kind + HandoffDispatcher into
        // call_edges, and the handoff-EP classifier consumes ONLY the handoff + methodGroup edges (~5k of
        // 533k). Read those directly (index-backed by IX_call_edges_Kind) instead of LoadFactGraphAsync,
        // which re-scans ~539k reference_facts and EF-marshals every edge just to discard all but those few
        // (~3.7s). call_edges.Kind is the SAME classification the rest of the SQL query path already trusts,
        // so this is equivalence-preserving; the classifier still attaches kind/requires from the passed
        // rules by HandoffDispatcher id.
        if (await SchemaGate.GraphAvailableAsync(connection, cancellationToken))
        {
            var edges = new List<CallEdge>();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT FromSym, ToSym, Kind, FilePath, Line, HandoffDispatcher FROM call_edges WHERE Kind IN ('handoff', 'methodGroup');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                edges.Add(
                    new CallEdge(
                        Caller: reader.GetString(0),
                        Callee: reader.GetString(1),
                        Kind: reader.GetString(2),
                        FilePath: reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Line: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                        HandoffDispatcher: reader.IsDBNull(5) ? null : reader.GetString(5)
                    )
                );
            }

            return HandoffClassifier.HandoffEntryPoints(edges, rules).Take(limit).ToList();
        }

        // Fallback: no materialized graph (`rig graph` not run) — derive from the full reference graph.
        // When the caller already holds the shaped graph (e.g. DeriveCommand which built it for FR-10),
        // reuse it here instead of reloading (fixes F1 double-load).
        var fallbackEdges = (graph ?? await LoadFactGraphAsync(context, rules, cancellationToken: cancellationToken)).CallEdges;
        return HandoffClassifier.HandoffEntryPoints(fallbackEdges, rules).Take(limit).ToList();
    }

    // Loads the facts needed by FactEntryPointDeriver: base-type edges, constructor+type symbols,
    // and ctor reference_facts (attribute applications).  No Roslyn, no latest-run concept —
    // queries are cross-run (all facts in the DB); deduplication happens in the deriver.
    public static async Task<FactEntryPointDeriver.FactEntryPointData> LoadFactEntryPointDataAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var baseEdgeRows = await context
            .TypeRelationFacts.Where(t => t.RelationKind == RelationKinds.Base)
            .Select(t => new { t.TypeSymbolId, t.RelatedSymbolId })
            .ToListAsync(cancellationToken);
        var baseEdges = baseEdgeRows.Select(t => (t.TypeSymbolId, t.RelatedSymbolId)).Distinct().ToList();

        var interfaceEdgeRows = await context
            .TypeRelationFacts.Where(t => t.RelationKind == RelationKinds.Interface)
            .Select(t => new { t.TypeSymbolId, t.RelatedSymbolId })
            .ToListAsync(cancellationToken);
        var interfaceEdges = interfaceEdgeRows.Select(t => (t.TypeSymbolId, t.RelatedSymbolId)).Distinct().ToList();

        // All methods (not just .ctor): page EPs use the .ctor rows, class-inheritance EPs use the
        // named handler rows. IsOverride feeds RequireOverride rules (e.g. WorkflowControllerBase.OnSave).
        // Projected straight to the MethodSymbol record server-side (every field is a direct column).
        var methodRows = await context
            .SymbolFacts.Where(s => s.Kind == SymbolKinds.Method)
            .Select(s => new MethodSymbol(
                SymbolId: s.SymbolId,
                Name: s.Name,
                ContainingSymbolId: s.ContainingSymbolId,
                Signature: s.Signature,
                FilePath: s.FilePath,
                Line: s.Line,
                IsOverride: s.IsOverride
            ))
            .ToListAsync(cancellationToken);
        // The three dedups below (methods, types, ctorRefs) run CLIENT-side by design. Measured dup ratios
        // are tiny — methods by (file,line) is ~0.02% (43 of 217k rows, essentially defensive), types ~9%,
        // ctorRefs ~3.6% (see docs/query-strategy.md). A server-side GROUP BY would scan + sort the full
        // table without cutting the rows marshalled (the real cost), so it would be slower, not faster; the
        // single client pass is the deliberate choice. The deriver also re-dedups by (file,line) downstream.
        var methods = methodRows.GroupBy(m => (m.FilePath, m.Line)).Select(g => g.First()).ToList();

        // Types can't materialize fully server-side: IsAbstract is Modifiers.Split(' ').Contains("abstract"),
        // and String.Split has no SQL translation. So project the raw Modifiers column, then build the
        // TypeSymbol record client-side (after the dedup), where the Split runs in memory.
        var typeRows = await context
            .SymbolFacts.Where(s => s.Kind == SymbolKinds.Type)
            .Select(s => new
            {
                s.SymbolId,
                s.Namespace,
                s.FilePath,
                s.Line,
                s.Modifiers,
            })
            .ToListAsync(cancellationToken);
        var types = typeRows
            .GroupBy(t => t.SymbolId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(t => new TypeSymbol(
                SymbolId: t.SymbolId,
                Namespace: t.Namespace,
                FilePath: t.FilePath,
                Line: t.Line,
                IsAbstract: t.Modifiers.Split(' ').Contains("abstract")
            ))
            .ToList();

        // ctor refs with RefKind="ctor" capture attribute applications (e.g. [ClientAction])
        // as well as regular constructor calls.  The deriver filters by Target prefix. Projected straight
        // to the SymbolRef record server-side; dedup by (file,line) stays client-side (tiny dup ratio).
        var ctorRefs = (
            await context
                .ReferenceFacts.Where(r => r.RefKind == RefKinds.Ctor && r.EnclosingSymbolId != null)
                .Select(r => new SymbolRef(Target: r.TargetSymbolId, Enclosing: r.EnclosingSymbolId, FilePath: r.FilePath, Line: r.Line))
                .ToListAsync(cancellationToken)
        )
            .GroupBy(r => (r.FilePath, r.Line))
            .Select(g => g.First())
            .ToList();

        return new FactEntryPointDeriver.FactEntryPointData(
            BaseEdges: baseEdges,
            Methods: methods,
            Types: types,
            CtorRefs: ctorRefs,
            InterfaceEdges: interfaceEdges
        );
    }

    // Loads invocation reference facts for fact-based effect + observation derivation.
    public static async Task<IReadOnlyList<FactInvocation>> LoadInvocationRefsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await context
            .ReferenceFacts.Where(r => r.RefKind == RefKinds.Invocation)
            .Select(r => new FactInvocation(
                Target: r.TargetSymbolId,
                Enclosing: r.EnclosingSymbolId,
                FilePath: r.FilePath,
                Line: r.Line,
                Receiver: r.ReceiverType,
                FirstArgTemplate: r.FirstArgumentTemplate,
                FirstArgType: r.FirstArgumentType,
                LoopKind: r.EnclosingLoopKind,
                LoopDetail: r.EnclosingLoopDetail,
                EnclosingInvocations: r.EnclosingInvocations,
                CatchTypes: r.EnclosingCatchTypes,
                TypeArguments: r.TypeArguments,
                FirstArgName: r.FirstArgumentName,
                EnclosingScopes: r.EnclosingScopes,
                ArgumentTemplates: r.ArgumentTemplates,
                ArgumentNames: r.ArgumentNames,
                EnclosingGuards: r.EnclosingGuards
            ))
            .ToListAsync(cancellationToken);

        return rows;
    }

    // Compiler-owned allocation facts for the ordinary whole-store effect derivation path.
    public static async Task<IReadOnlyList<AllocationFact>> LoadAllocationFactsAsync(
        RigDbContext context,
        IReadOnlyCollection<string>? enclosingScope = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = context.AllocationFacts.AsNoTracking().AsQueryable();
        if (enclosingScope is not null)
        {
            query = query.Where(a => enclosingScope.Contains(a.EnclosingSymbolId));
        }

        return await query
            .Select(a => new AllocationFact(
                a.Operation,
                a.ResourceType,
                a.EnclosingSymbolId,
                a.FilePath,
                a.Line,
                a.EnclosingLoopKind,
                a.EnclosingLoopDetail,
                a.EnclosingGuards
            ))
            .ToListAsync(cancellationToken);
    }

    // Library call SITES made by the given enclosing methods: invocations whose target is NOT in the
    // indexed source (TargetInSource = 0) — the raw calls that reach OUT to a referenced assembly.
    // `tree --full` renders the ones not already surfaced as effects as dimmed leaves. Chunked over
    // enclosingIds to stay under SQLite's bound-parameter limit on large trees.
    public static async Task<IReadOnlyList<SymbolRef>> LoadLibraryCallSitesAsync(
        RigDbContext context,
        IReadOnlyCollection<string> enclosingIds,
        CancellationToken cancellationToken = default
    )
    {
        var result = new List<SymbolRef>();
        foreach (var chunk in enclosingIds.Chunk(500))
        {
            var rows = await context
                .ReferenceFacts.AsNoTracking()
                .Where(r =>
                    r.RefKind == RefKinds.Invocation
                    && !r.TargetInSource
                    && r.EnclosingSymbolId != null
                    && chunk.Contains(r.EnclosingSymbolId)
                )
                .Select(r => new SymbolRef(
                    Target: r.TargetSymbolId,
                    Enclosing: r.EnclosingSymbolId,
                    FilePath: r.FilePath,
                    Line: r.Line,
                    EnclosingGuards: r.EnclosingGuards
                ))
                .ToListAsync(cancellationToken);
            result.AddRange(rows);
        }
        return result;
    }

    // Loads throw reference facts (RefKind="throw") for fact-based throw-effect derivation. Target is
    // the thrown exception type DocID ("T:Ns.Exception"); the deriver gates it like a declaring type.
    public static async Task<IReadOnlyList<SymbolRef>> LoadThrowRefsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await context
            .ReferenceFacts.Where(r => r.RefKind == RefKinds.Throw && r.EnclosingSymbolId != null)
            .Select(r => new SymbolRef(
                Target: r.TargetSymbolId,
                Enclosing: r.EnclosingSymbolId,
                FilePath: r.FilePath,
                Line: r.Line,
                EnclosingGuards: r.EnclosingGuards
            ))
            .ToListAsync(cancellationToken);

        return rows.GroupBy(r => (r.FilePath, r.Line, r.Target)).Select(g => g.First()).ToList();
    }

    // First-party field/property ACCESS references (RefKind read|write) — the data the call graph omits (it
    // carries method→method call edges only). `rig impact` (Phase 3) unions these targets into each reachable
    // method's reach as degenerate leaf nodes, so a changed field/property access inside a reachable method
    // shows in the per-EP reach-set diff. First-party only (TargetInSource) — a write to a BCL field is not a
    // first-party behavioral surface — and EnclosingSymbolId not null so it can be keyed to its method.
    public static async Task<IReadOnlyList<SymbolRef>> LoadFieldAccessRefsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await context
            .ReferenceFacts.AsNoTracking()
            .Where(r => (r.RefKind == RefKinds.Read || r.RefKind == RefKinds.Write) && r.TargetInSource && r.EnclosingSymbolId != null)
            .Select(r => new SymbolRef(Target: r.TargetSymbolId, Enclosing: r.EnclosingSymbolId, FilePath: r.FilePath, Line: r.Line))
            .ToListAsync(cancellationToken);

        return rows;
    }

    // Loads WRITE reference facts (RefKind="write") whose TARGET is a STATIC field/auto-property, for
    // fact-based shared-state-mutation derivation (FR-1(b)). The field-write fact already exists — the
    // FactExtractor classifies an assignment LHS as RefKinds.Write — but no deriver arm consumed it; this
    // surfaces those whose target is STATIC, which is the property that makes a write a SHARED-state
    // mutation (an instance/local write is local-vs-shared-ambiguous and deliberately excluded). The
    // target's static-ness is the JOIN to symbol_facts.Modifiers — the fact layer's only source of the
    // written slot's modifiers (the call graph carries method->method edges only). First-party only
    // (TargetInSource) and EnclosingSymbolId not null so the effect keys to a call-graph node. Target is
    // the written slot DocID ("F:Ns.Type.field" / "P:Ns.Type.Prop"); the deriver gates its declaring type.
    public static Task<IReadOnlyList<FactFieldAccess>> LoadStaticFieldWriteRefsAsync(
        RigDbContext context,
        IReadOnlyCollection<string>? enclosingScope = null,
        CancellationToken cancellationToken = default
    ) =>
        LoadStaticFieldAccessRefsAsync(
            context: context,
            refKind: RefKinds.Write,
            excludeReadonly: false,
            enclosingScope: enclosingScope,
            cancellationToken: cancellationToken
        );

    // Loads READ reference facts (RefKind="read") whose TARGET is a STATIC field/auto-property — the FR-1
    // read arm, the symmetric twin of LoadStaticFieldWriteRefsAsync. A read of `StaticType.SharedField` is
    // the "check" of a shared cell (the read-before-write TOCTOU/lost-update detector pairs it with the
    // write). Identical join/dedup/structural projection to the write loader — only RefKind differs.
    public static Task<IReadOnlyList<FactFieldAccess>> LoadStaticFieldReadRefsAsync(
        RigDbContext context,
        IReadOnlyCollection<string>? enclosingScope = null,
        CancellationToken cancellationToken = default
    ) =>
        LoadStaticFieldAccessRefsAsync(
            context: context,
            refKind: RefKinds.Read,
            excludeReadonly: true,
            enclosingScope: enclosingScope,
            cancellationToken: cancellationToken
        );

    // Combined loader for BOTH static-field-access arms in ONE query (the derive path needs both, back-to-back).
    // Runs a single scan with (RefKind == write || RefKind == read) and partitions client-side into (Writes,
    // Reads) — eliminating one of the two reference_facts round-trips LoadStaticField{Write,Read}RefsAsync made
    // separately. Semantics are EXACTLY the union of the two single-kind loaders: same static-target gate,
    // TargetInSource, EnclosingSymbolId != null, same structural projection, same per-partition dedup by
    // (FilePath, Line, Target). The one asymmetry the single-kind loaders carry is the `readonly` drop — only
    // the READ arm excludes readonly static targets (an immutable cell can't be a TOCTOU "check"; ~99k logger
    // reads of noise), while the WRITE arm keeps them. So the join keeps BOTH static-and-readonly and
    // static-and-mutable rows (gated on `static`), tags each row with its readonly-ness, and the client-side
    // partition applies the readonly drop to the READ side only — reproducing each loader's row set exactly.
    public static async Task<(
        IReadOnlyList<FactFieldAccess> Writes,
        IReadOnlyList<FactFieldAccess> Reads
    )> LoadStaticFieldAccessRefsByKindAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var rows = await context
            .ReferenceFacts.AsNoTracking()
            .Where(r => (r.RefKind == RefKinds.Write || r.RefKind == RefKinds.Read) && r.TargetInSource && r.EnclosingSymbolId != null)
            .Join(
                context.SymbolFacts.AsNoTracking().Where(s => s.Modifiers.Contains("static")),
                r => r.TargetSymbolId,
                s => s.SymbolId,
                (r, s) =>
                    new
                    {
                        Access = new FactFieldAccess(
                            Target: r.TargetSymbolId,
                            Enclosing: r.EnclosingSymbolId,
                            FilePath: r.FilePath,
                            Line: r.Line,
                            LoopKind: r.EnclosingLoopKind,
                            LoopDetail: r.EnclosingLoopDetail,
                            EnclosingInvocations: r.EnclosingInvocations,
                            CatchTypes: r.EnclosingCatchTypes,
                            EnclosingScopes: r.EnclosingScopes
                        ),
                        r.RefKind,
                        IsReadonly = s.Modifiers.Contains("readonly"),
                    }
            )
            .ToListAsync(cancellationToken);

        // Partition by kind, then dedup each partition by (FilePath, Line, Target) — mirroring each single-kind
        // loader's tail. The READ arm drops readonly targets (the `excludeReadonly: true` the read loader sets);
        // the WRITE arm keeps them.
        var writes = rows.Where(x => string.Equals(x.RefKind, RefKinds.Write, StringComparison.Ordinal))
            .Select(x => x.Access)
            .GroupBy(r => (r.FilePath, r.Line, r.Target))
            .Select(g => g.First())
            .ToList();
        var reads = rows.Where(x => string.Equals(x.RefKind, RefKinds.Read, StringComparison.Ordinal) && !x.IsReadonly)
            .Select(x => x.Access)
            .GroupBy(r => (r.FilePath, r.Line, r.Target))
            .Select(g => g.First())
            .ToList();

        return (writes, reads);
    }

    // Shared loader for both static-field-access arms (read vs write differ only by RefKind). Joins the
    // access ref to symbol_facts on a STATIC target (the fact layer's only source of the target's modifiers),
    // first-party only (TargetInSource), enclosing non-null (keys the effect to a call-graph node), carries
    // the access's structural context (mirrors LoadInvocationRefsAsync), and dedups by site.
    //
    // `excludeReadonly` additionally drops `readonly` static targets — set ONLY on the READ arm. A read of an
    // immutable cell (static readonly / const-folded field, e.g. a logger or a frozen table) can never be the
    // "check" of a TOCTOU read-before-write: the value cannot change underneath, so such reads are pure noise
    // (~99k on the real store, dominated by static readonly loggers). The WRITE arm keeps readonly targets:
    // a write to a readonly static is ctor-only and already rare, and remains a genuine shared-state mutation.
    // `enclosingScope`, when given, bounds the scan to refs whose ENCLOSING is in the set (the EnclosingSymbolId
    // index makes this a seek, not a full scan) — used by `tree --hazards` to load only this one EP's reachable
    // methods' field accesses instead of the whole store (~tens of thousands of rows). Null = whole store
    // (derive/impact, which need every cell).
    private static async Task<IReadOnlyList<FactFieldAccess>> LoadStaticFieldAccessRefsAsync(
        RigDbContext context,
        string refKind,
        bool excludeReadonly,
        IReadOnlyCollection<string>? enclosingScope = null,
        CancellationToken cancellationToken = default
    )
    {
        var refs = context
            .ReferenceFacts.AsNoTracking()
            .Where(r => r.RefKind == refKind && r.TargetInSource && r.EnclosingSymbolId != null);
        if (enclosingScope is not null)
        {
            refs = refs.Where(r => enclosingScope.Contains(r.EnclosingSymbolId!));
        }

        var rows = await refs.Join(
                context
                    .SymbolFacts.AsNoTracking()
                    .Where(s => s.Modifiers.Contains("static") && (!excludeReadonly || !s.Modifiers.Contains("readonly"))),
                r => r.TargetSymbolId,
                s => s.SymbolId,
                (r, s) =>
                    new FactFieldAccess(
                        Target: r.TargetSymbolId,
                        Enclosing: r.EnclosingSymbolId,
                        FilePath: r.FilePath,
                        Line: r.Line,
                        LoopKind: r.EnclosingLoopKind,
                        LoopDetail: r.EnclosingLoopDetail,
                        EnclosingInvocations: r.EnclosingInvocations,
                        CatchTypes: r.EnclosingCatchTypes,
                        EnclosingScopes: r.EnclosingScopes
                    )
            )
            .ToListAsync(cancellationToken);

        return rows.GroupBy(r => (r.FilePath, r.Line, r.Target)).Select(g => g.First()).ToList();
    }

    // The set of STATIC field DocIDs (`F:Ns.Type.field`) declared first-party — the universe the
    // static_init_capture detector joins an effect's EnclosingSymbolId against to decide whether a config
    // read sits inside a STATIC field initializer (frozen at CLR type-init) rather than an instance field
    // (re-runs per construction) or a method/accessor (re-evaluates live). Static-ness is sourced exactly
    // like the static-field-access loaders: symbol_facts.Modifiers (the fact layer's only source of a slot's
    // modifiers), gated on the "static" token. Field symbols (Kind="field"); deduped by SymbolId. A whole-store
    // load (no enclosing-scope bound) — derive needs every static field.
    public static async Task<IReadOnlySet<string>> LoadStaticFieldIdsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var ids = await context
            .SymbolFacts.AsNoTracking()
            .Where(s => s.Kind == SymbolKinds.Field && s.Modifiers.Contains("static"))
            .Select(s => s.SymbolId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    // The set of field/auto-property DocIDs carrying a [ThreadStatic] attribute. No dedicated attribute fact
    // is needed: an attribute application is a constructor invocation (`new ThreadStaticAttribute()`), so it
    // already lands as a `ctor` reference whose ENCLOSING is the decorated field's DocID and whose TARGET is
    // the attribute's ctor — exactly the join below. A [ThreadStatic] cell is THREAD-CONFINED (each thread
    // owns its copy) so it cannot have a cross-thread shared-state race; the hazard layer uses this set to
    // reroute such read→write pairs from race_window to the FR-2 thread_local_context candidate (see
    // FactHazardDeriver.ThreadLocalContextType). Matched on the exact ctor DocID, which is index-seekable on
    // the TargetSymbolId index; the rarer form where the attribute name binds to the type (not the ctor) is
    // not covered — accepted, it is uncommon for [ThreadStatic].
    public static async Task<IReadOnlySet<string>> LoadThreadStaticFieldIdsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        const string threadStaticCtor = "M:System.ThreadStaticAttribute.#ctor";
        var ids = await context
            .ReferenceFacts.AsNoTracking()
            .Where(r => r.RefKind == RefKinds.Ctor && r.TargetSymbolId == threadStaticCtor && r.EnclosingSymbolId != null)
            .Select(r => r.EnclosingSymbolId!)
            .Distinct()
            .ToListAsync(cancellationToken);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    // The set of field DocIDs declared `volatile` (symbol_facts.Modifiers, mined by FactExtractor since
    // 2026-07-02 — a store indexed BEFORE that yields an empty set, which callers treat as "never
    // corroborated"). One of the two signals hard-suppressing a lock-enclosed lazy-init as a safe DCL
    // (FactHazardDeriver): a volatile publish can't hand the lock-free outer read a torn object.
    public static async Task<IReadOnlySet<string>> LoadVolatileFieldIdsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var ids = await context
            .SymbolFacts.AsNoTracking()
            .Where(s => s.Kind == "field" && s.Modifiers.Contains("volatile"))
            .Select(s => s.SymbolId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    // --- assembly-level reference analysis (rig refs --unused / --usage), query-side, no re-index ---

    // DISTINCT (source-file, owning assembly) pairs for every indexed symbol with a known DefiningAssembly —
    // the raw input the csproj->assembly attribution (UnusedReferenceAnalyzer.BuildCsprojToAssembly) folds by
    // owning directory. Raw ADO (a plain aggregate over symbol_facts; no FTS dependency), mirroring the
    // FindReferencesAsync ADO style.
    public static async Task<IReadOnlyList<(string FilePath, string Assembly)>> LoadFileAssembliesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        var rows = new List<(string, string)>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT FilePath, DefiningAssembly FROM symbol_facts WHERE DefiningAssembly <> '' AND FilePath <> '';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    // Observed first-party assembly usage EDGES: (usingAsm, usedAsm, refCount) where a symbol in usingAsm
    // references a first-party (TargetInSource=1) symbol in a DIFFERENT usedAsm. The join keys the reference
    // to its enclosing symbol's assembly. The count is informational (rendered, not used by the diff — the
    // diff only tests edge existence), so multi-run duplication of symbol_facts rows does not affect
    // correctness. GROUP BY the two assemblies.
    public static async Task<IReadOnlyList<(string UsingAsm, string UsedAsm, int Count)>> LoadAssemblyUsageEdgesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        var rows = new List<(string, string, int)>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.DefiningAssembly AS usingAsm, r.TargetAssembly AS usedAsm, COUNT(*) AS refs "
            + "FROM reference_facts r JOIN symbol_facts s ON s.SymbolId = r.EnclosingSymbolId "
            + "WHERE r.TargetInSource = 1 AND r.TargetAssembly <> '' AND s.DefiningAssembly <> '' "
            + "AND s.DefiningAssembly <> r.TargetAssembly "
            + "GROUP BY s.DefiningAssembly, r.TargetAssembly;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return rows;
    }

    // Inbound first-party usage COUNTS per target assembly: (assembly, refs, fromMethods) — total references
    // and the number of DISTINCT enclosing methods that make them. Ascending by refs (least-used first) so
    // `rig refs --usage` surfaces the thinly-referenced assemblies at the top. No join needed.
    public static async Task<IReadOnlyList<(string Assembly, int Refs, int FromMethods)>> LoadAssemblyUsageCountsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        var rows = new List<(string, int, int)>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT TargetAssembly, COUNT(*) AS refs, COUNT(DISTINCT EnclosingSymbolId) AS fromMethods "
            + "FROM reference_facts WHERE TargetInSource = 1 AND TargetAssembly <> '' "
            + "GROUP BY TargetAssembly ORDER BY refs ASC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        }

        return rows;
    }
}

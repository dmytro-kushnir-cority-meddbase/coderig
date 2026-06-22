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
        if (pattern.Length >= 3 && await StorageProbes.TableExistsAsync(connection, "symbol_fts", cancellationToken))
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
        if (pattern.Length >= 3 && await StorageProbes.TableExistsAsync(connection, "ref_target_fts", cancellationToken))
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
                MethodTypeArgBinding: r.MethodTypeArgBinding
            ))
            .ToListAsync(cancellationToken);

        var callEdges = callRows.Distinct().ToList();

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
        var minedDispatch = await LoadDispatchFactsAsync(context, cancellationToken);
        return new FactGraphData(classifiedEdges, implEdges, methods, baseEdges, minedDispatch);
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

    // Loads the exact Roslyn-mined dispatch facts (dispatch_facts) into FactGraphData.MinedDispatch.
    // Probed (not assumed): a store indexed before dispatch facts existed has no table — return null
    // so FactPathFinder degrades to the pre-mining name/arity CHA (flagged heuristic) instead of
    // throwing "no such table" on a read-only connection that can't migrate.
    private static async Task<IReadOnlyList<DispatchFact>?> LoadDispatchFactsAsync(
        RigDbContext context,
        CancellationToken cancellationToken
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        if (!await StorageProbes.TableExistsAsync(connection, "dispatch_facts", cancellationToken))
        {
            return null;
        }

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
        if (
            !await StorageProbes.ColumnExistsAsync(
                connection,
                table: "symbol_facts",
                column: "EndLine",
                cancellationToken: cancellationToken
            )
        )
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

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
        if (
            !await StorageProbes.ColumnExistsAsync(
                connection,
                table: "symbol_facts",
                column: "BodyHash",
                cancellationToken: cancellationToken
            )
        )
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

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
        CancellationToken cancellationToken = default
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
        if (
            await StorageProbes.TableExistsAsync(connection, "call_edges", cancellationToken)
            && await StorageProbes.ColumnExistsAsync(
                connection,
                table: "call_edges",
                column: "HandoffDispatcher",
                cancellationToken: cancellationToken
            )
        )
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
        var graph = await LoadFactGraphAsync(context, rules, cancellationToken);
        return HandoffClassifier.HandoffEntryPoints(graph.CallEdges, rules).Take(limit).ToList();
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
                ArgumentNames: r.ArgumentNames
            ))
            .ToListAsync(cancellationToken);

        return rows;
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
                .Select(r => new SymbolRef(Target: r.TargetSymbolId, Enclosing: r.EnclosingSymbolId, FilePath: r.FilePath, Line: r.Line))
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
            .Select(r => new SymbolRef(Target: r.TargetSymbolId, Enclosing: r.EnclosingSymbolId, FilePath: r.FilePath, Line: r.Line))
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
        CancellationToken cancellationToken = default
    ) =>
        LoadStaticFieldAccessRefsAsync(
            context: context,
            refKind: RefKinds.Write,
            excludeReadonly: false,
            cancellationToken: cancellationToken
        );

    // Loads READ reference facts (RefKind="read") whose TARGET is a STATIC field/auto-property — the FR-1
    // read arm, the symmetric twin of LoadStaticFieldWriteRefsAsync. A read of `StaticType.SharedField` is
    // the "check" of a shared cell (the read-before-write TOCTOU/lost-update detector pairs it with the
    // write). Identical join/dedup/structural projection to the write loader — only RefKind differs.
    public static Task<IReadOnlyList<FactFieldAccess>> LoadStaticFieldReadRefsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    ) =>
        LoadStaticFieldAccessRefsAsync(
            context: context,
            refKind: RefKinds.Read,
            excludeReadonly: true,
            cancellationToken: cancellationToken
        );

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
    private static async Task<IReadOnlyList<FactFieldAccess>> LoadStaticFieldAccessRefsAsync(
        RigDbContext context,
        string refKind,
        bool excludeReadonly,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await context
            .ReferenceFacts.AsNoTracking()
            .Where(r => r.RefKind == refKind && r.TargetInSource && r.EnclosingSymbolId != null)
            .Join(
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
}

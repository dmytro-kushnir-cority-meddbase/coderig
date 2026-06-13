using System.Data.Common;
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
            return null;

        var rows = await context
            .DiRegistrations.Select(x => new DiRegistrationInfo(
                x.ServiceType,
                x.ImplementationType,
                x.Lifetime,
                x.RegistrationKind,
                x.FilePath,
                x.Line,
                x.Confidence,
                x.Basis,
                x.Reason,
                x.Evidence
            ))
            .ToArrayAsync(cancellationToken);

        return rows.GroupBy(d => (d.ServiceType, d.ImplementationType, d.FilePath, d.Line))
            .Select(g => g.First())
            .OrderBy(d => d.ServiceType, StringComparer.Ordinal)
            .ThenBy(d => d.ImplementationType, StringComparer.Ordinal)
            .ToArray();
    }

    // Skipped source files, run-agnostic: deduped by file path across all runs.
    public static async Task<IReadOnlyList<SourceFileInfo>?> LoadSkippedSourceFilesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (!await context.Database.CanConnectAsync(cancellationToken))
            return null;

        var rows = await context
            .SourceFiles.Where(x => x.Status == "skipped")
            .Select(x => new SourceFileInfo(x.ProjectName, x.FilePath, x.Status, x.Confidence, x.Basis, x.Reason, x.Evidence))
            .ToArrayAsync(cancellationToken);

        return rows.GroupBy(f => f.FilePath, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(f => f.FilePath, StringComparer.Ordinal)
            .ToArray();
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
                run.Id,
                DateTimeOffset.Parse(run.CreatedAtUtcText),
                run.SolutionPath,
                run.SymbolCount,
                run.ReferenceCount,
                run.DiRegistrationCount,
                run.ProjectIdentity,
                run.SourceProjectPath
            ))
            .ToArrayAsync(cancellationToken);
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
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);
        if (pattern.Length >= 3 && await StorageProbes.TableExistsAsync(connection, "symbol_fts", cancellationToken).ConfigureAwait(false))
        {
            var hits = new List<SymbolSearchHit>();
            using var command = connection.CreateCommand();
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
                AddParam(command, "$kind", kind);
            AddParam(command, "$lim", limit);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                hits.Add(
                    new SymbolSearchHit(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3),
                        ReadInt(reader, 4),
                        reader.IsDBNull(5) ? "" : reader.GetString(5)
                    )
                );
            return hits;
        }

        var like = $"%{pattern}%";
        var query = context.SymbolFacts.Where(s => EF.Functions.Like(s.Name, like) || EF.Functions.Like(s.SymbolId, like));
        if (kind is not null)
            query = query.Where(s => s.Kind == kind);

        // Dedupe by SymbolId across runs (multi-target siblings / re-indexed projects).
        var rows = await query.OrderBy(s => s.SymbolId).Take(5000).ToArrayAsync(cancellationToken);
        return rows.GroupBy(s => s.SymbolId)
            .Take(limit)
            .Select(g => g.First())
            .Select(s => new SymbolSearchHit(s.SymbolId, s.Kind, s.Signature, s.FilePath, s.Line, s.DefiningAssembly))
            .ToArray();
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
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);
        if (
            pattern.Length >= 3
            && await StorageProbes.TableExistsAsync(connection, "ref_target_fts", cancellationToken).ConfigureAwait(false)
        )
        {
            var hits = new List<ReferenceHit>();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT r.TargetSymbolId, r.RefKind, r.EnclosingSymbolId, r.FilePath, r.Line, r.TargetInSource "
                + "FROM reference_facts r "
                + "WHERE r.TargetSymbolId IN (SELECT symbolid FROM ref_target_fts WHERE ref_target_fts MATCH $q)"
                + (firstPartyOnly ? " AND r.TargetInSource = 1" : "")
                + (refKind is null ? "" : " AND r.RefKind = $kind")
                + " ORDER BY r.TargetSymbolId, r.FilePath, r.Line LIMIT $lim;";
            AddParam(command, "$q", FtsPhrase(pattern));
            if (refKind is not null)
                AddParam(command, "$kind", refKind);
            AddParam(command, "$lim", limit);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                hits.Add(
                    new ReferenceHit(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3),
                        ReadInt(reader, 4),
                        !reader.IsDBNull(5) && reader.GetInt64(5) != 0
                    )
                );
            return hits;
        }

        var like = $"%{pattern}%";
        var query = context.ReferenceFacts.Where(r => EF.Functions.Like(r.TargetSymbolId, like));
        if (firstPartyOnly)
            query = query.Where(r => r.TargetInSource);
        if (refKind is not null)
            query = query.Where(r => r.RefKind == refKind);

        var rows = await query
            .OrderBy(r => r.TargetSymbolId)
            .ThenBy(r => r.FilePath)
            .ThenBy(r => r.Line)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
        return rows.Select(r => new ReferenceHit(r.TargetSymbolId, r.RefKind, r.EnclosingSymbolId, r.FilePath, r.Line, r.TargetInSource))
            .ToArray();
    }

    // --- raw-ADO helpers for the FTS search paths (FTS5 isn't expressible in EF LINQ) ---

    // FTS5 query for a literal substring: wrap in double quotes (a string token) and double any embedded
    // quotes, so DocID punctuation (. : ( ) etc.) is treated as content, not FTS query syntax. Trigram
    // then matches the substring's 3-grams.
    private static string FtsPhrase(string pattern) => "\"" + pattern.Replace("\"", "\"\"") + "\"";

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
            return 0;
        try
        {
            return reader.GetInt32(ordinal);
        }
        catch (InvalidCastException)
        {
            return int.TryParse(reader.GetString(ordinal), out var v) ? v : 0;
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
            .ReferenceFacts.Where(r =>
                r.EnclosingSymbolId != null
                && r.TargetInSource
                && (r.RefKind == RefKinds.Invocation || r.RefKind == RefKinds.MethodGroup || r.RefKind == RefKinds.Ctor)
            )
            .Select(r => new
            {
                r.EnclosingSymbolId,
                r.TargetSymbolId,
                r.RefKind,
                r.FilePath,
                r.Line,
                r.EnclosingLoopKind,
                r.EnclosingLoopDetail,
                r.ReceiverType,
                // Call-site generic type args (e.g. Entity.New<Account,int,AccountRecord>) — they drive the
                // generic-factory rewrite + generic-dispatch narrowing. The bounded SQL path attaches these
                // too; without them here, this fallback silently disabled both (Construct.New CHA-fanned).
                r.TypeArguments,
                // The call/`new` a method-group is handed to (line-free); drives async-handoff
                // classification for multi-line dispatcher registrations. Null on pre-existing stores.
                r.DelegateConsumer,
            })
            .ToArrayAsync(cancellationToken);
        var callEdges = callRows
            .Select(r => new CallEdge(
                r.EnclosingSymbolId!,
                r.TargetSymbolId,
                r.RefKind,
                r.FilePath,
                r.Line,
                r.EnclosingLoopKind,
                r.EnclosingLoopDetail,
                r.ReceiverType,
                TypeArguments: r.TypeArguments,
                DelegateConsumer: r.DelegateConsumer
            ))
            .Distinct()
            .ToArray();

        var implRows = await context
            .TypeRelationFacts.Where(t => t.RelationKind == RelationKinds.Interface)
            .Select(t => new { t.TypeSymbolId, t.RelatedSymbolId })
            .ToArrayAsync(cancellationToken);
        var implEdges = implRows.Select(t => new ImplementsEdge(t.TypeSymbolId, t.RelatedSymbolId)).Distinct().ToArray();

        var baseRows = await context
            .TypeRelationFacts.Where(t => t.RelationKind == RelationKinds.Base)
            .Select(t => new { t.TypeSymbolId, t.RelatedSymbolId })
            .ToArrayAsync(cancellationToken);
        var baseEdges = baseRows.Select(t => new BaseEdge(t.TypeSymbolId, t.RelatedSymbolId)).Distinct().ToArray();

        var methodRows = await context
            .SymbolFacts.Where(s => s.Kind == SymbolKinds.Method)
            .Select(s => new
            {
                s.SymbolId,
                s.Name,
                s.ContainingSymbolId,
                s.IsOverride,
                s.FilePath,
                s.Line,
            })
            .ToArrayAsync(cancellationToken);
        var methods = methodRows
            .GroupBy(m => m.SymbolId)
            .Select(g => g.First())
            .Select(m => new MethodRef(m.SymbolId, m.Name, m.ContainingSymbolId, m.IsOverride, m.FilePath, m.Line))
            .ToArray();

        var classifiedEdges = HandoffClassifier.Classify(callEdges, handoffRules);
        var minedDispatch = await LoadDispatchFactsAsync(context, cancellationToken);
        return new FactGraphData(classifiedEdges, implEdges, methods, baseEdges, minedDispatch);
    }

    // Call SITES (EnclosingSymbolId, FilePath, Line) that contain an EVENT read — a "read" ref whose
    // target is an event (DocID "E:" prefix). A `someEvent += Handler` records both the event read and
    // the handler method-group at one site, so intersecting these sites with method-group edges
    // identifies event subscriptions (FactPathFinder.MarkEventSubscriptionHandoffs). Cheap: events are
    // few. Used at query time so the handler subtree is treated as a deferred handoff, not a sync call.
    public static async Task<ISet<(string Caller, string FilePath, int Line)>> EventSubscriptionSitesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await context
            .ReferenceFacts.Where(r => r.EnclosingSymbolId != null && r.RefKind == RefKinds.Read && r.TargetSymbolId.StartsWith("E:"))
            .Select(r => new
            {
                r.EnclosingSymbolId,
                r.FilePath,
                r.Line,
            })
            .ToArrayAsync(cancellationToken);
        return rows.Select(r => (r.EnclosingSymbolId!, r.FilePath, r.Line)).ToHashSet();
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
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);
        if (!await StorageProbes.TableExistsAsync(connection, "dispatch_facts", cancellationToken).ConfigureAwait(false))
            return null;

        var rows = await context
            .DispatchFacts.Select(d => new
            {
                d.SourceMember,
                d.TargetMember,
                d.Kind,
            })
            .ToArrayAsync(cancellationToken);
        return rows.Select(d => new DispatchFact(d.SourceMember, d.TargetMember, d.Kind)).Distinct().ToArray();
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
            .ToArrayAsync(cancellationToken);
        return rows.GroupBy(s => s.SymbolId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(s => new DeadCodeFinder.MethodMeta(
                s.SymbolId,
                s.Name,
                s.Modifiers,
                s.FilePath,
                s.Line,
                s.IsOverride,
                IsGeneratedPath(s.FilePath)
            ))
            .ToArray();
    }

    // Heuristic: a file is generated when it carries the conventional generated-source markers or the
    // synthetic source-generator path the loader assigns. Such members are reached via the generator /
    // build, not first-party calls, so the dead-code finder must not flag them.
    private static bool IsGeneratedPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;
        var p = filePath.Replace('\\', '/');
        return p.Contains("<generated>")
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
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);

        // Fast path: `rig graph` already classified every edge and persisted Kind + HandoffDispatcher into
        // call_edges, and the handoff-EP classifier consumes ONLY the handoff + methodGroup edges (~5k of
        // 533k). Read those directly (index-backed by IX_call_edges_Kind) instead of LoadFactGraphAsync,
        // which re-scans ~539k reference_facts and EF-marshals every edge just to discard all but those few
        // (~3.7s). call_edges.Kind is the SAME classification the rest of the SQL query path already trusts,
        // so this is equivalence-preserving; the classifier still attaches kind/requires from the passed
        // rules by HandoffDispatcher id.
        if (
            await StorageProbes.TableExistsAsync(connection, "call_edges", cancellationToken).ConfigureAwait(false)
            && await StorageProbes.ColumnExistsAsync(connection, "call_edges", "HandoffDispatcher", cancellationToken).ConfigureAwait(false)
        )
        {
            var edges = new List<CallEdge>();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT FromSym, ToSym, Kind, FilePath, Line, HandoffDispatcher FROM call_edges WHERE Kind IN ('handoff', 'methodGroup');";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
            return HandoffClassifier.HandoffEntryPoints(edges, rules).Take(limit).ToArray();
        }

        // Fallback: no materialized graph (`rig graph` not run) — derive from the full reference graph.
        var graph = await LoadFactGraphAsync(context, rules, cancellationToken).ConfigureAwait(false);
        return HandoffClassifier.HandoffEntryPoints(graph.CallEdges, rules).Take(limit).ToArray();
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
            .ToArrayAsync(cancellationToken);
        var baseEdges = baseEdgeRows.Select(t => (t.TypeSymbolId, t.RelatedSymbolId)).Distinct().ToArray();

        var interfaceEdgeRows = await context
            .TypeRelationFacts.Where(t => t.RelationKind == RelationKinds.Interface)
            .Select(t => new { t.TypeSymbolId, t.RelatedSymbolId })
            .ToArrayAsync(cancellationToken);
        var interfaceEdges = interfaceEdgeRows.Select(t => (t.TypeSymbolId, t.RelatedSymbolId)).Distinct().ToArray();

        // All methods (not just .ctor): page EPs use the .ctor rows, class-inheritance EPs use the
        // named handler rows. IsOverride feeds RequireOverride rules (e.g. WorkflowControllerBase.OnSave).
        var methodRows = await context
            .SymbolFacts.Where(s => s.Kind == SymbolKinds.Method)
            .Select(s => new
            {
                s.SymbolId,
                s.Name,
                s.ContainingSymbolId,
                s.Signature,
                s.FilePath,
                s.Line,
                s.IsOverride,
            })
            .ToArrayAsync(cancellationToken);
        var methods = methodRows
            .GroupBy(m => (m.FilePath, m.Line))
            .Select(g => g.First())
            .Select(m => (m.SymbolId, m.Name, m.ContainingSymbolId, m.Signature, m.FilePath, m.Line, m.IsOverride))
            .ToArray();

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
            .ToArrayAsync(cancellationToken);
        var types = typeRows
            .GroupBy(t => t.SymbolId)
            .Select(g => g.First())
            .Select(t => (t.SymbolId, t.Namespace, t.FilePath, t.Line, IsAbstract: t.Modifiers.Split(' ').Contains("abstract")))
            .ToArray();

        // ctor refs with RefKind="ctor" capture attribute applications (e.g. [ClientAction])
        // as well as regular constructor calls.  The deriver filters by TargetSymbolId prefix.
        var ctorRefRows = await context
            .ReferenceFacts.Where(r => r.RefKind == RefKinds.Ctor && r.EnclosingSymbolId != null)
            .Select(r => new
            {
                r.TargetSymbolId,
                r.EnclosingSymbolId,
                r.FilePath,
                r.Line,
            })
            .ToArrayAsync(cancellationToken);
        var ctorRefs = ctorRefRows
            .GroupBy(r => (r.FilePath, r.Line))
            .Select(g => g.First())
            .Select(r => (r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line))
            .ToArray();

        return new FactEntryPointDeriver.FactEntryPointData(baseEdges, methods, types, ctorRefs!, interfaceEdges);
    }

    // Loads invocation reference facts for fact-based effect + observation derivation.
    public static async Task<IReadOnlyList<FactInvocation>> LoadInvocationRefsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await context
            .ReferenceFacts.Where(r => r.RefKind == RefKinds.Invocation)
            .Select(r => new
            {
                r.TargetSymbolId,
                r.EnclosingSymbolId,
                r.FilePath,
                r.Line,
                r.ReceiverType,
                r.FirstArgumentTemplate,
                r.FirstArgumentType,
                r.EnclosingLoopKind,
                r.EnclosingLoopDetail,
                r.EnclosingInvocations,
                r.EnclosingCatchTypes,
                r.TypeArguments,
                r.FirstArgumentName,
                r.EnclosingScopes,
            })
            .ToArrayAsync(cancellationToken);
        return rows.Select(r => new FactInvocation(
                r.TargetSymbolId,
                r.EnclosingSymbolId,
                r.FilePath,
                r.Line,
                r.ReceiverType,
                r.FirstArgumentTemplate,
                r.FirstArgumentType,
                r.EnclosingLoopKind,
                r.EnclosingLoopDetail,
                r.EnclosingInvocations,
                r.EnclosingCatchTypes,
                r.TypeArguments,
                r.FirstArgumentName,
                r.EnclosingScopes
            ))
            .ToArray();
    }

    // Loads throw reference facts (RefKind="throw") for fact-based throw-effect derivation. Target is
    // the thrown exception type DocID ("T:Ns.Exception"); the deriver gates it like a declaring type.
    public static async Task<IReadOnlyList<(string Target, string? Enclosing, string FilePath, int Line)>> LoadThrowRefsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await context
            .ReferenceFacts.Where(r => r.RefKind == RefKinds.Throw && r.EnclosingSymbolId != null)
            .Select(r => new
            {
                r.TargetSymbolId,
                r.EnclosingSymbolId,
                r.FilePath,
                r.Line,
            })
            .ToArrayAsync(cancellationToken);
        return rows.GroupBy(r => (r.FilePath, r.Line, r.TargetSymbolId))
            .Select(g => g.First())
            .Select(r => (r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line))
            .ToArray();
    }
}

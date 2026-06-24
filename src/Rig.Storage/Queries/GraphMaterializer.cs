using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Storage;
using static System.Globalization.CultureInfo;

namespace Rig.Storage.Queries;

// Builds the DERIVED call-graph views (call_edges + dispatch_edges) that the SQL recursive-CTE
// reachability path traverses. Decoupled from indexing: it reads only facts already in the .rig
// (reference_facts / type_relation_facts / symbol_facts, via Reads.LoadFactGraphAsync) and is fully
// idempotent — rerun it any time (after a dispatch-logic change, a re-mine, or just to rebuild) with
// NO Roslyn and NO rescan. The whole rebuild runs in one transaction, so a crash leaves the previous
// derived tables (or none) intact; the facts are never touched.
//
// Why precompute: the graph queries (reaches/callers/tree/dead) otherwise load the entire 1.4M-row
// reference set into process memory and rebuild the adjacency + synthetic dispatch edges on EVERY
// invocation (~6-7s, RAM ∝ whole graph). Materialising the resolved edges once lets SQLite walk only
// the reachable frontier on disk via the edge index (~tens of ms, RAM ∝ result) — no daemon, no state.
//
// dispatch_edges is built from FactPathFinder.AllDispatchEdges, the SAME interface->impl /
// base->override resolution the in-memory oracle uses, so the SQL traversal sees exactly the edges the
// EF FactPathFinder would compute lazily.
public static class GraphMaterializer
{
    public sealed record GraphStats(int CallEdges, int DispatchEdges, int Nodes = 0, int HeuristicDispatchEdges = 0);

    private const int InsertBatchSize = 20_000;

    public static async Task<GraphStats> BuildAsync(
        RigDbContext context,
        IReadOnlyList<FactHandoffRule>? handoffRules = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<FactGenericFactoryRule>? factoryRules = null,
        IReadOnlyList<DeliveryRule>? deliveryRules = null,
        IReadOnlyList<FactRedirectRule>? redirectRules = null
    )
    {
        progress?.Invoke("Loading facts");
        // Classify dispatcher-consumed method-group edges here (the materializer owns the call_edges
        // table) so the persisted Kind="handoff" + HandoffDispatcher flow to every SQL reader; the
        // in-memory oracle classifies identically by being given the same rules. Redirect rules are applied
        // in LoadFactGraphAsync so the external-virtual-override redirects bake into call_edges too.
        var graph = await Reads.LoadFactGraphAsync(
            context,
            handoffRules: handoffRules,
            redirectRules: redirectRules,
            cancellationToken: cancellationToken
        );
        return await BuildFromGraphAsync(context, graph, factoryRules, progress, cancellationToken, deliveryRules: deliveryRules);
    }

    // Materialize the derived tables from a graph ALREADY built in memory (FactGraphProjection.FromAnalysis at
    // index time), so the graph phase skips re-reading the whole fact store off disk. The graph MUST already be
    // handoff-classified — both FromAnalysis and LoadFactGraphAsync do this — this method only bakes the
    // generic-factory rewrite and persists. `rig index` calls this with the facts it just extracted; the
    // DB-loading BuildAsync overload above wraps it for callers that only have a store (a re-graph).
    //
    // `symbols`/`references` are the in-memory fact arrays from THIS index run. When supplied (the `rig index`
    // path), the FTS search index is fed from them directly — skipping the full-table scans of symbol_facts /
    // reference_facts that the SQL `INSERT … SELECT` would otherwise do (the bulk of the graph phase's disk
    // reads, since the facts were just written and are still in RAM). Null on the re-graph path (BuildAsync
    // only has the store), which falls back to the on-disk SELECT.
    public static async Task<GraphStats> BuildFromGraphAsync(
        RigDbContext context,
        FactGraphData graph,
        IReadOnlyList<FactGenericFactoryRule>? factoryRules = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<SymbolFact>? symbols = null,
        IReadOnlyList<ReferenceFact>? references = null,
        IReadOnlyList<DeliveryRule>? deliveryRules = null
    )
    {
        // Bake generic-factory monomorphization into the persisted edges — the SAME RewriteGenericFactories
        // the in-memory traversal applies via ShapeGraph. This is the EDGE-CREATING shaping (it rewrites
        // `caller -> Factory<X>` to `caller -> X.Target`), so it MUST be in call_edges or the SQL bounding
        // walk traverses the un-rewritten factory edge and never pulls X.Target's closure into the bounded
        // subgraph — under-reporting reach vs the in-memory oracle (the effect-path divergence). Cut and
        // context-dispatch shaping are deliberately NOT baked: they are traversal-time (a cut removes
        // reach; context narrows dispatch), so leaving them out keeps call_edges a sound SUPERSET that the
        // in-memory pass re-applies over the bounded graph. No-op when factoryRules is null/empty.
        graph = FactPathFinder.RewriteGenericFactories(graph, factoryRules ?? []);

        // Publish→consumer DELIVERY edges: a publish (a C# event raise `someEvent?.Invoke` / an Echo
        // `Process.tell(name, msg)`) delivers to the channel's handler(s), an edge no syntactic call records.
        // EDGE-CREATING like the factory rewrite above, so it is baked into call_edges here — otherwise the SQL
        // bounding walk never pulls a handler's closure into a bounded reach (under-reporting --async reach +
        // blinding cycle detection). The event reads / actor calls are already in the store at this point
        // (facts are saved before graph build, on both the index and re-graph paths). Both frameworks feed the
        // ONE framework-blind join (events identity-EXACT on the `E:` symbol; actors ~heuristic on a process-
        // name string — Tag namespaces them so they never cross). Each delivery MECHANISM is DATA (the
        // `deliveryRules` rule section), threaded in like factoryRules; the single rule-driven loader returns
        // BOTH event + actor sites. Modeled as handoff edges → sync-cut by default, walked under --async.
        // No-op when there are no sites.
        var sites = await Reads.LoadDeliverySitesAsync(context, deliveryRules ?? [], cancellationToken);
        graph = FactPathFinder.AddDeliveryEdges(graph, sites);

        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await ApplyGraphPragmasAsync(connection, cancellationToken);

        await EnsureSchemaAsync(connection, cancellationToken);

        progress?.Invoke("Rebuilding derived edge tables");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, "DELETE FROM call_edges;", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM dispatch_edges;", cancellationToken);

        var callCount = await InsertCallEdgesAsync(connection, transaction, graph, progress, cancellationToken);
        var (dispatchCount, heuristicCount) = await InsertDispatchEdgesAsync(connection, transaction, graph, progress, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // `nodes` = the distinct symbol universe the SQL reachability seeds scan: every edge endpoint
        // PLUS every declared method (symbol_facts), so it matches FactPathFinder's index.Nodes (which
        // includes edge-less methods). One indexed LIKE scan over this replaces four full edge-column
        // scans for pattern seeding. Built after the edges are committed; a plain SQL pass, no Roslyn.
        progress?.Invoke("Building node index");
        var nodeCount = await BuildNodesAsync(connection, cancellationToken);

        // Trigram FTS indexes for substring search (`rig symbols` / `rig refs`). A leading-wildcard
        // LIKE '%pat%' can't use any B-tree index, so it full-scans symbol_facts / reference_facts; the
        // trigram tokenizer indexes 3-grams, so a MATCH on a >=3-char substring is index-accelerated
        // while preserving the SAME mid-token, case-insensitive substring semantics LIKE had. Queries
        // <3 chars fall back to LIKE. Owned by `rig graph` (the writer); read commands only MATCH them.
        progress?.Invoke("Building search index (FTS5 trigram)");
        await BuildSearchIndexAsync(connection, progress, cancellationToken, symbols, references);

        // Refresh whole-store statistics (sqlite_stat1) now that all derived tables + indexes exist, so the
        // query-time planner picks the right index/join order for whole-store reads (entry-point data,
        // dispatch facts) instead of guessing. One-time cost at graph build; query connections only read.
        progress?.Invoke("Analyzing statistics");
        // analysis_limit caps the rows ANALYZE samples PER INDEX (SQLite 3.32+), so it stops full-scanning
        // the multi-GB fact tables — a large share of the phase's disk reads — while still producing
        // good-enough sqlite_stat1 for the planner (the column distributions it needs are well-estimated
        // from a bounded sample). 0 = unbounded (the old full scan); 400 is SQLite's own recommended cap.
        await ExecuteAsync(connection, null, "PRAGMA analysis_limit=400;", cancellationToken);
        await ExecuteAsync(connection, null, "ANALYZE;", cancellationToken);

        // Stamp the graph stage as current now the graph tables (call_edges/dispatch_edges/nodes/fts) are
        // built. The read-time SchemaGate.GraphAvailableAsync gates every graph read on this version, so a
        // store whose graph this build didn't (re)stamp reads as graph-absent and callers degrade. On a
        // re-graph over a legacy store with no meta row, WriteGraphVersionAsync also stamps the current
        // index version (the graph build proves the store is current-shaped).
        await SchemaMeta.WriteGraphVersionAsync(connection, cancellationToken);

        return new GraphStats(CallEdges: callCount, DispatchEdges: dispatchCount, Nodes: nodeCount, HeuristicDispatchEdges: heuristicCount);
    }

    // Tune THIS connection for the graph rebuild. The phase is both write-heavy (DELETE + bulk-insert
    // ~550k call edges + ~260k nodes) and READ-heavy: the FTS5 trigram builds, the `nodes` UNION, and
    // ANALYZE all full-scan the just-written fact tables, which on SQLite defaults (mmap_size=0, 2 MB
    // cache) means a syscall-per-page cold read — the bulk of the phase's multi-GB disk reads. A big
    // mmap + page cache serves those scans from memory, temp_store=MEMORY keeps the FTS/ANALYZE scratch
    // off disk, and synchronous=OFF drops fsyncs (the rebuild is idempotent — re-run `rig graph`). This
    // is the same read-pragma template the query paths use (StorageProbes), plus synchronous=OFF.
    // journal_mode is deliberately LEFT ON: unlike the save path (which writes a throwaway .tmp then
    // atomically renames), graph mutates the PUBLISHED store in place, so the rollback journal must
    // stay for the one-transaction rebuild to remain crash-safe. Best-effort — a PRAGMA that doesn't
    // take just leaves the default.
    private static async Task ApplyGraphPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "PRAGMA mmap_size=1073741824; PRAGMA cache_size=-262144; PRAGMA temp_store=MEMORY; PRAGMA synchronous=OFF;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException)
        {
            // pragmas are an optimization only — ignore and run with the defaults
        }
    }

    private static async Task BuildSearchIndexAsync(
        DbConnection connection,
        Action<string>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<SymbolFact>? symbols = null,
        IReadOnlyList<ReferenceFact>? references = null
    )
    {
        // symbol_fts: one row per distinct SymbolId (matching SearchSymbolsAsync's dedup), trigram over
        // symbolid + name (the two LIKE'd columns); kind + the display payload ride along UNINDEXED so a
        // single MATCH returns everything `rig symbols` prints — no join back to symbol_facts.
        await ExecuteAsync(connection, null, "DROP TABLE IF EXISTS symbol_fts;", cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            """
            CREATE VIRTUAL TABLE symbol_fts USING fts5(
                symbolid, name,
                kind UNINDEXED, signature UNINDEXED, filepath UNINDEXED, line UNINDEXED, assembly UNINDEXED,
                tokenize = 'trigram');
            """,
            cancellationToken
        );
        if (symbols is not null)
        {
            await InsertSymbolFtsFromMemoryAsync(connection, symbols, cancellationToken);
        }
        else
        {
            await ExecuteAsync(
                connection,
                null,
                """
                INSERT INTO symbol_fts(symbolid, name, kind, signature, filepath, line, assembly)
                SELECT SymbolId, Name, Kind, Signature, FilePath, Line, DefiningAssembly
                FROM symbol_facts GROUP BY SymbolId;
                """,
                cancellationToken
            );
        }

        // ref_target_fts: the DISTINCT target symbols (far fewer than the millions of reference rows,
        // and a superset of symbol_facts — includes BCL/external targets `rig refs` can search). A MATCH
        // resolves the substring to exact target ids; `rig refs` then fetches rows via the existing
        // reference_facts(TargetSymbolId) index. So the FTS stays small and the row fetch stays indexed.
        await ExecuteAsync(connection, null, "DROP TABLE IF EXISTS ref_target_fts;", cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            "CREATE VIRTUAL TABLE ref_target_fts USING fts5(symbolid, tokenize = 'trigram');",
            cancellationToken
        );
        if (references is not null)
        {
            await InsertRefTargetFtsFromMemoryAsync(connection, references, cancellationToken);
        }
        else
        {
            await ExecuteAsync(
                connection,
                null,
                "INSERT INTO ref_target_fts(symbolid) SELECT DISTINCT TargetSymbolId FROM reference_facts;",
                cancellationToken
            );
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM symbol_fts), (SELECT count(*) FROM ref_target_fts);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            progress?.Invoke($"search index: {reader.GetInt32(0)} symbols, {reader.GetInt32(1)} ref targets");
        }
    }

    // Feed symbol_fts from the in-memory SymbolFacts, one row per distinct SymbolId — the RAM equivalent of
    // the `… GROUP BY SymbolId` SELECT, with no symbol_facts scan. (Like that GROUP BY, which row's display
    // payload represents a duplicate id is arbitrary; here it's the first-seen — cosmetic only, and only for
    // multi-targeted/partial duplicates.) One reused prepared insert inside a single transaction.
    private static async Task InsertSymbolFtsFromMemoryAsync(
        DbConnection connection,
        IReadOnlyList<SymbolFact> symbols,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO symbol_fts(symbolid, name, kind, signature, filepath, line, assembly) "
            + "VALUES ($sid, $name, $kind, $sig, $file, $line, $asm);";
        var pSid = AddParam(command, "$sid");
        var pName = AddParam(command, "$name");
        var pKind = AddParam(command, "$kind");
        var pSig = AddParam(command, "$sig");
        var pFile = AddParam(command, "$file");
        var pLine = AddParam(command, "$line");
        var pAsm = AddParam(command, "$asm");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in symbols)
        {
            if (!seen.Add(s.SymbolId))
            {
                continue; // one row per distinct SymbolId
            }

            pSid.Value = s.SymbolId;
            pName.Value = s.Name;
            pKind.Value = s.Kind;
            pSig.Value = s.Signature;
            pFile.Value = s.FilePath;
            pLine.Value = s.Line;
            pAsm.Value = s.DefiningAssembly;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // Feed ref_target_fts from the in-memory ReferenceFacts' DISTINCT TargetSymbolId — the RAM equivalent of
    // the `SELECT DISTINCT TargetSymbolId FROM reference_facts` scan (the biggest table), deduped in-process.
    private static async Task InsertRefTargetFtsFromMemoryAsync(
        DbConnection connection,
        IReadOnlyList<ReferenceFact> references,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ref_target_fts(symbolid) VALUES ($sid);";
        var pSid = AddParam(command, "$sid");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in references)
        {
            if (seen.Add(r.TargetSymbolId))
            {
                pSid.Value = r.TargetSymbolId;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int> BuildNodesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, "DROP TABLE IF EXISTS nodes;", cancellationToken);
        await ExecuteAsync(connection, null, "CREATE TABLE nodes(sym TEXT PRIMARY KEY) WITHOUT ROWID;", cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            """
            INSERT OR IGNORE INTO nodes(sym)
            SELECT FromSym FROM call_edges     UNION SELECT ToSym FROM call_edges
            UNION SELECT FromSym FROM dispatch_edges UNION SELECT ToSym FROM dispatch_edges
            UNION SELECT SymbolId FROM symbol_facts WHERE Kind = 'method';
            """,
            cancellationToken
        );
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM nodes;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), InvariantCulture);
    }

    // The derived tables are owned entirely by this routine — index/mine never create or touch them.
    // Global (cross-run): the edges are deduped facts joined by DocID, with no RunId, matching the
    // run-agnostic semantics of LoadFactGraphAsync.
    private static async Task EnsureSchemaAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            null,
            """
            CREATE TABLE IF NOT EXISTS call_edges (
                FromSym      TEXT NOT NULL,
                ToSym        TEXT NOT NULL,
                Kind         TEXT NOT NULL,
                FilePath     TEXT,
                Line         INTEGER,
                LoopKind     TEXT,
                LoopDetail   TEXT,
                ReceiverType TEXT,
                HandoffDispatcher TEXT,
                DeliveryPrecision TEXT,
                NonVirtual INTEGER
            );
            """,
            cancellationToken
        );

        await ExecuteAsync(connection, null, "CREATE INDEX IF NOT EXISTS IX_call_edges_FromSym ON call_edges(FromSym);", cancellationToken);
        await ExecuteAsync(connection, null, "CREATE INDEX IF NOT EXISTS IX_call_edges_ToSym ON call_edges(ToSym);", cancellationToken);
        // Index on Kind so the handoff-EP read (DeriveHandoffEntryPoints) selects the ~5k handoff +
        // methodGroup rows by index instead of scanning all ~533k call_edges.
        await ExecuteAsync(connection, null, "CREATE INDEX IF NOT EXISTS IX_call_edges_Kind ON call_edges(Kind);", cancellationToken);

        await ExecuteAsync(
            connection,
            null,
            """
            CREATE TABLE IF NOT EXISTS dispatch_edges (
                FromSym TEXT NOT NULL,
                ToSym   TEXT NOT NULL,
                Kind    TEXT NOT NULL,
                Basis   TEXT
            );
            """,
            cancellationToken
        );

        await ExecuteAsync(
            connection,
            null,
            "CREATE INDEX IF NOT EXISTS IX_dispatch_edges_FromSym ON dispatch_edges(FromSym);",
            cancellationToken
        );

        await ExecuteAsync(
            connection,
            null,
            "CREATE INDEX IF NOT EXISTS IX_dispatch_edges_ToSym ON dispatch_edges(ToSym);",
            cancellationToken
        );
    }

    private static async Task<int> InsertCallEdgesAsync(
        DbConnection connection,
        DbTransaction transaction,
        FactGraphData graph,
        Action<string>? progress,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO call_edges (FromSym, ToSym, Kind, FilePath, Line, LoopKind, LoopDetail, ReceiverType, HandoffDispatcher, DeliveryPrecision, NonVirtual) "
            + "VALUES ($from, $to, $kind, $file, $line, $loopKind, $loopDetail, $receiver, $handoff, $precision, $nonVirtual);";
        var pFrom = AddParam(command, "$from");
        var pTo = AddParam(command, "$to");
        var pKind = AddParam(command, "$kind");
        var pFile = AddParam(command, "$file");
        var pLine = AddParam(command, "$line");
        var pLoopKind = AddParam(command, "$loopKind");
        var pLoopDetail = AddParam(command, "$loopDetail");
        var pReceiver = AddParam(command, "$receiver");
        var pHandoff = AddParam(command, "$handoff");
        var pPrecision = AddParam(command, "$precision");
        var pNonVirtual = AddParam(command, "$nonVirtual");

        var count = 0;
        foreach (var edge in FactPathFinder.AllCallEdges(graph))
        {
            pFrom.Value = edge.From;
            pTo.Value = edge.To;
            pKind.Value = edge.Kind;
            pFile.Value = (object?)edge.File ?? DBNull.Value;
            pLine.Value = edge.Line;
            pLoopKind.Value = (object?)edge.LoopKind ?? DBNull.Value;
            pLoopDetail.Value = (object?)edge.LoopDetail ?? DBNull.Value;
            pReceiver.Value = (object?)edge.ReceiverType ?? DBNull.Value;
            pHandoff.Value = (object?)edge.HandoffDispatcher ?? DBNull.Value;
            pPrecision.Value = (object?)edge.DeliveryPrecision ?? DBNull.Value;
            pNonVirtual.Value = edge.NonVirtual ? 1 : 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (++count % InsertBatchSize == 0)
            {
                progress?.Invoke($"call_edges: {count}");
            }
        }
        progress?.Invoke($"call_edges: {count} (done)");
        return count;
    }

    private static async Task<(int Total, int Heuristic)> InsertDispatchEdgesAsync(
        DbConnection connection,
        DbTransaction transaction,
        FactGraphData graph,
        Action<string>? progress,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO dispatch_edges (FromSym, ToSym, Kind, Basis) VALUES ($from, $to, $kind, $basis);";
        var pFrom = AddParam(command, "$from");
        var pTo = AddParam(command, "$to");
        var pKind = AddParam(command, "$kind");
        var pBasis = AddParam(command, "$basis");

        var count = 0;
        var heuristic = 0;
        foreach (var edge in FactPathFinder.AllDispatchEdges(graph))
        {
            pFrom.Value = edge.From;
            pTo.Value = edge.To;
            pKind.Value = edge.Kind;
            pBasis.Value = edge.Basis;
            if (edge.Basis == "heuristic")
            {
                heuristic++;
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
            if (++count % InsertBatchSize == 0)
            {
                progress?.Invoke($"dispatch_edges: {count}");
            }
        }
        progress?.Invoke($"dispatch_edges: {count} (done; {count - heuristic} roslyn-mined, {heuristic} heuristic)");
        return (count, heuristic);
    }

    private static DbParameter AddParam(DbCommand command, string name)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        command.Parameters.Add(p);
        return p;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

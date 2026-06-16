using System.Data.Common;
using Microsoft.EntityFrameworkCore;
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
        Domain.Data.FactHandoffRule[]? handoffRules = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<Domain.Data.FactGenericFactoryRule>? factoryRules = null
    )
    {
        progress?.Invoke("Loading facts");
        // Classify dispatcher-consumed method-group edges here (the materializer owns the call_edges
        // table) so the persisted Kind="handoff" + HandoffDispatcher flow to every SQL reader; the
        // in-memory oracle classifies identically by being given the same rules.
        var graph = await Reads.LoadFactGraphAsync(context, handoffRules, cancellationToken);

        // Bake generic-factory monomorphization into the persisted edges — the SAME RewriteGenericFactories
        // the in-memory traversal applies via ShapeGraph. This is the EDGE-CREATING shaping (it rewrites
        // `caller -> Factory<X>` to `caller -> X.Target`), so it MUST be in call_edges or the SQL bounding
        // walk traverses the un-rewritten factory edge and never pulls X.Target's closure into the bounded
        // subgraph — under-reporting reach vs the in-memory oracle (the effect-path divergence). Cut and
        // context-dispatch shaping are deliberately NOT baked: they are traversal-time (a cut removes
        // reach; context narrows dispatch), so leaving them out keeps call_edges a sound SUPERSET that the
        // in-memory pass re-applies over the bounded graph. No-op when factoryRules is null/empty.
        graph = FactPathFinder.RewriteGenericFactories(graph, factoryRules ?? []);

        var connection = (DbConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

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
        await BuildSearchIndexAsync(connection, progress, cancellationToken);

        // Refresh whole-store statistics (sqlite_stat1) now that all derived tables + indexes exist, so the
        // query-time planner picks the right index/join order for whole-store reads (entry-point data,
        // dispatch facts) instead of guessing. One-time cost at graph build; query connections only read.
        progress?.Invoke("Analyzing statistics");
        await ExecuteAsync(connection, null, "ANALYZE;", cancellationToken);

        return new GraphStats(CallEdges: callCount, DispatchEdges: dispatchCount, Nodes: nodeCount, HeuristicDispatchEdges: heuristicCount);
    }

    private static async Task BuildSearchIndexAsync(DbConnection connection, Action<string>? progress, CancellationToken cancellationToken)
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
        await ExecuteAsync(
            connection,
            null,
            "INSERT INTO ref_target_fts(symbolid) SELECT DISTINCT TargetSymbolId FROM reference_facts;",
            cancellationToken
        );

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM symbol_fts), (SELECT count(*) FROM ref_target_fts);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            progress?.Invoke($"search index: {reader.GetInt32(0)} symbols, {reader.GetInt32(1)} ref targets");
        }
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
                HandoffDispatcher TEXT
            );
            """,
            cancellationToken
        );
        // Add the ReceiverType column to a pre-existing call_edges (a store created before receiver-type
        // dispatch narrowing). `rig graph` rebuilds the rows anyway; this just makes the column present
        // so the INSERT/SELECT carry it. Reads degrade gracefully (NULL receiver => CHA) on old stores.
        await AddColumnIfMissingAsync(
            connection,
            table: "call_edges",
            column: "ReceiverType",
            type: "TEXT",
            cancellationToken: cancellationToken
        );
        // Likewise add HandoffDispatcher to a pre-existing table so the INSERT/SELECT carry it (a store
        // created before async-handoff classification). Re-`rig graph` repopulates it from the rules.
        await AddColumnIfMissingAsync(
            connection,
            table: "call_edges",
            column: "HandoffDispatcher",
            type: "TEXT",
            cancellationToken: cancellationToken
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
        // Add Basis to a pre-existing dispatch_edges (a store graphed before dispatch provenance).
        // Render-only — the CTE set walk never reads it; re-`rig graph` repopulates the rows.
        await AddColumnIfMissingAsync(
            connection,
            table: "dispatch_edges",
            column: "Basis",
            type: "TEXT",
            cancellationToken: cancellationToken
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
        Domain.Data.FactGraphData graph,
        Action<string>? progress,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO call_edges (FromSym, ToSym, Kind, FilePath, Line, LoopKind, LoopDetail, ReceiverType, HandoffDispatcher) "
            + "VALUES ($from, $to, $kind, $file, $line, $loopKind, $loopDetail, $receiver, $handoff);";
        var pFrom = AddParam(command, "$from");
        var pTo = AddParam(command, "$to");
        var pKind = AddParam(command, "$kind");
        var pFile = AddParam(command, "$file");
        var pLine = AddParam(command, "$line");
        var pLoopKind = AddParam(command, "$loopKind");
        var pLoopDetail = AddParam(command, "$loopDetail");
        var pReceiver = AddParam(command, "$receiver");
        var pHandoff = AddParam(command, "$handoff");

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
        Domain.Data.FactGraphData graph,
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

    // Adds `column` to `table` when a pre-existing store doesn't already have it (idempotent). SQLite
    // has no "ADD COLUMN IF NOT EXISTS", so probe PRAGMA table_info first.
    private static async Task AddColumnIfMissingAsync(
        DbConnection connection,
        string table,
        string column,
        string type,
        CancellationToken cancellationToken
    )
    {
        if (!await StorageProbes.ColumnExistsAsync(connection, table, column, cancellationToken))
        {
            await ExecuteAsync(connection, null, $"ALTER TABLE {table} ADD COLUMN {column} {type};", cancellationToken);
        }
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

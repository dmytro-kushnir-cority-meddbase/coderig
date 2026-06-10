using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Rig.Domain.Functions;
using Rig.Storage.Storage;

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
    public sealed record GraphStats(int CallEdges, int DispatchEdges);

    private const int InsertBatchSize = 20_000;

    public static async Task<GraphStats> BuildAsync(
        RigDbContext context, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Invoke("Loading facts");
        var graph = await Reads.LoadFactGraphAsync(context, cancellationToken).ConfigureAwait(false);

        var connection = (DbConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

        progress?.Invoke("Rebuilding derived edge tables");
        using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "DELETE FROM call_edges;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM dispatch_edges;", cancellationToken).ConfigureAwait(false);

        var callCount = await InsertCallEdgesAsync(connection, transaction, graph, progress, cancellationToken).ConfigureAwait(false);
        var dispatchCount = await InsertDispatchEdgesAsync(connection, transaction, graph, progress, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GraphStats(callCount, dispatchCount);
    }

    // The derived tables are owned entirely by this routine — index/mine never create or touch them.
    // Global (cross-run): the edges are deduped facts joined by DocID, with no RunId, matching the
    // run-agnostic semantics of LoadFactGraphAsync.
    private static async Task EnsureSchemaAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, """
            CREATE TABLE IF NOT EXISTS call_edges (
                FromSym    TEXT NOT NULL,
                ToSym      TEXT NOT NULL,
                Kind       TEXT NOT NULL,
                FilePath   TEXT,
                Line       INTEGER,
                LoopKind   TEXT,
                LoopDetail TEXT
            );
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            "CREATE INDEX IF NOT EXISTS IX_call_edges_FromSym ON call_edges(FromSym);", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            "CREATE INDEX IF NOT EXISTS IX_call_edges_ToSym ON call_edges(ToSym);", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, null, """
            CREATE TABLE IF NOT EXISTS dispatch_edges (
                FromSym TEXT NOT NULL,
                ToSym   TEXT NOT NULL,
                Kind    TEXT NOT NULL
            );
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            "CREATE INDEX IF NOT EXISTS IX_dispatch_edges_FromSym ON dispatch_edges(FromSym);", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            "CREATE INDEX IF NOT EXISTS IX_dispatch_edges_ToSym ON dispatch_edges(ToSym);", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> InsertCallEdgesAsync(
        DbConnection connection, DbTransaction transaction, Domain.Data.FactGraphData graph,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO call_edges (FromSym, ToSym, Kind, FilePath, Line, LoopKind, LoopDetail) " +
            "VALUES ($from, $to, $kind, $file, $line, $loopKind, $loopDetail);";
        var pFrom = AddParam(command, "$from");
        var pTo = AddParam(command, "$to");
        var pKind = AddParam(command, "$kind");
        var pFile = AddParam(command, "$file");
        var pLine = AddParam(command, "$line");
        var pLoopKind = AddParam(command, "$loopKind");
        var pLoopDetail = AddParam(command, "$loopDetail");

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
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (++count % InsertBatchSize == 0) progress?.Invoke($"call_edges: {count}");
        }
        progress?.Invoke($"call_edges: {count} (done)");
        return count;
    }

    private static async Task<int> InsertDispatchEdgesAsync(
        DbConnection connection, DbTransaction transaction, Domain.Data.FactGraphData graph,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO dispatch_edges (FromSym, ToSym, Kind) VALUES ($from, $to, $kind);";
        var pFrom = AddParam(command, "$from");
        var pTo = AddParam(command, "$to");
        var pKind = AddParam(command, "$kind");

        var count = 0;
        foreach (var edge in FactPathFinder.AllDispatchEdges(graph))
        {
            pFrom.Value = edge.From;
            pTo.Value = edge.To;
            pKind.Value = edge.Kind;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (++count % InsertBatchSize == 0) progress?.Invoke($"dispatch_edges: {count}");
        }
        progress?.Invoke($"dispatch_edges: {count} (done)");
        return count;
    }

    private static DbParameter AddParam(DbCommand command, string name)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        command.Parameters.Add(p);
        return p;
    }

    private static async Task ExecuteAsync(
        DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

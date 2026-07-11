using System.Data.Common;

namespace Rig.Storage.Queries;

// Raw-SQL helpers that own the `meta` table — the single DB-FILE-level schema-version row. There is ONE
// row (id=0) per db: the table SHAPE is shared across the (multi-)runs a store holds, and the GRAPH
// (call_edges/dispatch_edges/nodes/fts) is global (no RunId), so schema-version + graph-presence are
// properties of the FILE, not of any run. The write paths use this from both the EF index path (Writes)
// and the raw-SQL graph path (GraphMaterializer); the read gate (SchemaGate) reads it. NOT a migration
// system: these helpers only STAMP and READ a version — an old store is re-indexed, never transformed.
//
//   meta(id=0, index_schema_version, graph_schema_version)
//
// graph_schema_version NULL = no/stale graph (a --no-graph index, a pre-graph store, or a post-append
// store whose graph the append left stale). NULL vs SchemaVersion.Graph IS the "current stage" indicator
// (index-only vs graphed) — there is deliberately no separate stage column.
internal static class SchemaMeta
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS meta (
          id                   INTEGER PRIMARY KEY CHECK (id = 0),
          index_schema_version INTEGER NOT NULL,
          graph_schema_version INTEGER
        );
        """;

    // Stamp the index version and NULL the graph version. Called by the INDEX write path after the facts
    // are written: a fresh full index (or an append) invalidates any prior graph, so the graph stage is
    // reset to "absent" until GraphMaterializer re-stamps it. Upserts the single id=0 row.
    public static async Task WriteIndexVersionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta (id, index_schema_version, graph_schema_version)
            VALUES (0, $index, NULL)
            ON CONFLICT(id) DO UPDATE SET index_schema_version = $index, graph_schema_version = NULL;
            """;
        AddParam(command, "$index", SchemaVersion.Index);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Stamp the graph version on the existing row (the index version must already be present — graph build
    // runs over an indexed store). Leaves index_schema_version untouched. Upserts the id=0 row defensively
    // (the index path stamps it first, but BuildAsync re-graph over a legacy store may find no meta row;
    // in that case stamp the current index version too, since the graph build proves the store is current).
    public static async Task WriteGraphVersionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta (id, index_schema_version, graph_schema_version)
            VALUES (0, $index, $graph)
            ON CONFLICT(id) DO UPDATE SET graph_schema_version = $graph;
            """;
        AddParam(command, "$index", SchemaVersion.Index);
        AddParam(command, "$graph", SchemaVersion.Graph);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // (index_schema_version, graph_schema_version) from the id=0 row, or (null, null) when meta is absent
    // (no table, or no row). Tolerant of the table being missing on a legacy store — that case is what the
    // gate reports as "not an initialized rig store".
    public static async Task<(int? Index, int? Graph)> ReadAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (!await StorageProbes.TableExistsAsync(connection, "meta", cancellationToken))
        {
            return (null, null);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT index_schema_version, graph_schema_version FROM meta WHERE id = 0 LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (null, null);
        }

        int? index = reader.IsDBNull(0) ? null : reader.GetInt32(0);
        int? graph = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        return (index, graph);
    }

    private static async Task EnsureTableAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = CreateSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParam(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}

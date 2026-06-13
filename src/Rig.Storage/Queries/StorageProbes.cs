using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Rig.Storage.Storage;

namespace Rig.Storage.Queries;

// Shared SQLite connection + schema probes for the query classes (Reads / SqlReachability /
// GraphMaterializer). The "does this store predate column/table X?" degradation checks were previously
// copy-pasted across those classes with subtly DIFFERENT SQL — e.g. one TableExists omitted the
// `type='table'` guard, another included it — so there was no single source of truth for what counts as
// "present". Centralising them here removes that drift. SQLite has no "ADD COLUMN IF NOT EXISTS", which
// is why ColumnExists probes PRAGMA table_info rather than relying on DDL guards.
internal static class StorageProbes
{
    // The EF-managed connection, opened if the caller hasn't opened it yet (raw-ADO query paths need an
    // open connection; FTS5 / PRAGMA / recursive-CTE SQL isn't expressible in EF LINQ).
    public static async Task<DbConnection> OpenConnectionAsync(RigDbContext context, CancellationToken cancellationToken)
    {
        var connection = (DbConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    // True when `table` exists as a real or virtual table. FTS5 virtual tables are recorded in
    // sqlite_master with type='table', so the guard still matches symbol_fts / ref_target_fts.
    public static async Task<bool> TableExistsAsync(DbConnection connection, string table, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        var p = command.CreateParameter();
        p.ParameterName = "$name";
        p.Value = table;
        command.Parameters.Add(p);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    // True when `table` has a column named `column` (case-insensitive), via PRAGMA table_info — the only
    // portable way to detect a column on a store that may predate a schema addition.
    public static async Task<bool> ColumnExistsAsync(
        DbConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

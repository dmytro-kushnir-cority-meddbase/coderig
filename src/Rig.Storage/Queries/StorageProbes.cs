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
        {
            await connection.OpenAsync(cancellationToken);
            await ApplyReadPragmasAsync(connection, cancellationToken);
        }
        return connection;
    }

    // Tune the freshly-opened connection for the whole-store / bounded scans the query paths run against a
    // multi-GB store. Defaults (mmap_size=0, 2 MB page cache) make every cold scan a syscall-per-page read.
    // memory-mapped IO turns page faults into mapped reads, a bigger cache holds the hot b-tree pages, and
    // an in-memory temp store keeps reach_set/reach_depth (and their ANALYZE stats) in RAM. All are
    // connection-local, read-only-safe, and best-effort: a PRAGMA that doesn't take just leaves the default.
    private static async Task ApplyReadPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA mmap_size=1073741824; PRAGMA cache_size=-262144; PRAGMA temp_store=MEMORY;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException)
        {
            // pragmas are an optimization only — ignore and run with the defaults
        }
    }

    // True when `table` exists as a real or virtual table. FTS5 virtual tables are recorded in
    // sqlite_master with type='table', so the guard still matches symbol_fts / ref_target_fts.
    public static async Task<bool> TableExistsAsync(DbConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        var p = command.CreateParameter();
        p.ParameterName = "$name";
        p.Value = table;
        command.Parameters.Add(p);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
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
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

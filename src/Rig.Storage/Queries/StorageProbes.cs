using System.Data;
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
    // Named connection tuning profiles — the single registry of which PRAGMAs a freshly-opened connection
    // gets. SQLite defaults (mmap_size=0, 2 MB page cache) make every cold scan a syscall-per-page read; the
    // profiles below trade memory for that. Profile choice is connection-scoped (applied once, on first
    // open), and best-effort: a PRAGMA that doesn't take just leaves the default. Measured on the MedDBase
    // store: capping mmap/cache (BoundedRead) ~halves peak RAM (1.9 GB → ~1.0 GB) for ~7% slower load,
    // because rig loads tables once into managed objects — the big mmap was resident RAM with little
    // latency payoff for a one-shot query. It pays off only when ONE connection serves MANY queries.
    internal enum Profile
    {
        // One-shot CLI reads (reaches/tree/path/callers/derive/impact/symbols/refs). Caps mmap+cache to
        // bound peak RAM; the loser is ~7% on a single query, which one-shot reads don't care about.
        BoundedRead,

        // Retained-page reads + fact-save writes: big mmap + cache. Worth the RAM only where pages are
        // re-read across the connection's life (a future warm/shared read; bulk fact INSERTs in `index`).
        Speed,

        // The `rig graph` materialize set: Speed + synchronous=OFF. Index/graph RAM is dominated by Roslyn
        // extraction anyway, so it is NOT bounded — it keeps the throughput profile. (Owned here so the
        // pragma set is named in one place; GraphMaterializer selects it instead of inlining the string.)
        Index,

        // The `rig index` bulk fact-INSERT set (Writes.SaveAsync): the heaviest, single-writer-owns-the-file
        // tuning — no rollback journal, no fsync, 4 GB mmap, exclusive lock. For writing millions of fact
        // rows into a fresh, disposable store where durability doesn't matter (a crash just re-indexes).
        BulkWrite,
    }

    internal static string PragmaSqlFor(Profile profile) =>
        profile switch
        {
            Profile.BoundedRead => "PRAGMA mmap_size=268435456; PRAGMA cache_size=-131072; PRAGMA temp_store=MEMORY;",
            Profile.Speed => "PRAGMA mmap_size=1073741824; PRAGMA cache_size=-262144; PRAGMA temp_store=MEMORY;",
            Profile.Index => "PRAGMA mmap_size=1073741824; PRAGMA cache_size=-262144; PRAGMA temp_store=MEMORY; PRAGMA synchronous=OFF;",
            Profile.BulkWrite => "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; "
                + "PRAGMA mmap_size=4294967296; PRAGMA cache_size=-262144; PRAGMA locking_mode=EXCLUSIVE;",
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, message: null),
        };

    // The EF-managed connection, opened if the caller hasn't opened it yet (raw-ADO query paths need an
    // open connection; FTS5 / PRAGMA / recursive-CTE SQL isn't expressible in EF LINQ). The default profile
    // is BoundedRead — every caller of THIS method is a one-shot read. The writers tune separately:
    // GraphMaterializer opens its own connection and applies Index, and SaveAsync applies BulkWrite via
    // ApplyProfileAsync. The profile is applied ONLY on the open this call performs (first-open-wins), so
    // the first opener of a connection chooses its tuning for the connection's life.
    public static async Task<DbConnection> OpenConnectionAsync(
        RigDbContext context,
        CancellationToken cancellationToken,
        Profile profile = Profile.BoundedRead
    )
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            await ApplyProfileAsync(connection, profile, cancellationToken);
        }

        return connection;
    }

    // Apply a profile's PRAGMAs to an already-open connection, unconditionally. OpenConnectionAsync uses
    // this for the open it performs; callers whose connection was opened elsewhere (e.g. SaveAsync, where
    // EnsureCreatedAsync opens it first) call this directly so the tuning still takes. Routing every tuned
    // open through the factory means all profile uses are greppable in one place. Best-effort: a PRAGMA
    // that doesn't take just leaves the default.
    public static async Task ApplyProfileAsync(DbConnection connection, Profile profile, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = PragmaSqlFor(profile);
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
}

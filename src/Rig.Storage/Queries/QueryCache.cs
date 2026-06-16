using Microsoft.Data.Sqlite;

namespace Rig.Storage.Queries;

// A small, best-effort query-result cache backed by a SEPARATE writable SQLite file (`.rig/cache.db`).
// The main store (`.rig/rig.db`) is opened Mode=ReadOnly at query time, so derived query artifacts
// (e.g. a `rig tree` forest + its effects) can't be written back into it — they live here instead.
//
// Invalidation is automatic on reindex: every entry is stamped with a `storeKey` derived from the
// current rig.db identity (run-id + file size + mtime — see CacheKeys). `rig index`/`rig graph` rewrite
// rig.db, changing the storeKey, so (a) the storeKey is also folded into each entry's key (stale keys
// never match) and (b) Open() purges every row whose storeKey != the current one, keeping cache.db from
// growing across reindexes. Entirely best-effort: any SQLite failure degrades to "no cache" (Open
// returns null, Get returns null, Put is a no-op) — the cache can never change a query's result, only
// its latency.
public sealed class QueryCache : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _storeKey;

    private QueryCache(SqliteConnection connection, string storeKey)
    {
        _connection = connection;
        _storeKey = storeKey;
    }

    // Opens (creating if absent) the cache next to rig.db and drops entries from any prior store
    // identity. Returns null when the cache can't be opened — callers treat null as "caching disabled".
    public static QueryCache? Open(string rigDirectory, string storeKey)
    {
        try
        {
            var path = Path.Combine(rigDirectory, "cache.db");
            // Pooling=False so Dispose actually closes the file handle (a pooled connection keeps it open,
            // which blocks deleting/replacing cache.db). The cache is opened once per command, so there's
            // nothing to pool anyway.
            var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            // A single command may hold two cache connections briefly (e.g. `rig tree` uses one for the
            // forest and BuildEpContext opens another for the EP set). busy_timeout makes a writer wait
            // out a transient lock instead of failing immediately; Put still catches if it ultimately can't.
            Exec(connection, "PRAGMA busy_timeout=3000;");
            Exec(
                connection,
                "CREATE TABLE IF NOT EXISTS artifact_cache (key TEXT PRIMARY KEY, store_key TEXT NOT NULL, payload BLOB NOT NULL) WITHOUT ROWID;"
            );
            using (var purge = connection.CreateCommand())
            {
                purge.CommandText = "DELETE FROM artifact_cache WHERE store_key <> $sk;";
                purge.Parameters.AddWithValue("$sk", storeKey);
                purge.ExecuteNonQuery();
            }
            return new QueryCache(connection, storeKey);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public byte[]? Get(string key)
    {
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT payload FROM artifact_cache WHERE key = $k AND store_key = $sk;";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$sk", _storeKey);
            return command.ExecuteScalar() as byte[];
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public void Put(string key, byte[] payload)
    {
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO artifact_cache (key, store_key, payload) VALUES ($k, $sk, $p) "
                + "ON CONFLICT(key) DO UPDATE SET store_key = excluded.store_key, payload = excluded.payload;";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$sk", _storeKey);
            command.Parameters.AddWithValue("$p", payload);
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Best-effort: a failed write just means the next query recomputes.
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

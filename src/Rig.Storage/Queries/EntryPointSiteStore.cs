using System.Data.Common;
using Rig.Storage.Storage;

namespace Rig.Storage.Queries;

// The derived entry-point SITE set — (file,line) -> (kind, capability requirements) — materialized as a
// first-class table at `rig graph` time, exactly like call_edges/dispatch_edges/nodes. Query-time EP
// rendering then reads a few thousand small rows via raw ADO (no EF materialization, no whole-store fact
// load, no re-derivation) instead of loading 217k method rows + 136k ctor refs and running the deriver
// (~2.1s cold). Stamped with the rules-hash it was built under: a query whose effective rules differ
// (e.g. --rules) sees a mismatch and the caller falls back to a live derive. Rebuilt on every reindex,
// so it can never be staler than the store. A store predating this table (no `rig graph` since) simply
// reads as "absent" and the caller degrades to the derive path.
public static class EntryPointSiteStore
{
    // (FilePath, Line) -> (Kind, Requires). Requires is stored comma-joined (capability tokens carry no
    // commas), NULL when the EP has no capability gate.
    public static async Task PersistAsync(
        RigDbContext context,
        IReadOnlyDictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)> sites,
        string rulesHash,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // One command, four statements — Microsoft.Data.Sqlite steps a ;-delimited batch in a single
        // ExecuteNonQuery (as ApplyReadPragmasAsync does), so the schema reset is one call, not four.
        await using (var ddl = connection.CreateCommand())
        {
            ddl.Transaction = (DbTransaction)tx;
            ddl.CommandText = """
                DROP TABLE IF EXISTS entry_point_sites;
                DROP TABLE IF EXISTS entry_point_sites_meta;
                CREATE TABLE entry_point_sites(FilePath TEXT NOT NULL, Line INTEGER NOT NULL, Kind TEXT NOT NULL, Requires TEXT);
                CREATE TABLE entry_point_sites_meta(RulesHash TEXT NOT NULL);
                """;
            await ddl.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var meta = connection.CreateCommand())
        {
            meta.Transaction = (DbTransaction)tx;
            meta.CommandText = "INSERT INTO entry_point_sites_meta(RulesHash) VALUES ($h);";
            AddParam(meta, "$h", rulesHash);
            await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (DbTransaction)tx;
            insert.CommandText = "INSERT INTO entry_point_sites(FilePath, Line, Kind, Requires) VALUES ($f, $l, $k, $r);";
            var pf = AddParam(insert, "$f", "");
            var pl = AddParam(insert, "$l", 0);
            var pk = AddParam(insert, "$k", "");
            var pr = AddParam(insert, "$r", "");
            foreach (var kv in sites)
            {
                pf.Value = kv.Key.File;
                pl.Value = kv.Key.Line;
                pk.Value = kv.Value.Kind;
                pr.Value = kv.Value.Requires is { } req ? string.Join(",", req) : DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // The materialized set if it exists AND was built under `rulesHash`; null otherwise (absent table, or
    // built under different rules — caller derives live). Empty-but-present is a valid result (returned as
    // an empty map), distinguished from "absent" by the meta table.
    public static async Task<IReadOnlyDictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>?> LoadAsync(
        RigDbContext context,
        string rulesHash,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);
        if (!await StorageProbes.TableExistsAsync(connection, "entry_point_sites_meta", cancellationToken).ConfigureAwait(false))
            return null;

        string? storedHash;
        await using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "SELECT RulesHash FROM entry_point_sites_meta LIMIT 1;";
            storedHash = await meta.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        if (!string.Equals(storedHash, rulesHash, StringComparison.Ordinal))
            return null; // built under different rules (e.g. --rules) → caller derives under its own rules

        var map = new Dictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT FilePath, Line, Kind, Requires FROM entry_point_sites;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var file = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var line = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var kind = reader.IsDBNull(2) ? "" : reader.GetString(2);
            IReadOnlyList<string>? requires = reader.IsDBNull(3)
                ? null
                : reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries);
            map[(file, line)] = (kind, requires);
        }
        return map;
    }

    private static DbParameter AddParam(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
        return p;
    }
}

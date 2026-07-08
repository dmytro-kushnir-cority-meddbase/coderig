using System.Data.Common;
using Rig.Storage.Storage;

namespace Rig.Storage.Queries;

// The open-time fail-fast gate that REPLACES the scattered per-table TableExistsAsync schema probes. The
// `.rig` store is DERIVED + DISPOSABLE; instead of probing "does this column/table exist" deep inside a
// query (old-store drift surfaced as a cryptic mid-query `no such column`), we stamp a schema version
// (SchemaMeta) and check it ONCE when a read context opens. NOT a migration system — a tripwire that says
// "re-index", never "transform".
public static class SchemaGate
{
    // The HARD gate. Read the `meta` row and assert the store is a current, initialized rig store. Called
    // once per query command at the read-context open seam (TraversalGraphLoader.OpenReadContextGated*).
    // Writers (index/graph) are NOT gated — they CREATE the store.
    //   - meta missing (no table / no row)        -> not an initialized rig store, run `rig index`
    //   - index_schema_version != SchemaVersion.Index -> store schema vN, this rig expects vM, re-index
    // Context overload — opens the EF-managed connection (read pragmas applied) and asserts. The CLI read
    // commands call this at the open seam (they hold a RigDbContext, not a raw connection).
    public static async Task AssertReadableAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        await AssertReadableAsync(connection, cancellationToken);
    }

    public static async Task AssertReadableAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        var (index, _) = await SchemaMeta.ReadAsync(connection, cancellationToken);
        if (index is null)
        {
            throw new RigStoreException(
                $"Not an initialized rig store (schema v{SchemaVersion.Index} expected, none found) — run `rig index`."
            );
        }

        if (index != SchemaVersion.Index)
        {
            throw new RigStoreException(
                $"Store schema v{index}, this rig expects v{SchemaVersion.Index} — re-index (the .rig store is disposable; rebuild it with `rig index`)."
            );
        }

        // Targeted drift tripwire: `reference_facts.EnclosingGuards` (control-dependence guards) was added
        // WITHOUT bumping SchemaVersion.Index, so a store predating it shares the current version and passes
        // the checks above — then fails MID-QUERY with a raw `no such column` DbException, which
        // CommandGuard.StoreError mis-attributes to the DEFAULT/LATEST store path (not the one actually
        // opened — the reported bug). Probe it at OPEN so the drift fails fast AND names the correct store
        // (connection.DataSource). Add further column probes here for any future add-without-bump drift.
        if (!await StorageProbes.ColumnExistsAsync(connection, table: "reference_facts", column: "EnclosingGuards", cancellationToken))
        {
            throw new RigStoreException(
                $"The store at {connection.DataSource} was built by an older rig (missing reference_facts.EnclosingGuards) "
                    + "— re-index (the .rig store is disposable; rebuild it with `rig index`)."
            );
        }
    }

    // True iff the store carries a CURRENT graph (graph_schema_version == SchemaVersion.Graph). The graph
    // (call_edges/dispatch_edges/nodes/fts) is built as a UNIT by GraphMaterializer, so the per-table
    // presence probes that used to gate individual graph reads collapse to this one check; callers degrade
    // (LIKE fallback / live derive) when it's false. Never asserts the index gate — call AssertReadableAsync
    // for that; this is purely "is the graph stage present + current".
    //
    // graph_schema_version present-but-mismatched is normally false (a stale graph the caller should not
    // trust), BUT the RIG_TRUST_GRAPH escape hatch ("1"/"true") treats a present-but-mismatched graph as
    // available — a dev bypass to skip a regraph after a graph-shape bump. It NEVER bypasses the index gate
    // (a present graph version implies the index version was stamped) and has no effect when the graph is
    // absent (NULL).
    public static async Task<bool> GraphAvailableAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        var (_, graph) = await SchemaMeta.ReadAsync(connection, cancellationToken);
        if (graph is null)
        {
            return false;
        }

        if (graph == SchemaVersion.Graph)
        {
            return true;
        }

        return TrustGraphEnvSet();
    }

    private static bool TrustGraphEnvSet()
    {
        var value = Environment.GetEnvironmentVariable("RIG_TRUST_GRAPH");
        return string.Equals(value, "1", StringComparison.Ordinal) || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}

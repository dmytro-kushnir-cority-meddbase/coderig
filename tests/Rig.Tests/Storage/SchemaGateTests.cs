using Microsoft.EntityFrameworkCore;
using Rig.Storage;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Storage;

// The schema-version tripwire (SchemaMeta) + open-time gate (SchemaGate) that replaced the scattered
// per-table TableExistsAsync schema probes. Uses the session-shared AnalyzedPlaygrounds store + the real
// Writes.SaveAsync / GraphMaterializer.BuildAsync paths (mirrors LoadShapedGraphTests) — no SQLite harness
// from scratch. SchemaMeta is internal, so these tests observe meta THROUGH the public gate + a direct SQL
// read of the meta columns (the table is plain SQL, not the internal class).
[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class SchemaGateTests(AnalyzedPlaygrounds playgrounds)
{
    // (1a) A freshly-INDEXED store (the Writes.SaveAsync path) stamps the current index version and leaves
    // the graph version NULL (no graph built yet).
    [Test]
    public async Task Indexed_store_has_current_index_version_and_null_graph_version()
    {
        await WithStoreAsync(
            buildGraph: false,
            async dbPath =>
            {
                var (index, graph) = await ReadMetaAsync(dbPath);
                index.ShouldBe(SchemaVersion.Index);
                graph.ShouldBeNull("a fresh index has not built the graph yet");
            }
        );
    }

    // (1b) After the graph is built, graph_schema_version is stamped to the current graph version.
    [Test]
    public async Task Graph_build_stamps_current_graph_version()
    {
        await WithStoreAsync(
            buildGraph: true,
            async dbPath =>
            {
                var (index, graph) = await ReadMetaAsync(dbPath);
                index.ShouldBe(SchemaVersion.Index);
                graph.ShouldBe(SchemaVersion.Graph);
            }
        );
    }

    // (2a) AssertReadableAsync SUCCEEDS on a current, freshly-indexed store.
    [Test]
    public async Task AssertReadable_succeeds_on_current_store()
    {
        await WithStoreAsync(
            buildGraph: false,
            async dbPath =>
            {
                await using var read = new RigDbContext(dbPath, pooling: false, readOnly: true);
                await Should.NotThrowAsync(async () => await SchemaGate.AssertReadableAsync(read));
            }
        );
    }

    // (2b) AssertReadableAsync THROWS a clear message when meta is ABSENT (an uninitialized store).
    [Test]
    public async Task AssertReadable_throws_when_meta_absent()
    {
        var dir = Directory.CreateTempSubdirectory("rig-schemagate-empty-").FullName;
        try
        {
            // A bare DB with the EF tables created but NO Writes.SaveAsync — so no meta row was stamped.
            var dbPath = Path.Combine(dir, "rig.db");
            await using (var ctx = new RigDbContext(dbPath, pooling: false))
            {
                await ctx.Database.EnsureCreatedAsync();
            }

            await using var read = new RigDbContext(dbPath, pooling: false, readOnly: true);
            var ex = await Should.ThrowAsync<RigStoreException>(async () => await SchemaGate.AssertReadableAsync(read));
            ex.Message.ShouldContain("Not an initialized rig store");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // (2c) AssertReadableAsync THROWS when the stamped index version is WRONG (write a bogus version).
    [Test]
    public async Task AssertReadable_throws_on_index_version_mismatch()
    {
        await WithStoreAsync(
            buildGraph: false,
            async dbPath =>
            {
                await StampMetaAsync(dbPath, index: SchemaVersion.Index + 99, graph: null);

                await using var read = new RigDbContext(dbPath, pooling: false, readOnly: true);
                var ex = await Should.ThrowAsync<RigStoreException>(async () => await SchemaGate.AssertReadableAsync(read));
                ex.Message.ShouldContain($"v{SchemaVersion.Index + 99}");
                ex.Message.ShouldContain("re-index");
            }
        );
    }

    // (2d) The open-time drift probe: StorageProbes.ColumnExistsAsync reports a REAL column present and a
    // bogus one absent. This is what SchemaGate.AssertReadableAsync uses to fail fast (store-correct) when a
    // store predates reference_facts.EnclosingGuards — instead of the old raw mid-query `no such column`
    // that CommandGuard.StoreError mis-attributed to the LATEST store path. Tested via the PROBE (read-only),
    // NOT by mutating a store's schema in code (forbidden — ad-hoc surgery is sqlite3-CLI-only).
    [Test]
    public async Task ColumnExists_reports_present_and_absent_columns()
    {
        await WithStoreAsync(
            buildGraph: false,
            async dbPath =>
            {
                await using var read = new RigDbContext(dbPath, pooling: false, readOnly: true);
                var connection = read.Database.GetDbConnection();
                await connection.OpenAsync();

                (await StorageProbes.ColumnExistsAsync(connection, "reference_facts", "EnclosingGuards", default)).ShouldBeTrue(
                    "a current store carries the control-dependence-guards column"
                );
                (await StorageProbes.ColumnExistsAsync(connection, "reference_facts", "NoSuchColumn", default)).ShouldBeFalse();

                // Composed behavior: the current store has the column, so the gate passes (the throw path
                // fires only when the probe returns false — covered by the absent-column assertion above).
                await Should.NotThrowAsync(async () => await SchemaGate.AssertReadableAsync(read));
            }
        );
    }

    // (3a) GraphAvailableAsync is FALSE on an index-only store, TRUE after a graph version is written.
    [Test]
    public async Task GraphAvailable_false_index_only_true_after_graph()
    {
        await WithStoreAsync(
            buildGraph: false,
            async dbPath =>
            {
                await using (var read = new RigDbContext(dbPath, pooling: false, readOnly: true))
                {
                    (await GraphAvailableAsync(read)).ShouldBeFalse("no graph built yet");
                }

                await StampMetaAsync(dbPath, index: SchemaVersion.Index, graph: SchemaVersion.Graph);

                await using (var read = new RigDbContext(dbPath, pooling: false, readOnly: true))
                {
                    (await GraphAvailableAsync(read)).ShouldBeTrue("graph version now current");
                }
            }
        );
    }

    // (3b) A present-but-MISMATCHED graph version reads as FALSE without RIG_TRUST_GRAPH, TRUE with it.
    // The escape hatch is GRAPH-only — it never bypasses the (current) index gate.
    [Test]
    public async Task GraphAvailable_mismatch_honours_RIG_TRUST_GRAPH_escape_hatch()
    {
        await WithStoreAsync(
            buildGraph: false,
            async dbPath =>
            {
                await StampMetaAsync(dbPath, index: SchemaVersion.Index, graph: SchemaVersion.Graph + 99);

                var previous = Environment.GetEnvironmentVariable("RIG_TRUST_GRAPH");
                try
                {
                    Environment.SetEnvironmentVariable("RIG_TRUST_GRAPH", null);
                    await using (var read = new RigDbContext(dbPath, pooling: false, readOnly: true))
                    {
                        (await GraphAvailableAsync(read)).ShouldBeFalse("stale graph, no escape hatch");
                    }

                    Environment.SetEnvironmentVariable("RIG_TRUST_GRAPH", "1");
                    await using (var read = new RigDbContext(dbPath, pooling: false, readOnly: true))
                    {
                        (await GraphAvailableAsync(read)).ShouldBeTrue("escape hatch trusts a present-but-stale graph");
                    }
                }
                finally
                {
                    Environment.SetEnvironmentVariable("RIG_TRUST_GRAPH", previous);
                }
            }
        );
    }

    // --- helpers ---

    // Write the LegacyNet48 playground facts to a temp store (optionally building the graph), run the body,
    // then clean up. Mirrors LoadShapedGraphTests.LoadFromStoreAsync.
    private async Task WithStoreAsync(bool buildGraph, Func<string, Task> body)
    {
        var playground = await playgrounds.LegacyNet48Async();
        var dir = Path.Combine(Path.GetTempPath(), "rig-schemagate-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "rig.db");
        try
        {
            await using (var write = new RigDbContext(dbPath, pooling: false))
            {
                await Writes.SaveAsync(write, playground.Result);
            }

            if (buildGraph)
            {
                await using var graphCtx = new RigDbContext(dbPath, pooling: false);
                await GraphMaterializer.BuildAsync(graphCtx);
            }

            await body(dbPath);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            { /* best-effort */
            }
        }
    }

    private static async Task<bool> GraphAvailableAsync(RigDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return await SchemaGate.GraphAvailableAsync(connection);
    }

    // Read the meta columns directly via SQL (SchemaMeta is internal to Rig.Storage; the table is plain SQL).
    private static async Task<(int? Index, int? Graph)> ReadMetaAsync(string dbPath)
    {
        await using var ctx = new RigDbContext(dbPath, pooling: false, readOnly: true);
        var connection = ctx.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT index_schema_version, graph_schema_version FROM meta WHERE id = 0 LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (null, null);
        }

        int? index = reader.IsDBNull(0) ? null : reader.GetInt32(0);
        int? graph = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        return (index, graph);
    }

    // Overwrite the single meta row with explicit versions (read-write connection, raw SQL with params).
    private static async Task StampMetaAsync(string dbPath, int index, int? graph)
    {
        await using var ctx = new RigDbContext(dbPath, pooling: false);
        var connection = ctx.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO meta (id, index_schema_version, graph_schema_version) VALUES (0, $index, $graph) "
            + "ON CONFLICT(id) DO UPDATE SET index_schema_version = $index, graph_schema_version = $graph;";
        AddParam(command, "$index", index);
        AddParam(command, "$graph", graph is null ? DBNull.Value : graph.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParam(System.Data.Common.DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}

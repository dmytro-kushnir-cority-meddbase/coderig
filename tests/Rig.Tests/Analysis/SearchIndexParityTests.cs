using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// Guards the index-time FTS perf shortcut: `rig index` feeds the search index (symbol_fts / ref_target_fts)
// from the in-memory facts it just extracted, while the standalone re-graph path builds the SAME tables by
// scanning the store (INSERT ... SELECT). This asserts both produce an identical search index, so `rig
// symbols` / `rig refs` return the same hits no matter which path built the store. Compared on the indexed
// `symbolid` set (the search invariant) — not the UNINDEXED display payload, which for a duplicate SymbolId
// is arbitrary in BOTH paths (SQL GROUP BY vs first-seen) and would make a full-row compare flaky.
[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class SearchIndexParityTests(AnalyzedPlaygrounds playgrounds)
{
    [Test]
    public async Task Ram_fed_fts_matches_the_store_scan_fts()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var handoffRules = RuleSetLoader.Load(playground.WorkingDirectory).Handoff;
        var result = playground.Result;
        var graph = FactGraphProjection.FromAnalysis(result, handoffRules);

        var ram = await BuildAndReadFtsAsync(result, graph, fromMemory: true);
        var scan = await BuildAndReadFtsAsync(result, graph, fromMemory: false);

        // Non-empty guard so parity isn't vacuous.
        ram.SymbolFts.Count.ShouldBeGreaterThan(0);
        ram.RefTargetFts.Count.ShouldBeGreaterThan(0);

        ram.SymbolFts.ShouldBe(scan.SymbolFts, ignoreOrder: true);
        ram.RefTargetFts.ShouldBe(scan.RefTargetFts, ignoreOrder: true);
    }

    private static async Task<(List<string> SymbolFts, List<string> RefTargetFts)> BuildAndReadFtsAsync(
        AnalysisResult result,
        FactGraphData graph,
        bool fromMemory
    )
    {
        var directory = Path.Combine(Path.GetTempPath(), "rig-ftsparity-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "rig.db");
        try
        {
            await using (var write = new RigDbContext(databasePath, pooling: false))
            {
                await Writes.SaveAsync(write, result);
            }

            await using (var build = new RigDbContext(databasePath, pooling: false))
            {
                await GraphMaterializer.BuildFromGraphAsync(
                    build,
                    graph,
                    factoryRules: null,
                    progress: null,
                    cancellationToken: default,
                    // The path under test: in-memory facts feed the FTS (true) vs the on-disk SELECT scan (false).
                    symbols: fromMemory ? result.Symbols : null,
                    references: fromMemory ? result.References : null
                );
            }

            await using var read = new RigDbContext(databasePath, pooling: false);
            var connection = read.Database.GetDbConnection();
            return (
                SymbolFts: await ReadColumnAsync(connection, "SELECT symbolid FROM symbol_fts;"),
                RefTargetFts: await ReadColumnAsync(connection, "SELECT symbolid FROM ref_target_fts;")
            );
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            { /* best-effort cleanup */
            }
        }
    }

    private static async Task<List<string>> ReadColumnAsync(DbConnection connection, string sql)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}

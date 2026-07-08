using Rig.Cli.CommandLine;
using Rig.Cli.Services;
using Rig.Domain.Data;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

// Smoke coverage for UnusedRefsQueryService — the shared data path behind `rig refs --usage` / `--unused` and
// the /api/refs/* web endpoints. Materializes an analyzed playground into an indexed per-commit store (mirrors
// ImpactTwoStoreTests.MaterializeStoreAsync) and asserts UsageAsync reads it back: the EntryPointEffects
// playground has first-party references, so the inbound-usage ranking must return at least one assembly row.
[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class UnusedRefsQueryServiceTests(AnalyzedPlaygrounds playgrounds)
{
    [Test]
    public async Task UsageAsync_returns_at_least_one_assembly_row()
    {
        var pg = await playgrounds.EntryPointEffectsAsync();
        var wd = NewWorkingDirectory();
        try
        {
            var storeId = await MaterializeStoreAsync(wd, pg.Result, storeId: "usagesmoke");

            var rows = await UnusedRefsQueryService.UsageAsync(wd, storeId);

            rows.ShouldNotBeNull();
            rows.Count.ShouldBeGreaterThanOrEqualTo(1);
            rows.ShouldAllBe(r => !string.IsNullOrEmpty(r.Assembly) && r.Refs >= 1);
        }
        finally
        {
            TryDelete(wd);
        }
    }

    private static string NewWorkingDirectory()
    {
        var wd = Path.Combine(Path.GetTempPath(), $"rig-refs-usage-{Guid.NewGuid():n}");
        Directory.CreateDirectory(wd);
        return wd;
    }

    private static async Task<string> MaterializeStoreAsync(string workingDirectory, AnalysisResult result, string storeId)
    {
        var dir = StoreLayout.NewStoreDir(workingDirectory, storeId);
        var db = Path.Combine(dir, StoreLayout.DbFileName);
        await using var ctx = new RigDbContext(db, pooling: false);
        await Writes.SaveAsync(ctx, result, provenance: null);
        return storeId;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a held SQLite handle must not fail the test.
        }
    }
}

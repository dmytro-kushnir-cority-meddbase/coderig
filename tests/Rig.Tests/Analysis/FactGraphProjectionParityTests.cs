using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// Guards the index-time perf shortcut: `rig index` materializes the call graph from the facts it just
// extracted (FactGraphProjection.FromAnalysis) instead of re-reading the whole fact store off disk
// (Reads.LoadFactGraphAsync, the standalone path). Both feed the SAME GraphMaterializer.BuildFromGraphAsync,
// so the only thing that can diverge is the graph SOURCE — this asserts the two build an identical graph
// from the same facts. If they ever drift, the persisted call_edges would diverge from what the in-memory
// oracle computes over a re-read store (the effect-path divergence). Keep the two projections in lockstep.
[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class FactGraphProjectionParityTests(AnalyzedPlaygrounds playgrounds)
{
    [Test]
    public async Task In_memory_projection_matches_the_store_read_projection()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var handoffRules = RuleSetLoader.Load(playground.WorkingDirectory).Handoff;

        var inMemory = FactGraphProjection.FromAnalysis(playground.Result, handoffRules);
        var fromStore = await LoadFromSavedStoreAsync(playground.Result, handoffRules);

        // Non-empty guard: a parity check over two empty graphs would pass vacuously.
        inMemory.CallEdges.Count.ShouldBeGreaterThan(0);
        inMemory.Methods.Count.ShouldBeGreaterThan(0);

        inMemory.CallEdges.ShouldBe(fromStore.CallEdges, ignoreOrder: true);
        inMemory.ImplementsEdges.ShouldBe(fromStore.ImplementsEdges, ignoreOrder: true);
        inMemory.BaseEdges!.ShouldBe(fromStore.BaseEdges!, ignoreOrder: true);
        inMemory.Methods.ShouldBe(fromStore.Methods, ignoreOrder: true);
        (inMemory.MinedDispatch ?? []).ShouldBe(fromStore.MinedDispatch ?? [], ignoreOrder: true);
    }

    private static async Task<FactGraphData> LoadFromSavedStoreAsync(AnalysisResult result, IReadOnlyList<FactHandoffRule> handoffRules)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rig-graphparity-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "rig.db");
        try
        {
            await using (var write = new RigDbContext(databasePath, pooling: false))
            {
                await Writes.SaveAsync(write, result);
            }

            await using var read = new RigDbContext(databasePath, pooling: false);
            return await Reads.LoadFactGraphAsync(read, handoffRules);
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
}

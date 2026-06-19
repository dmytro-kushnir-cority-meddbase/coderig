using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class MultiLineHandoffTests(AnalyzedPlaygrounds playgrounds)
{
    private const string RegisterSchedules = "SchedulerZoo.RegisterSchedules";

    [Test]
    [Arguments("EndOfTerm")]
    [Arguments("ReindexDirectory")]
    [Arguments("SweepCache")]
    [Arguments("FlushAudit")]
    [Arguments("PurgeNightly")]
    public async Task Multiline_dispatcher_delegate_is_classified_as_a_handoff(string callback)
    {
        var graph = await GraphAsync();

        var handoff = graph.CallEdges.FirstOrDefault(e => e.Kind == "handoff" && e.Callee.Contains(callback, StringComparison.Ordinal));
        handoff.ShouldNotBeNull($"{callback} should be classified as a handoff despite the multi-line layout");
        handoff!.HandoffDispatcher.ShouldNotBeNull();

        var sync = FactPathFinder.Reaches(graph, RegisterSchedules, maxDepth: 20);
        sync.Keys.ShouldNotContain(k => k.Contains(callback, StringComparison.Ordinal), $"{callback} must leave the sync tree");

        var async = FactPathFinder.ReachesWithFanout(
            graph,
            RegisterSchedules,
            maxDepth: 20,
            mode: FactPathFinder.TraversalMode.AsyncInclude
        );
        var hit = async.FirstOrDefault(kv => kv.Key.Contains(callback, StringComparison.Ordinal));
        hit.Key.ShouldNotBeNull($"--async should reach the scheduled {callback}");
        hit.Value.HandoffVia.ShouldNotBeNull($"the scheduled {callback} must carry HandoffVia provenance");
    }

    [Test]
    public async Task A_methodgroup_consumed_by_a_non_dispatcher_stays_synchronous()
    {
        var graph = await GraphAsync();

        graph.CallEdges.ShouldNotContain(
            e => e.Kind == "handoff" && e.Callee.Contains("SyncTransform", StringComparison.Ordinal),
            "SyncTransform's consumer is RunNow (not a dispatcher) — it must not be classified as a handoff"
        );

        var sync = FactPathFinder.Reaches(graph, RegisterSchedules, maxDepth: 20);
        sync.Keys.ShouldContain(k => k.Contains("SyncTransform", StringComparison.Ordinal), "SyncTransform should stay in the sync tree");
    }

    private async Task<FactGraphData> GraphAsync()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var rules = FactHandoffRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory);
        return FactProjection.GraphData(playground.Result, rules);
    }
}

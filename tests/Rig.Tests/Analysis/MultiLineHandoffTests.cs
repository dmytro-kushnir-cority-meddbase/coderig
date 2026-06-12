using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// Regression net for PROBLEM 1: a dispatcher registration written MULTI-LINE put the consuming `new`
// and the delegate method-group on DIFFERENT lines, so the old exact-same-line co-location never fired
// and the callback expanded as a synchronous call (AgedState.RegisterTermEndProcess → EndOfTerm). The
// fix mines a structural DelegateConsumer fact (the call/`new` a method-group is an argument to,
// resolved by ancestor walk — line-placement-agnostic), which the classifier matches. These tests
// drive the EXTRACTOR over SchedulerZoo's convoluted layouts (C1–C6), so they cover the resolution end
// to end on real source, the weirder the line placement the better.
[Collection(RoslynIntegrationCollection.Name)]
public sealed class MultiLineHandoffTests(AnalyzedPlaygrounds playgrounds)
{
    private readonly AnalyzedPlaygrounds _playgrounds = playgrounds;

    private const string RegisterSchedules = "SchedulerZoo.RegisterSchedules";

    // C1–C5: each callback is handed to a background-schedule dispatcher across a different multi-line
    // layout. All must classify as handoffs regardless of how the call is split across lines.
    [Theory]
    [InlineData("EndOfTerm")] // C1 — delegate on its own line below `new`
    [InlineData("ReindexDirectory")] // C2 — arg #1 split across lines, delegate several lines down
    [InlineData("SweepCache")] // C3 — `this.X` member-access method-group
    [InlineData("FlushAudit")] // C4 — dispatcher `new` nested in another call's argument list
    [InlineData("PurgeNightly")] // C5 — comment lines interleaved between `new` and the delegate
    public async Task Multiline_dispatcher_delegate_is_classified_as_a_handoff(string callback)
    {
        var graph = await GraphAsync();

        // The edge to the callback is reclassified to a handoff (a curated dispatcher consumed it).
        var handoff = graph.CallEdges.FirstOrDefault(e => e.Kind == "handoff" && e.Callee.Contains(callback, StringComparison.Ordinal));
        handoff.ShouldNotBeNull($"{callback} should be classified as a handoff despite the multi-line layout");
        handoff!.HandoffDispatcher.ShouldNotBeNull();

        // Sync-cut: the deferred callback (and its effect) is NOT synchronously reachable.
        var sync = FactPathFinder.Reaches(graph, RegisterSchedules, maxDepth: 20);
        sync.Keys.ShouldNotContain(k => k.Contains(callback, StringComparison.Ordinal), $"{callback} must leave the sync tree");

        // --async: it IS reachable, carrying handoff provenance.
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

    // C6 recall guard: a method-group handed to a NON-dispatcher helper, sitting right among the
    // dispatcher registrations, must STAY a synchronous call — the structural consumer (RunNow) does not
    // match any dispatcher, so it is never swept into a handoff.
    [Fact]
    public async Task A_methodgroup_consumed_by_a_non_dispatcher_stays_synchronous()
    {
        var graph = await GraphAsync();

        graph.CallEdges.ShouldNotContain(
            e => e.Kind == "handoff" && e.Callee.Contains("SyncTransform", StringComparison.Ordinal),
            "SyncTransform's consumer is RunNow (not a dispatcher) — it must not be classified as a handoff"
        );

        // It is still an ordinary method-group edge, so its body is synchronously reachable.
        var sync = FactPathFinder.Reaches(graph, RegisterSchedules, maxDepth: 20);
        sync.Keys.ShouldContain(k => k.Contains("SyncTransform", StringComparison.Ordinal), "SyncTransform should stay in the sync tree");
    }

    private async Task<FactGraphData> GraphAsync()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var rules = FactHandoffRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory);
        return FactProjection.GraphData(playground.Result, rules);
    }
}

using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class SqlReachabilityTests(AnalyzedPlaygrounds playgrounds)
{
    private const string RegisterSchedules = "SchedulerZoo.RegisterSchedules";
    private const string ProcessHealthcodeQueue = "SchedulerZoo.ProcessHealthcodeQueue";

    [Test]
    public async Task Sql_forward_reachability_matches_the_in_memory_oracle()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var graph = ShapedOracle(playground);

        await WithMaterializedStoreAsync(
            playground,
            async context =>
            {
                (await SqlReachability.HasGraphAsync(context)).ShouldBeTrue();

                var seeds = FactPathFinder
                    .AllDispatchEdges(graph)
                    .SelectMany(e => new[] { e.From, e.To })
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray();
                seeds.ShouldNotBeEmpty();

                foreach (var seed in seeds)
                {
                    var sql = await SqlReachability.ReachableSetAsync(context, [seed], SqlReachability.Direction.Forward);
                    var oracle = FactPathFinder.ReachableFromAll(graph, [seed]);
                    sql.ShouldBe(oracle, ignoreOrder: true, customMessage: $"forward reach mismatch for seed {seed}");
                }
            }
        );
    }

    [Test]
    public async Task Sql_reachability_traverses_dispatch_edges()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var graph = ShapedOracle(playground);

        var dispatchEdge = FactPathFinder.AllDispatchEdges(graph).FirstOrDefault();
        dispatchEdge.From.ShouldNotBeNull();

        await WithMaterializedStoreAsync(
            playground,
            async context =>
            {
                var reachable = await SqlReachability.ReachableSetAsync(context, [dispatchEdge.From], SqlReachability.Direction.Forward);
                reachable.ShouldContain(dispatchEdge.To, $"a call to {dispatchEdge.From} should dispatch to {dispatchEdge.To}");
            }
        );
    }

    [Test]
    public async Task Sql_reverse_reachability_recovers_a_known_caller()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var graph = ShapedOracle(playground);

        var callEdge = FactPathFinder.AllCallEdges(graph).First(e => e.Kind != EdgeKinds.Handoff);

        await WithMaterializedStoreAsync(
            playground,
            async context =>
            {
                var callers = await SqlReachability.ReachableSetAsync(context, [callEdge.To], SqlReachability.Direction.Reverse);
                callers.ShouldContain(callEdge.From, $"{callEdge.From} calls {callEdge.To}, so it must be in the reverse closure");
            }
        );
    }

    [Test]
    public async Task Sql_depth_reachability_matches_the_cha_oracle_per_mode_and_bounds_the_narrowed_oracle()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var graph = ShapedOracle(playground);
        const int maxDepth = 20;
        const FactPathFinder.TraversalMode SyncMode = FactPathFinder.TraversalMode.SyncCut;
        const FactPathFinder.TraversalMode AsyncMode = FactPathFinder.TraversalMode.AsyncInclude;

        var patterns = FactPathFinder
            .AllDispatchEdges(graph)
            .SelectMany(e => new[] { e.From, e.To })
            .Concat([RegisterSchedules, ProcessHealthcodeQueue])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        patterns.ShouldNotBeEmpty();

        await WithMaterializedStoreAsync(
            playground,
            async context =>
            {
                foreach (var pattern in patterns)
                foreach (var direction in new[] { SqlReachability.Direction.Reverse, SqlReachability.Direction.Forward })
                {
                    var includeHandoff = false;

                    var sqlSync = await SqlReachability.ReachedWithDepthAsync(context, pattern, direction, maxDepth, includeHandoff);
                    var oracleSync = OracleDepth(graph, pattern, direction, maxDepth, narrow: false, SyncMode);
                    Sorted(sqlSync).ShouldBe(Sorted(oracleSync), customMessage: $"sync {direction} mismatch for {pattern}");

                    var sqlAsync = await SqlReachability.ReachedWithDepthAsync(context, pattern, direction, maxDepth, includeHandoff: true);
                    var oracleAsync = OracleDepth(graph, pattern, direction, maxDepth, narrow: false, AsyncMode);
                    Sorted(sqlAsync).ShouldBe(Sorted(oracleAsync), customMessage: $"async {direction} mismatch for {pattern}");

                    foreach (var key in OracleDepth(graph, pattern, direction, maxDepth, narrow: true, SyncMode).Keys)
                    {
                        sqlSync.Keys.ShouldContain(key, $"narrowed sync {direction} {key} must be within the SQL superset for {pattern}");
                    }

                    foreach (var key in sqlSync.Keys)
                    {
                        sqlAsync.Keys.ShouldContain(key, $"sync ⊆ async violated for {pattern} {direction} at {key}");
                    }
                }
            }
        );
    }

    [Test]
    public async Task Bounded_graph_reproduces_full_graph_reach_in_both_modes()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var full = ShapedOracle(playground);

        await WithMaterializedStoreAsync(
            playground,
            async context =>
            {
                foreach (var mode in new[] { FactPathFinder.TraversalMode.SyncCut, FactPathFinder.TraversalMode.AsyncInclude })
                foreach (var pattern in new[] { RegisterSchedules, ProcessHealthcodeQueue })
                {
                    var bounded = await SqlReachability.LoadBoundedGraphAsync(context, pattern, SqlReachability.Direction.Forward);
                    var boundedReach = FactPathFinder.Reaches(bounded, pattern, maxDepth: 20, mode: mode);
                    var fullReach = FactPathFinder.Reaches(full, pattern, maxDepth: 20, mode: mode);
                    Sorted(MapToOne(boundedReach))
                        .ShouldBe(Sorted(MapToOne(fullReach)), customMessage: $"bounded != full for {pattern} mode {mode}");
                }
            }
        );
    }

    [Test]
    public async Task Handoff_callback_leaves_sync_reach_and_returns_under_async()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var rules = HandoffRules(playground);
        var graph = FactProjection.GraphData(playground.Result, rules);

        var handoffEdge = graph.CallEdges.FirstOrDefault(e =>
            e.Kind == "handoff" && e.Callee.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal)
        );
        handoffEdge.ShouldNotBeNull("the BPS method-group should be classified as a handoff");
        handoffEdge!.HandoffDispatcher.ShouldNotBeNull();

        var sync = FactPathFinder.Reaches(graph, RegisterSchedules, maxDepth: 20);
        sync.Keys.ShouldNotContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));

        var asyncReach = FactPathFinder.ReachesWithFanout(
            graph,
            RegisterSchedules,
            maxDepth: 20,
            mode: FactPathFinder.TraversalMode.AsyncInclude
        );
        var hit = asyncReach.FirstOrDefault(kv => kv.Key.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));
        hit.Key.ShouldNotBeNull("--async should reach the scheduled callback");
        hit.Value.HandoffVia.ShouldNotBeNull("the scheduled reach must carry HandoffVia provenance");

        var roots = FactPathFinder.EntryRootsReaching(graph, ProcessHealthcodeQueue, maxDepth: 20);
        roots.ShouldContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));
    }

    [Test]
    public async Task Sql_callers_roots_surfaces_the_background_origin()
    {
        var playground = await playgrounds.LegacyNet48Async();

        await WithMaterializedStoreAsync(
            playground,
            async context =>
            {
                var syncFwd = await SqlReachability.ReachedWithDepthAsync(
                    context,
                    RegisterSchedules,
                    SqlReachability.Direction.Forward,
                    maxDepth: 20,
                    includeHandoff: false
                );
                syncFwd.Keys.ShouldNotContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));

                var asyncFwd = await SqlReachability.ReachedWithDepthAsync(
                    context,
                    RegisterSchedules,
                    SqlReachability.Direction.Forward,
                    maxDepth: 20,
                    includeHandoff: true
                );
                asyncFwd.Keys.ShouldContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));

                var roots = await SqlReachability.EntryRootsReachingAsync(
                    context,
                    ProcessHealthcodeQueue,
                    maxDepth: 20,
                    includeHandoff: false
                );
                roots.ShouldContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));
            }
        );
    }

    [Test]
    public async Task Dead_code_keeps_all_handoff_targets_as_roots()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var rules = HandoffRules(playground);
        var graph = FactProjection.GraphData(playground.Result, rules);

        var handoffTargets = graph
            .CallEdges.Where(e => e.Kind is EdgeKinds.MethodGroup or EdgeKinds.Handoff)
            .Select(e => e.Callee)
            .ToHashSet(StringComparer.Ordinal);

        handoffTargets.ShouldContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));
        handoffTargets.ShouldContain(k => k.Contains("BackgroundWork", StringComparison.Ordinal));
    }

    private static IReadOnlyList<FactHandoffRule> HandoffRules(AnalyzedPlayground playground) =>
        FactHandoffRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory);

    private static IReadOnlyDictionary<string, int> OracleDepth(
        FactGraphData graph,
        string pattern,
        SqlReachability.Direction direction,
        int maxDepth,
        bool narrow,
        FactPathFinder.TraversalMode mode
    ) =>
        direction == SqlReachability.Direction.Forward
            ? FactPathFinder.Reaches(graph, pattern, maxDepth, narrowDispatch: narrow, mode: mode)
            : FactPathFinder.ReachedBy(graph, pattern, maxDepth, narrowDispatch: narrow, mode: mode);

    private static IReadOnlyDictionary<string, int> MapToOne(IReadOnlyDictionary<string, int> map) =>
        map.ToDictionary(kv => kv.Key, _ => 0, StringComparer.Ordinal);

    private static (string, int)[] Sorted(IReadOnlyDictionary<string, int> map) =>
        map.Select(kv => (kv.Key, kv.Value)).OrderBy(t => t.Key, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<FactGenericFactoryRule> FactoryRules(AnalyzedPlayground playground) =>
        FactGenericFactoryRuleProvider.LoadForWorkingDirectory(playground.WorkingDirectory);

    // The in-memory oracle the SQL views are compared against: handoff-classified (FactProjection) AND
    // generic-factory-rewritten, mirroring exactly what GraphMaterializer now bakes into call_edges.
    private static FactGraphData ShapedOracle(AnalyzedPlayground playground) =>
        FactPathFinder.RewriteGenericFactories(
            FactProjection.GraphData(playground.Result, HandoffRules(playground)),
            FactoryRules(playground)
        );

    private static async Task WithMaterializedStoreAsync(AnalyzedPlayground playground, Func<RigDbContext, Task> assert)
    {
        var rules = HandoffRules(playground).ToArray();
        var factoryRules = FactoryRules(playground);
        var directory = Path.Combine(Path.GetTempPath(), "rig-sqlreach-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "rig.db");
        try
        {
            await using (var write = new RigDbContext(databasePath, pooling: false))
            {
                await Writes.SaveAsync(write, playground.Result);
            }

            await using (var build = new RigDbContext(databasePath, pooling: false))
            {
                await GraphMaterializer.BuildAsync(build, rules, factoryRules: factoryRules);
            }

            await using (var read = new RigDbContext(databasePath, pooling: false))
            {
                await assert(read);
            }
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

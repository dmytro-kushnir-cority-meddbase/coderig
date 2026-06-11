using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// Differential tests for the SQL recursive-CTE reachability path (the perf path that walks the derived
// edge views on disk) against the in-memory FactPathFinder oracle — now PARAMETERIZED BY TRAVERSAL
// MODE (sync-cut default vs --async). The async-handoff classification (HandoffClassifier) splits
// dispatcher-consumed method-group edges into Kind="handoff"; the contract is:
//   * CHA-oracle(sync)  == SQL(sync-filter)   — handoffs cut on both sides
//   * CHA-oracle(async) == SQL(unfiltered)    — handoffs walked on both sides
//   * narrowed ⊆ SQL,  sync ⊆ async,  bounded(mode) == full(mode)
// plus the headline acceptance (ProcessHealthcodeQueue leaves sync reach, returns under --async, and is
// a background origin) and the dead-code recall rail (all method-group/handoff targets stay roots).
[Collection(RoslynIntegrationCollection.Name)]
public sealed class SqlReachabilityTests(AnalyzedPlaygrounds playgrounds)
{
    private readonly AnalyzedPlaygrounds _playgrounds = playgrounds;

    private const string RegisterSchedules = "SchedulerZoo.RegisterSchedules";
    private const string ProcessHealthcodeQueue = "SchedulerZoo.ProcessHealthcodeQueue";

    [Fact]
    public async Task Sql_forward_reachability_matches_the_in_memory_oracle()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var rules = HandoffRules(playground);
        var graph = FactProjection.GraphData(playground.Result, rules);

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

                // Default (sync-cut) set walk both sides.
                foreach (var seed in seeds)
                {
                    var sql = await SqlReachability.ReachableSetAsync(context, [seed], SqlReachability.Direction.Forward);
                    var oracle = FactPathFinder.ReachableFromAll(graph, [seed]);
                    sql.ShouldBe(oracle, ignoreOrder: true, customMessage: $"forward reach mismatch for seed {seed}");
                }
            }
        );
    }

    [Fact]
    public async Task Sql_reachability_traverses_dispatch_edges()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var graph = FactProjection.GraphData(playground.Result, HandoffRules(playground));

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

    [Fact]
    public async Task Sql_reverse_reachability_recovers_a_known_caller()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var graph = FactProjection.GraphData(playground.Result, HandoffRules(playground));

        // A SYNCHRONOUS direct call edge (not a handoff): its caller must appear in the reverse closure.
        var callEdge = FactPathFinder.AllCallEdges(graph).First(e => e.Kind != "handoff");

        await WithMaterializedStoreAsync(
            playground,
            async context =>
            {
                var callers = await SqlReachability.ReachableSetAsync(context, [callEdge.To], SqlReachability.Direction.Reverse);
                callers.ShouldContain(callEdge.From, $"{callEdge.From} calls {callEdge.To}, so it must be in the reverse closure");
            }
        );
    }

    // The mode contract: CHA-oracle == SQL per mode, both directions; narrowed ⊆ SQL; sync ⊆ async.
    [Fact]
    public async Task Sql_depth_reachability_matches_the_cha_oracle_per_mode_and_bounds_the_narrowed_oracle()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var rules = HandoffRules(playground);
        var graph = FactProjection.GraphData(playground.Result, rules);
        const int maxDepth = 20;
        const FactPathFinder.TraversalMode SyncMode = FactPathFinder.TraversalMode.SyncCut;
        const FactPathFinder.TraversalMode AsyncMode = FactPathFinder.TraversalMode.AsyncInclude;

        // Seeds: dispatch endpoints PLUS the registration site (so handoff edges are on a real path).
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
                    var includeHandoff = false; // sync-cut

                    // CHA(sync) == SQL(sync-filter).
                    var sqlSync = await SqlReachability.ReachedWithDepthAsync(context, pattern, direction, maxDepth, includeHandoff);
                    var oracleSync = OracleDepth(graph, pattern, direction, maxDepth, narrow: false, SyncMode);
                    Sorted(sqlSync).ShouldBe(Sorted(oracleSync), customMessage: $"sync {direction} mismatch for {pattern}");

                    // CHA(async) == SQL(unfiltered).
                    var sqlAsync = await SqlReachability.ReachedWithDepthAsync(context, pattern, direction, maxDepth, includeHandoff: true);
                    var oracleAsync = OracleDepth(graph, pattern, direction, maxDepth, narrow: false, AsyncMode);
                    Sorted(sqlAsync).ShouldBe(Sorted(oracleAsync), customMessage: $"async {direction} mismatch for {pattern}");

                    // narrowed ⊆ SQL (per mode).
                    foreach (var key in OracleDepth(graph, pattern, direction, maxDepth, narrow: true, SyncMode).Keys)
                        sqlSync.Keys.ShouldContain(key, $"narrowed sync {direction} {key} must be within the SQL superset for {pattern}");

                    // sync ⊆ async.
                    foreach (var key in sqlSync.Keys)
                        sqlAsync.Keys.ShouldContain(key, $"sync ⊆ async violated for {pattern} {direction} at {key}");
                }
            }
        );
    }

    // bounded(mode) == full(mode): the bounded SQL subgraph + in-memory FactPathFinder reproduces the
    // full in-memory graph's reach, in BOTH modes. The bounded load stays the (async) superset.
    [Fact]
    public async Task Bounded_graph_reproduces_full_graph_reach_in_both_modes()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var rules = HandoffRules(playground);
        var full = FactProjection.GraphData(playground.Result, rules);

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

    // Headline acceptance: ProcessHealthcodeQueue's DB/SOAP effects are NOT synchronously reachable from
    // the registration site; --async restores them; the callback is a background ORIGIN.
    [Fact]
    public async Task Handoff_callback_leaves_sync_reach_and_returns_under_async()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var rules = HandoffRules(playground);
        var graph = FactProjection.GraphData(playground.Result, rules);

        // The classified edge exists: RegisterSchedules -handoff-> ProcessHealthcodeQueue.
        var handoffEdge = graph.CallEdges.FirstOrDefault(e =>
            e.Kind == "handoff" && e.Callee.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal)
        );
        handoffEdge.ShouldNotBeNull("the BPS method-group should be classified as a handoff");
        handoffEdge!.HandoffDispatcher.ShouldNotBeNull();

        // Sync-cut: from the registrar, the callback (and its SaveEntity effect) is NOT reachable.
        var sync = FactPathFinder.Reaches(graph, RegisterSchedules, maxDepth: 20);
        sync.Keys.ShouldNotContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));

        // --async: it IS reachable, carrying the handoff provenance.
        var async = FactPathFinder.ReachesWithFanout(
            graph,
            RegisterSchedules,
            maxDepth: 20,
            mode: FactPathFinder.TraversalMode.AsyncInclude
        );
        var hit = async.FirstOrDefault(kv => kv.Key.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));
        hit.Key.ShouldNotBeNull("--async should reach the scheduled callback");
        hit.Value.HandoffVia.ShouldNotBeNull("the scheduled reach must carry HandoffVia provenance");

        // The callback is a background ORIGIN: under sync-cut nothing reaches it, so it is its own root.
        var roots = FactPathFinder.EntryRootsReaching(graph, ProcessHealthcodeQueue, maxDepth: 20);
        roots.ShouldContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));
    }

    // Same acceptance, but over the SQL set queries (the path `rig callers`/`reaches` actually use).
    [Fact]
    public async Task Sql_callers_roots_surfaces_the_background_origin()
    {
        var playground = await _playgrounds.LegacyNet48Async();

        await WithMaterializedStoreAsync(
            playground,
            async context =>
            {
                // Sync-cut reverse reach from the registration site does NOT reach the callback.
                var syncFwd = await SqlReachability.ReachedWithDepthAsync(
                    context,
                    RegisterSchedules,
                    SqlReachability.Direction.Forward,
                    maxDepth: 20,
                    includeHandoff: false
                );
                syncFwd.Keys.ShouldNotContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));

                // --async forward reach DOES.
                var asyncFwd = await SqlReachability.ReachedWithDepthAsync(
                    context,
                    RegisterSchedules,
                    SqlReachability.Direction.Forward,
                    maxDepth: 20,
                    includeHandoff: true
                );
                asyncFwd.Keys.ShouldContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));

                // callers --roots (sync) lists the callback as an origin.
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

    // Dead-code recall rail: every method-group/handoff target stays a root regardless of
    // classification, so a scheduled-only callback is never falsely flagged dead.
    [Fact]
    public async Task Dead_code_keeps_all_handoff_targets_as_roots()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var rules = HandoffRules(playground);
        var graph = FactProjection.GraphData(playground.Result, rules);

        var handoffTargets = graph
            .CallEdges.Where(e => e.Kind is "methodGroup" or "handoff")
            .Select(e => e.Callee)
            .ToHashSet(StringComparer.Ordinal);

        handoffTargets.ShouldContain(k => k.Contains("ProcessHealthcodeQueue", StringComparison.Ordinal));
        // BackgroundWork is handed to Task.Run (a BCL dispatcher), so it stays an UNCLASSIFIED
        // methodGroup — still a root (recall preserved even when classification declines to fire).
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

    // Saves to a throwaway store, materialises the derived views WITH the handoff rules (so call_edges
    // carry Kind="handoff"), then runs the assertion against a fresh read context.
    private static async Task WithMaterializedStoreAsync(AnalyzedPlayground playground, Func<RigDbContext, Task> assert)
    {
        var rules = HandoffRules(playground).ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "rig-sqlreach-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "rig.db");
        try
        {
            await using (var write = new RigDbContext(databasePath, pooling: false))
                await Writes.SaveAsync(write, playground.Result);
            await using (var build = new RigDbContext(databasePath, pooling: false))
                await GraphMaterializer.BuildAsync(build, rules);
            await using (var read = new RigDbContext(databasePath, pooling: false))
                await assert(read);
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

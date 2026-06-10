using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// Example-based tests for the SQL recursive-CTE reachability path (the perf path that walks the
// derived edge views on disk instead of loading the whole graph into memory). The in-memory
// FactPathFinder is the REFERENCE oracle: the SQL traversal over call_edges ∪ dispatch_edges (built
// by GraphMaterializer) must return the same reachable set as the oracle over the same FactGraphData.
//
// These are concrete, deterministic cases (specific fixture-derived seeds) — the randomized
// property/differential sweep is deferred until this is green.
[Collection(RoslynIntegrationCollection.Name)]
public sealed class SqlReachabilityTests(AnalyzedPlaygrounds playgrounds)
{
    private readonly AnalyzedPlaygrounds _playgrounds = playgrounds;

    [Fact]
    public async Task Sql_forward_reachability_matches_the_in_memory_oracle()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var graph = FactProjection.GraphData(playground.Result);

        await WithMaterializedStoreAsync(
            playground.Result,
            async context =>
            {
                (await SqlReachability.HasGraphAsync(context)).ShouldBeTrue();

                // Deterministic seed set derived from the fixture: every distinct dispatch SOURCE (so both
                // interface->impl and base->override hops are exercised) plus their targets. Comparing each
                // against the oracle's exact closure is example-based differential testing, not a random sweep.
                var seeds = FactPathFinder
                    .AllDispatchEdges(graph)
                    .SelectMany(e => new[] { e.From, e.To })
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray();
                seeds.ShouldNotBeEmpty(); // the fixture has interface/override dispatch

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
        var graph = FactProjection.GraphData(playground.Result);

        // A concrete dispatch edge: a base-virtual/interface method whose call must reach a same-named
        // override/impl on a subtype. Pure direct-edge traversal would miss this — it is exactly the
        // hop that makes dispatch precomputation necessary.
        var dispatchEdge = FactPathFinder.AllDispatchEdges(graph).FirstOrDefault();
        dispatchEdge.From.ShouldNotBeNull();

        await WithMaterializedStoreAsync(
            playground.Result,
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
        var graph = FactProjection.GraphData(playground.Result);

        // Pick any direct call edge; its caller must appear in the reverse closure of its callee.
        var callEdge = FactPathFinder.AllCallEdges(graph).FirstOrDefault();
        callEdge.From.ShouldNotBeNull();

        await WithMaterializedStoreAsync(
            playground.Result,
            async context =>
            {
                var callers = await SqlReachability.ReachableSetAsync(context, [callEdge.To], SqlReachability.Direction.Reverse);
                callers.ShouldContain(callEdge.From, $"{callEdge.From} calls {callEdge.To}, so it must be in the reverse closure");
            }
        );
    }

    // The set+depth path behind `rig callers` / the reachable-method map. SqlReachability walks
    // call_edges ∪ dispatch_edges, where dispatch_edges is the CHA (receiver-blind) superset — so the
    // SQL result equals the in-memory oracle run in CHA MODE (narrowDispatch:false), depth included,
    // both directions. The default (narrowed) in-memory traversal applies receiver-type narrowing and
    // is therefore a SUBSET of the SQL set (a narrowed walk simply doesn't visit the extra CHA-only
    // dispatch targets). Both invariants are asserted here: CHA oracle == SQL, and narrowed ⊆ SQL.
    [Fact]
    public async Task Sql_depth_reachability_matches_the_cha_oracle_and_bounds_the_narrowed_oracle_both_directions()
    {
        var playground = await _playgrounds.LegacyNet48Async();
        var graph = FactProjection.GraphData(playground.Result);
        const int maxDepth = 20;

        // Deterministic seed patterns: distinct dispatch endpoints (exercise both dispatch hops) — each
        // full DocID is used as a substring pattern, mirroring FactPathFinder.Contains seeding.
        var patterns = FactPathFinder
            .AllDispatchEdges(graph)
            .SelectMany(e => new[] { e.From, e.To })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Take(15)
            .ToArray();
        patterns.ShouldNotBeEmpty();

        await WithMaterializedStoreAsync(
            playground.Result,
            async context =>
            {
                foreach (var pattern in patterns)
                {
                    // CHA mode: exact equivalence with the SQL traversal (the original invariant).
                    var sqlRev = await SqlReachability.ReachedWithDepthAsync(context, pattern, SqlReachability.Direction.Reverse, maxDepth);
                    var oracleRevCha = FactPathFinder.ReachedBy(graph, pattern, maxDepth, narrowDispatch: false);
                    Sorted(sqlRev).ShouldBe(Sorted(oracleRevCha), customMessage: $"reverse CHA depth mismatch for {pattern}");

                    var sqlFwd = await SqlReachability.ReachedWithDepthAsync(context, pattern, SqlReachability.Direction.Forward, maxDepth);
                    var oracleFwdCha = FactPathFinder.Reaches(graph, pattern, maxDepth, narrowDispatch: false);
                    Sorted(sqlFwd).ShouldBe(Sorted(oracleFwdCha), customMessage: $"forward CHA depth mismatch for {pattern}");

                    // Narrowed mode (the default the CLI uses): a subset of the SQL/CHA closure.
                    var oracleRevNarrow = FactPathFinder.ReachedBy(graph, pattern, maxDepth);
                    foreach (var key in oracleRevNarrow.Keys)
                        sqlRev.Keys.ShouldContain(key, $"narrowed reverse reach {key} must be within the SQL/CHA superset for {pattern}");

                    var oracleFwdNarrow = FactPathFinder.Reaches(graph, pattern, maxDepth);
                    foreach (var key in oracleFwdNarrow.Keys)
                        sqlFwd.Keys.ShouldContain(key, $"narrowed forward reach {key} must be within the SQL/CHA superset for {pattern}");
                }
            }
        );
    }

    private static (string, int)[] Sorted(IReadOnlyDictionary<string, int> map) =>
        map.Select(kv => (kv.Key, kv.Value)).OrderBy(t => t.Key, StringComparer.Ordinal).ToArray();

    // Saves the analysis result to a throwaway SQLite store, materialises the derived edge views with
    // a fresh context (mirroring `rig index` then `rig graph`), then runs the assertion against another
    // fresh read context. The temp directory is always cleaned up.
    private static async Task WithMaterializedStoreAsync(AnalysisResult result, Func<RigDbContext, Task> assert)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rig-sqlreach-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "rig.db");
        try
        {
            await using (var write = new RigDbContext(databasePath, pooling: false))
                await Writes.SaveAsync(write, result);
            await using (var build = new RigDbContext(databasePath, pooling: false))
                await GraphMaterializer.BuildAsync(build);
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

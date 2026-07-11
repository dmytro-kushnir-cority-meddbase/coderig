using Rig.Cli;
using Rig.Cli.Caching;
using Rig.Cli.CommandLine;
using Rig.Cli.Commands;
using Rig.Cli.Impact;
using Rig.Domain.Data;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

// The `rig impact` result-cache guardrail (docs: caching-approach quorum, 2026-06-19). `impact` diffs two
// IMMUTABLE per-commit stores, so the whole diff is a pure function of (baseStoreKey, headStoreKey,
// rulesHash, mode); it is cached in the HEAD store's cache.db and replayed on a re-run. These tests are the
// correctness contract the panel REQUIRED before the cache could land:
//   (1) a warm (cache-hit) render is BYTE-IDENTICAL to a cold (--no-cache) recompute, across the whole
//       render-flag matrix (--structural, --format tsv, --limit) — proving the artifact fully materializes
//       the FQN labels + structural-cause data and that render-only flags don't fragment the key;
//   (2) the traversal MODE is part of the key — an --async run is never served the sync blob;
//   (3) reindexing a side (new rig.db identity) INVALIDATES the entry — a content change is never masked
//       by a stale hit.
[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class ImpactCacheTests(AnalyzedPlaygrounds playgrounds)
{
    // (1) For a fixed (base, head) pair, every render-flag combination served WARM (from the single cached
    // artifact written by the first run) matches the COLD --no-cache recompute byte-for-byte. The artifact is
    // keyed on neither --structural nor --format nor --limit, so one blob must render all of them correctly.
    [Test]
    public async Task Warm_render_is_byte_identical_to_cold_recompute_across_the_flag_matrix()
    {
        var headPg = await playgrounds.EntryPointEffectsAsync();
        var basePg = await playgrounds.LegacyNet48Async();
        var wd = NewWorkingDirectory();
        try
        {
            var baseId = await MaterializeStoreAsync(wd, basePg.Result, storeId: "warmbase", provenance: null);
            var headId = await MaterializeStoreAsync(wd, headPg.Result, storeId: "warmhead", provenance: null);

            // Populate the cache once (the default sync, human-format run) — a miss that computes + stores.
            (await RunImpactAsync(wd, baseId, headId)).exit.ShouldBe(0);

            string[][] renderCombos =
            [
                [],
                ["--structural"],
                ["--format", "tsv"],
                ["--limit", "1"],
                ["--limit", "1000"],
                ["--structural", "--limit", "2"],
            ];

            foreach (var combo in renderCombos)
            {
                var cold = await RunImpactAsync(wd, baseId, headId, ["--no-cache", .. combo]);
                var warm = await RunImpactAsync(wd, baseId, headId, combo);

                cold.exit.ShouldBe(0);
                warm.exit.ShouldBe(0);
                // The whole point: a cache HIT renders the exact bytes a fresh recompute would.
                warm.output.ShouldBe(cold.output, customMessage: $"warm != cold for flags [{string.Join(' ', combo)}]");
                warm.output.ShouldNotBeEmpty();
            }
        }
        finally
        {
            TryDelete(wd);
        }
    }

    // (2) The traversal mode is keyed. After the SYNC artifact is cached, an --async run must NOT be served the
    // sync blob: its cached output equals its own --no-cache recompute (proving a distinct key / fresh compute),
    // and the warm async re-run is stable.
    [Test]
    public async Task Async_run_is_not_served_the_sync_artifact()
    {
        var headPg = await playgrounds.EntryPointEffectsAsync();
        var basePg = await playgrounds.LegacyNet48Async();
        var wd = NewWorkingDirectory();
        try
        {
            var baseId = await MaterializeStoreAsync(wd, basePg.Result, storeId: "modebase", provenance: null);
            var headId = await MaterializeStoreAsync(wd, headPg.Result, storeId: "modehead", provenance: null);

            // Cache the SYNC artifact.
            (await RunImpactAsync(wd, baseId, headId)).exit.ShouldBe(0);

            var asyncCold = await RunImpactAsync(wd, baseId, headId, ["--no-cache", "--async"]);
            var asyncWarmMiss = await RunImpactAsync(wd, baseId, headId, ["--async"]); // distinct mode key => miss => compute
            var asyncWarmHit = await RunImpactAsync(wd, baseId, headId, ["--async"]); // now a hit

            asyncCold.exit.ShouldBe(0);
            // Both the first async run (which must NOT pick up the sync blob) and the subsequent async hit
            // render exactly the fresh async recompute.
            asyncWarmMiss.output.ShouldBe(asyncCold.output);
            asyncWarmHit.output.ShouldBe(asyncCold.output);
        }
        finally
        {
            TryDelete(wd);
        }
    }

    // (3) Reindexing a side changes its rig.db identity (size/mtime) => a new StoreKey => a new cache key AND a
    // store_key-purge on the next cache open. A content change on the HEAD store therefore can NEVER be masked
    // by the stale prior hit: after re-materializing HEAD with the SAME content as BASE, the diff collapses to
    // empty, exactly as a --no-cache recompute reports — not the prior non-empty diff.
    [Test]
    public async Task Reindexing_a_store_invalidates_the_cached_diff()
    {
        var differentPg = await playgrounds.EntryPointEffectsAsync();
        var basePg = await playgrounds.LegacyNet48Async();
        var wd = NewWorkingDirectory();
        try
        {
            var baseId = await MaterializeStoreAsync(wd, basePg.Result, storeId: "invbase", provenance: null);
            var headId = await MaterializeStoreAsync(wd, differentPg.Result, storeId: "invhead", provenance: null);

            // Cold-populate: HEAD != BASE, so the diff is non-empty and gets cached.
            var before = await RunImpactAsync(wd, baseId, headId);
            before.exit.ShouldBe(0);
            before.output.ShouldNotContain("+0/-0 entry point(s)"); // genuinely different stores

            // Re-index HEAD with the SAME content as BASE. A real reindex publishes a FRESH rig.db, so delete
            // the current one first (its cache.db stays — that's the point: a stale sibling cache must not serve
            // a hit). The store id is unchanged, so --head still resolves it, but its rig.db identity — and thus
            // the cache key, plus the store_key purge on the next cache open — shifts.
            ReindexHeadAsync(wd, storeId: "invhead");
            await MaterializeStoreAsync(wd, basePg.Result, storeId: "invhead", provenance: null);

            var afterWarm = await RunImpactAsync(wd, baseId, headId);
            var afterCold = await RunImpactAsync(wd, baseId, headId, ["--no-cache"]);

            afterWarm.exit.ShouldBe(0);
            // The stale non-empty diff must NOT survive the reindex: identical content => empty diff, matching
            // the --no-cache recompute exactly.
            afterWarm.output.ShouldBe(afterCold.output);
            afterWarm.output.ShouldContain("+0/-0 entry point(s)");
            afterWarm.output.ShouldContain("no entry point's reachable-effect set changed");
        }
        finally
        {
            TryDelete(wd);
        }
    }

    // (4) The HAZARD DELTA round-trips through the codec: a per-EP footprint delta carrying HazardsAdded /
    // HazardsRemoved encodes + decodes to an equal delta, so the warm (cache-replayed) path renders the hazard
    // lines + headline byte-identically to a cold recompute. Mirrors the SharedMutationOnPath round-trip.
    [Test]
    public async Task Hazard_delta_round_trips_through_the_codec()
    {
        var prov = new StoreProvenance(Branch: "b", ShortCommit: "abc123", Fallback: "id");
        var delta = new EpFootprintDelta(
            Kind: "http",
            Route: "x",
            FilePath: "/x.cs",
            Line: 1,
            BranchEffects: 1,
            BaseEffects: 1,
            Added: [],
            Removed: [],
            Amplified: [],
            SharedMutationOnPath: false,
            HazardsAdded: [new HazardFinding(Type: "race_window", Cell: "N.T._status", Enclosing: "N.T.M", Confidence: "high")],
            HazardsRemoved: [new HazardFinding(Type: "n_plus_1", Cell: "id", Enclosing: "N.T.Q", Confidence: "high")]
        );
        var diff = new ImpactDiff(Ep: null, AffectedEps: [], PerEp: [delta]);

        var blob = ImpactCacheCodec.Encode(
            diff: diff,
            baseProvenance: prov,
            headProvenance: prov,
            idBySite: new Dictionary<(string File, int Line), string>()
        );
        var art = ImpactCacheCodec.Decode(blob);

        art.ShouldNotBeNull();
        art!.Diff.PerEp.Count.ShouldBe(1);
        var rt = art.Diff.PerEp[0];
        rt.HazardsAddedOrEmpty.ShouldBe(delta.HazardsAdded!);
        rt.HazardsRemovedOrEmpty.ShouldBe(delta.HazardsRemoved!);
        await Task.CompletedTask;
    }

    private static async Task<(int exit, string output)> RunImpactAsync(
        string workingDirectory,
        string baseId,
        string headId,
        string[]? extraArgs = null
    )
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = ["impact", "--base", baseId, "--head", headId, .. (extraArgs ?? [])];
        var exit = await CliApplication.RunAsync(args, output, error, workingDirectory);
        return (exit, output.ToString());
    }

    // Simulate the rig.db half of a reindex: drop the store's rig.db (+ WAL/SHM) so the next MaterializeStore
    // writes a fresh one with a new identity, while DELIBERATELY leaving cache.db behind (the stale-hit risk).
    // The just-finished impact run leaves a POOLED read-only SQLite handle on rig.db (Microsoft.Data.Sqlite
    // pools by default), so clear the pools + finalize before deleting, then retry briefly — a Windows-only
    // handle-release race, not a product concern.
    private static void ReindexHeadAsync(string workingDirectory, string storeId)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var dir = Path.Combine(workingDirectory, StoreLayout.RigDirName, storeId);
        foreach (var name in new[] { StoreLayout.DbFileName, StoreLayout.DbFileName + "-wal", StoreLayout.DbFileName + "-shm" })
        {
            var path = Path.Combine(dir, name);
            for (var attempt = 0; File.Exists(path); attempt++)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    private static string NewWorkingDirectory()
    {
        var wd = Path.Combine(Path.GetTempPath(), $"rig-impact-cache-{Guid.NewGuid():n}");
        Directory.CreateDirectory(wd);
        return wd;
    }

    // Materialize an analysis result into an indexed per-commit store (.rig/<storeId>/rig.db). Re-calling with
    // an existing storeId rewrites that store's rig.db in place (a reindex) — used by the invalidation test.
    private static async Task<string> MaterializeStoreAsync(
        string workingDirectory,
        AnalysisResult result,
        string storeId,
        GitProvenance? provenance
    )
    {
        var dir = StoreLayout.NewStoreDir(workingDirectory, storeId);
        var db = Path.Combine(dir, StoreLayout.DbFileName);
        await using var ctx = new RigDbContext(db, pooling: false);
        await Writes.SaveAsync(ctx, result, provenance: provenance);
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
            // Best-effort temp cleanup; a held SQLite handle on a CI box must not fail the test.
        }
    }
}

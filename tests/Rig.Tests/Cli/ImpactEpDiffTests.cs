using Rig.Analysis.Rules;
using Rig.Cli.Commands;
using Rig.Cli.EntryPoints;
using Rig.Domain.Data;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Cli;

// The two-store entry-point diff behind `impact --base` (docs/design-impact-behavioral-diff.md §3.1-3.2):
// derive EPs on the branch and base stores, set-diff on (Kind, Route). Identical content => empty diff
// (the formatting/no-op-immunity guarantee); genuinely different sources => the symmetric difference shows.
[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class ImpactEpDiffTests(AnalyzedPlaygrounds playgrounds)
{
    [Test]
    public async Task Identical_stores_produce_an_empty_entry_point_diff()
    {
        var pg = await playgrounds.EntryPointEffectsAsync();
        var (branchDb, baseDb, wd) = await TwoStoresAsync(pg.Result, pg.Result);

        var diff = await DiffAsync(branchDb, baseDb, wd);

        diff.Added.ShouldBeEmpty();
        diff.Removed.ShouldBeEmpty();
    }

    [Test]
    public async Task Different_sources_surface_added_and_removed_entry_points()
    {
        var branchPg = await playgrounds.EntryPointEffectsAsync();
        var basePg = await playgrounds.LegacyNet48Async();
        var (branchDb, baseDb, wd) = await TwoStoresAsync(branchPg.Result, basePg.Result);

        var diff = await DiffAsync(branchDb, baseDb, wd);

        // Each solution has entry points the other lacks → the symmetric difference is non-empty both ways.
        diff.Added.ShouldNotBeEmpty();
        diff.Removed.ShouldNotBeEmpty();
    }

    private static async Task<ImpactCommand.EpDiff> DiffAsync(string branchDb, string baseDb, string wd)
    {
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(wd, []);
        await using var branchCtx = new RigDbContext(branchDb, pooling: false, readOnly: true);
        var branchEpData = await Reads.LoadFactEntryPointDataAsync(branchCtx);
        var branchSet = await EntryPointContext.DeriveEntryPointsAsync(branchCtx, branchEpData, wd, [], handoffRules);
        var branchEps = branchSet.Derived.Concat(branchSet.PromotedOrigins).ToList();
        return await ImpactCommand.ComputeEpDiffAsync(baseDb, branchEps, wd, [], handoffRules);
    }

    private static async Task<(string BranchDb, string BaseDb, string Wd)> TwoStoresAsync(AnalysisResult branch, AnalysisResult @base)
    {
        var wd = Path.Combine(Path.GetTempPath(), $"rig-epdiff-{Guid.NewGuid():n}");
        Directory.CreateDirectory(wd);
        return (await MaterializeAsync(branch, wd, "branch"), await MaterializeAsync(@base, wd, "base"), wd);
    }

    private static async Task<string> MaterializeAsync(AnalysisResult result, string wd, string name)
    {
        var db = Path.Combine(wd, $"{name}.db");
        await using var ctx = new RigDbContext(db, pooling: false);
        await Writes.SaveAsync(ctx, result);
        return db;
    }
}

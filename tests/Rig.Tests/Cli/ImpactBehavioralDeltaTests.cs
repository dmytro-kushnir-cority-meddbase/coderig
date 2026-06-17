using Rig.Analysis.Rules;
using Rig.Cli.Commands;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Cli;

// Step 4 — the behavioral delta behind `impact --base` (docs/design-impact-behavioral-diff.md §3.3):
// effects/observations reachable FROM the changed methods, diffed across the two stores. Identical content
// => empty delta (the formatting/no-op-immunity guarantee, achieved via param-free effect keys); a base
// that can't reach what the branch reaches => the branch's effects surface as "added".
[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class ImpactBehavioralDeltaTests(AnalyzedPlaygrounds playgrounds)
{
    [Test]
    public async Task Identical_stores_produce_an_empty_behavioral_delta()
    {
        var pg = await playgrounds.EntryPointEffectsAsync();
        var (branchDb, baseDb, wd) = await TwoStoresAsync(pg.Result, pg.Result);

        var (delta, branchEffects) = await DeltaAsync(branchDb, baseDb, wd);

        branchEffects.ShouldNotBeEmpty(); // sanity: the playground actually reaches effects
        delta.AddedEffects.ShouldBeEmpty();
        delta.RemovedEffects.ShouldBeEmpty();
        delta.AddedObservations.ShouldBeEmpty();
        delta.RemovedObservations.ShouldBeEmpty();
    }

    [Test]
    public async Task A_base_that_lacks_the_changed_methods_surfaces_the_branch_effects_as_added()
    {
        var branchPg = await playgrounds.EntryPointEffectsAsync();
        var basePg = await playgrounds.LegacyNet48Async();
        var (branchDb, baseDb, wd) = await TwoStoresAsync(branchPg.Result, basePg.Result);

        var (delta, branchEffects) = await DeltaAsync(branchDb, baseDb, wd);

        // The branch's changed methods don't exist in the (different-solution) base, so its reachable
        // effects are all newly reachable vs the base.
        branchEffects.ShouldNotBeEmpty();
        delta.AddedEffects.ShouldNotBeEmpty();
    }

    private static async Task<(ImpactCommand.BehavioralDelta Delta, IReadOnlyList<DerivedEffect> BranchEffects)> DeltaAsync(
        string branchDb,
        string baseDb,
        string wd
    )
    {
        var rules = RuleSet.Load(wd);
        await using var branchCtx = new RigDbContext(branchDb, pooling: false, readOnly: true);
        var methods = await Reads.LoadDeadCodeMethodsAsync(branchCtx);
        var seed = methods.Select(m => m.SymbolId).ToHashSet(StringComparer.Ordinal);
        var (branchEffects, branchReach) = await ImpactCommand.ReachEffectsAsync(
            branchCtx,
            seed,
            rules,
            FactPathFinder.TraversalMode.SyncCut
        );
        var delta = await ImpactCommand.ComputeBehavioralDeltaAsync(
            baseDb,
            branchEffects,
            branchReach,
            methods,
            rules,
            FactPathFinder.TraversalMode.SyncCut
        );
        return (delta, branchEffects);
    }

    private static async Task<(string BranchDb, string BaseDb, string Wd)> TwoStoresAsync(AnalysisResult branch, AnalysisResult @base)
    {
        var wd = Path.Combine(Path.GetTempPath(), $"rig-behdelta-{Guid.NewGuid():n}");
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

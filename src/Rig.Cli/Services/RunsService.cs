using Rig.Cli.CommandLine;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.Graph.TraversalGraphLoader;

namespace Rig.Cli.Services;

// The indexed-store inventory, lifted out of FactCommands.BuildRuns so the web host and the CLI share one
// enumeration. Walks EVERY per-commit store (not just LATEST) and flattens each store's runs into a flat
// list of views, marking the LATEST (the store the read commands default to).
public static class RunsService
{
    public sealed record RunView(
        string StoreId,
        bool IsLatest,
        string SolutionPath,
        DateTimeOffset IndexedUtc,
        string? Commit,
        string? Branch,
        bool Dirty,
        int Symbols,
        int References,
        int DiRegistrations
    );

    public static async Task<IReadOnlyList<RunView>> ListAsync(string workingDirectory)
    {
        var storeIds = StoreLayout.AvailableStoreIds(workingDirectory);
        var views = new List<RunView>();

        // No per-commit stores => a single default context (legacy flat store, or a clean "no store").
        if (storeIds.Count == 0)
        {
            await using var context = await OpenReadContextGatedAsync(new WorkspaceLocation(workingDirectory));
            await AppendRunsAsync(context, storeId: "(default)", isLatest: true, views);
            return views;
        }

        var latest = StoreLayout.LatestStoreId(workingDirectory);
        foreach (var storeId in storeIds)
        {
            var isLatest = string.Equals(storeId, latest, StringComparison.OrdinalIgnoreCase);
            await using var context = await OpenReadContextGatedAsync(
                new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: storeId)
            );
            await AppendRunsAsync(context, storeId, isLatest, views);
        }

        // Order for the picker: LATEST (the read default) first, then most-recently-indexed — enumeration
        // order (AvailableStoreIds) is by store-id string, which is meaningless to a human choosing a store.
        return views.OrderByDescending(v => v.IsLatest).ThenByDescending(v => v.IndexedUtc).ToList();
    }

    private static async Task AppendRunsAsync(RigDbContext context, string storeId, bool isLatest, List<RunView> into)
    {
        foreach (var run in await Reads.ListRunsAsync(context))
        {
            into.Add(
                new RunView(
                    StoreId: storeId,
                    IsLatest: isLatest,
                    SolutionPath: run.SolutionPath,
                    IndexedUtc: run.CreatedAtUtc,
                    Commit: run.SourceCommit is { } c ? (c.Length >= 12 ? c[..12] : c) : null,
                    Branch: run.SourceBranch,
                    Dirty: run.SourceDirty,
                    Symbols: run.SymbolCount,
                    References: run.ReferenceCount,
                    DiRegistrations: run.DiRegistrationCount
                )
            );
        }
    }
}

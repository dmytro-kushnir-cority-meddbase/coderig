using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Storage.Queries;
using static Rig.Cli.Graph.TraversalGraphLoader;

namespace Rig.Cli.Services;

// Symbol name search for the web explorer's search box — the same FTS/LIKE search `rig symbols` runs
// (Reads.SearchSymbolsAsync), lifted so the CLI and web share it. Returns the DocID (Id) — which the tree
// endpoint's exact-match pattern resolves precisely — plus a short display name and location.
public static class SymbolSearchService
{
    public sealed record SymbolHit(string Id, string Kind, string Name, string? File, int Line);

    public static async Task<IReadOnlyList<SymbolHit>> SearchAsync(
        string workingDirectory,
        string query,
        string? kind = null,
        int limit = 25,
        bool noLambdas = true,
        string? storeRef = null
    )
    {
        await using var context = await OpenReadContextGatedAsync(new WorkspaceLocation(workingDirectory, storeRef));
        // Fetch unbounded then trim after the lambda filter, matching `rig symbols` (the display cap applies
        // post-filter). Compiler-generated lambdas (~λ) are dropped by default — they're noise in a picker.
        var hits = await Reads.SearchSymbolsAsync(context, pattern: query, kind: kind, limit: int.MaxValue);
        var filtered = noLambdas ? hits.Where(h => !h.SymbolId.Contains("~λ", StringComparison.Ordinal)) : hits;
        return filtered
            .Take(limit)
            .Select(h => new SymbolHit(
                Id: h.SymbolId,
                Kind: h.Kind,
                Name: SymbolNameFormatter.ShortName(h.SymbolId),
                File: h.FilePath,
                Line: h.Line
            ))
            .ToList();
    }
}

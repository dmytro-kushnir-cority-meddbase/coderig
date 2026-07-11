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
        await using var context = await OpenReadContextGatedAsync(
            new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: storeRef)
        );
        // Fetch unbounded then trim after the lambda filter, matching `rig symbols` (the display cap applies
        // post-filter). Compiler-generated lambdas (~λ) are dropped by default — they're noise in a picker.
        var hits = await Reads.SearchSymbolsAsync(context, pattern: query, kind: kind, limit: int.MaxValue);
        var filtered = noLambdas ? hits.Where(h => !h.SymbolId.Contains("~λ", StringComparison.Ordinal)) : hits;

        // Rank for a NAVIGATION picker before applying the cap. The shared query orders by symbolid
        // (alphabetical), which puts DocID prefixes E:/F: (events/fields) ahead of M:/T: (methods/types) — so a
        // common term (e.g. "invoice") fills the 25-row cap with events/fields and buries the methods/types the
        // user actually navigates to. Re-rank: best name match first, then navigable kinds, then shorter (more
        // specific) names. Web-only — the CLI's alphabetical `rig symbols` order is unchanged.
        var q = query.Trim();
        return filtered
            .Select(h => (h, name: SymbolNameFormatter.ShortName(h.SymbolId)))
            .OrderBy(x => NameRank(x.name, q))
            .ThenBy(x => KindRank(x.h.Kind))
            .ThenBy(x => x.name.Length)
            .ThenBy(x => x.h.SymbolId, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => new SymbolHit(Id: x.h.SymbolId, Kind: x.h.Kind, Name: x.name, File: x.h.FilePath, Line: x.h.Line))
            .ToList();
    }

    // How well the display name matches the query: exact > prefix > contains > matched-only-via-DocID.
    private static int NameRank(string name, string q) =>
        name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0
        : name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1
        : name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2
        : 3;

    // Navigable call-graph nodes (methods, types) first; then properties; events/fields/other last.
    private static int KindRank(string kind) =>
        kind is "method" or "type" ? 0
        : kind is "property" ? 1
        : 2;
}

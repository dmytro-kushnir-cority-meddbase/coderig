using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Storage.Queries;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;

namespace Rig.Cli.Services;

// The rule-detected entry-point listing for the web explorer — the SAME set `rig entrypoints` (and
// derive/callers/impact) build: L1 derived EPs + promoted async-handoff origins, deduped and sorted by
// (kind, route). Each carries the QUERYABLE fqn (what to pass as tree ?from=) beside its display route —
// the route form matches nothing, the fqn exact-resolves.
public static class EntryPointService
{
    public sealed record EntryPointView(string Kind, string Route, string Fqn, string? File, int Line);

    public static async Task<IReadOnlyList<EntryPointView>> ListAsync(
        string workingDirectory,
        string? storeRef = null,
        IReadOnlyList<string>? extraRules = null
    )
    {
        var rules = RuleSetLoader.Load(workingDirectory, extraRules ?? []);
        await using var context = await OpenReadContextGatedAsync(new WorkspaceLocation(workingDirectory, storeRef));

        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epSet = await DeriveEntryPointsAsync(context, epData, rules);
        // (file,line) -> handler DocID, so each EP can carry the queryable FQN beside its slash route.
        var docIdBySite = MethodDocIdBySite(epData);

        return epSet
            .Derived.Concat(epSet.PromotedOrigins)
            .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
            .Select(g =>
            {
                var (kind, route, filePath, line) = g.Key;
                return new EntryPointView(
                    Kind: kind,
                    Route: route,
                    Fqn: FqnOrRoute(route: route, filePath: filePath, line: line, docIdBySite: docIdBySite),
                    File: string.IsNullOrEmpty(filePath) ? null : filePath,
                    Line: line
                );
            })
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Route, StringComparer.Ordinal)
            .ToList();
    }
}

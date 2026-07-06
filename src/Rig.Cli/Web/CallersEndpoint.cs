using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rig.Cli.Services;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Web;

// /api/callers — reverse reachability (web equivalent of `rig callers <to>`): who reaches a given method.
// Split into its own MapCallers extension (same shape as PathEndpoint.MapPath / ReachesEndpoint.MapReaches)
// so the feature's service + contracts + registration live together; call `app.MapCallers(workingDirectory)`
// alongside `RigApiEndpoints.MapApi(app, workingDirectory)` in RigWebHost.Build. Thin: delegates to
// CallersQueryService (the same engine `rig callers` runs) and projects to the mode-appropriate DTO. Errors
// surface as a 400 with the message (a bad pattern / missing store is user error, not a 500) — matching
// every other endpoint in RigApiEndpoints/PathEndpoint/ReachesEndpoint.
internal static class CallersEndpoint
{
    public static void MapCallers(this WebApplication app, string workingDirectory)
    {
        // `from` is required (the target method pattern — "who reaches this?"). `store` picks a specific
        // commit/id (default LATEST, mirrors the other endpoints). `mode` selects the lens: "entrypoints" for
        // the precise rule-detected set (`rig callers --entrypoints`), anything else (incl. absent) defaults
        // to "roots" (`rig callers --roots`) — CallersCommand itself has NO shared default between these two
        // (they're independent opt-in flags off a third, unrelated flag-less default — the depth-tagged flat
        // caller listing, out of scope for this endpoint); "roots" is picked as the default here because the
        // CLI documents it as the superset ("Superset of --entrypoints"). `async` mirrors `rig callers --async`
        // (also walk async handoff edges — a TRAVERSAL-mode flag, NOT ASP.NET async/await).
        app.MapGet(
            "/api/callers",
            async (string? from, string? store, string? mode, bool? async) =>
            {
                if (string.IsNullOrWhiteSpace(from))
                {
                    return Results.Problem(
                        title: "Missing 'from'",
                        detail: "Provide a ?from= target method pattern.",
                        statusCode: 400
                    );
                }

                var resolvedMode = string.Equals(mode, "entrypoints", StringComparison.OrdinalIgnoreCase)
                    ? CallersQueryService.CallersMode.EntryPoints
                    : CallersQueryService.CallersMode.Roots;

                try
                {
                    var result = await CallersQueryService.BuildAsync(
                        workingDirectory: workingDirectory,
                        fromPattern: from,
                        storeRef: NullIfBlank(store),
                        mode: resolvedMode,
                        async: async ?? false
                    );

                    if (resolvedMode == CallersQueryService.CallersMode.EntryPoints)
                    {
                        var epResponse = new CallersEntryPointsResponseDto(
                            To: from,
                            Matched: result.Matched,
                            EntryPoints: (result.EntryPoints ?? [])
                                .Select(e => new EntryPointDto(
                                    Kind: e.View.Kind,
                                    Route: e.View.Route,
                                    Fqn: e.View.Fqn,
                                    File: e.View.File,
                                    Line: e.View.Line,
                                    Services: e.Services
                                ))
                                .ToList()
                        );
                        return Results.Json(epResponse);
                    }

                    var rootsResponse = new CallersRootsResponseDto(
                        To: from,
                        Matched: result.Matched,
                        Roots: (result.Roots ?? [])
                            .Select(r => new CallerRootDto(Id: r.SymbolId, Name: ShortName(r.SymbolId), ForwardConfirmed: r.ForwardConfirmed))
                            .ToList()
                    );
                    return Results.Json(rootsResponse);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Callers query failed", detail: ex.Message, statusCode: 400);
                }
            }
        );
    }

    // A blank query-string value (?store=) arrives as "" not null; normalize so the service sees null
    // (LATEST). Duplicated from RigApiEndpoints/PathEndpoint/ReachesEndpoint (private in each) — small
    // enough not to warrant extracting a shared helper for one line.
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

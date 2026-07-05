using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rig.Cli.Services;

namespace Rig.Cli.Web;

// The /api surface for `rig serve`. Thin: each endpoint delegates to a Service (the same engine the CLI runs)
// and projects to a DTO. No caching / no rendering chrome — those stay CLI concerns for now. Errors surface
// as a 400 with the message (a bad pattern / missing store is user error, not a 500).
internal static class RigApiEndpoints
{
    public static void MapApi(WebApplication app, string workingDirectory)
    {
        // Indexed-store inventory (health check + the store picker's source).
        app.MapGet(
            "/api/runs",
            async () =>
            {
                try
                {
                    return Results.Json(await RunsService.ListAsync(workingDirectory));
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Failed to list runs", detail: ex.Message, statusCode: 400);
                }
            }
        );

        // Rule-detected entry points (the search/browse panel's source) — same set as `rig entrypoints`.
        app.MapGet(
            "/api/entrypoints",
            async (string? store) =>
            {
                try
                {
                    return Results.Json(await EntryPointService.ListAsync(workingDirectory, storeRef: NullIfBlank(store)));
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Failed to list entry points", detail: ex.Message, statusCode: 400);
                }
            }
        );

        // The valid only/exclude filter tokens (providers + provider:operation) from the effective rules —
        // feeds the filter control's autocomplete so users pick real tokens instead of typing from memory.
        app.MapGet(
            "/api/providers",
            () =>
            {
                try
                {
                    return Results.Json(ProvidersService.List(workingDirectory));
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Failed to list providers", detail: ex.Message, statusCode: 400);
                }
            }
        );

        // Symbol name search for the search box. `q` required; `kind` optional (method/type/…), `limit` caps.
        app.MapGet(
            "/api/search",
            async (string? q, string? kind, int? limit, string? store) =>
            {
                if (string.IsNullOrWhiteSpace(q))
                {
                    return Results.Problem(title: "Missing 'q'", detail: "Provide a ?q= search query.", statusCode: 400);
                }

                try
                {
                    var hits = await SymbolSearchService.SearchAsync(
                        workingDirectory: workingDirectory,
                        query: q,
                        kind: NullIfBlank(kind),
                        limit: limit is > 0 ? limit.Value : 25,
                        storeRef: NullIfBlank(store)
                    );
                    return Results.Json(hits);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Search failed", detail: ex.Message, statusCode: 400);
                }
            }
        );

        // The call tree from an entry-point / symbol pattern. `from` is required; the rest mirror `rig tree`.
        // NOTE: `view` (paths/full/effects) and `only`/`exclude` effect filters are applied CLIENT-side — the
        // endpoint returns the one canonical tree + all effects, and the SPA projects/filters without refetch.
        app.MapGet(
            "/api/tree",
            async (string? from, int? depth, bool? async, string? store) =>
            {
                if (string.IsNullOrWhiteSpace(from))
                {
                    return Results.Problem(
                        title: "Missing 'from'",
                        detail: "Provide a ?from= entry-point or symbol pattern.",
                        statusCode: 400
                    );
                }

                try
                {
                    var result = await TreeQueryService.BuildAsync(
                        workingDirectory: workingDirectory,
                        fromPattern: from,
                        storeRef: NullIfBlank(store),
                        depth: depth,
                        async: async ?? false
                    );
                    var response = TreeMapper.ToResponse(
                        from: from,
                        roots: result.Roots,
                        effects: result.Effects,
                        locations: result.Locations,
                        emoji: result.EffectEmoji
                    );
                    return Results.Json(response);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Tree query failed", detail: ex.Message, statusCode: 400);
                }
            }
        );
    }

    // A blank query-string value (?store=) arrives as "" not null; normalize so services see null (LATEST).
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

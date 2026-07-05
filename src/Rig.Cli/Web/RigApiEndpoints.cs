using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rig.Cli.Services;

namespace Rig.Cli.Web;

// The /api surface for `rig serve`. Thin: each endpoint delegates to a Service (the same engine the CLI runs)
// and projects to a DTO. Errors surface as a 400 with the message (a bad pattern / missing store is user
// error, not a 500). A commit-scoped store is IMMUTABLE, so responses pinned to an explicit ?store=<id> are
// marked cacheable-forever (see SetStoreCache) — the client caches by store too.
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
            async (HttpContext http, string? store) =>
            {
                try
                {
                    var eps = await EntryPointService.ListAsync(workingDirectory, storeRef: NullIfBlank(store));
                    SetStoreCache(http, store);
                    return Results.Json(eps);
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
            async (HttpContext http, string? q, string? kind, int? limit, string? store) =>
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
                    SetStoreCache(http, store);
                    return Results.Json(hits);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Search failed", detail: ex.Message, statusCode: 400);
                }
            }
        );

        // Per-method hazard marks for a tree (same set as `rig tree --view hazards`) — the client overlays
        // these on nodes when "hazards" is toggled. Separate from /api/tree because it's an expensive whole-
        // store derivation, independently cacheable by store.
        app.MapGet(
            "/api/hazards",
            async (HttpContext http, string? from, string? store) =>
            {
                if (string.IsNullOrWhiteSpace(from))
                {
                    return Results.Problem(title: "Missing 'from'", detail: "Provide a ?from= pattern.", statusCode: 400);
                }

                try
                {
                    var marks = await HazardsService.ForTreeAsync(workingDirectory, from, storeRef: NullIfBlank(store));
                    SetStoreCache(http, store);
                    return Results.Json(marks);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Hazard query failed", detail: ex.Message, statusCode: 400);
                }
            }
        );

        // The call tree from an entry-point / symbol pattern. `from` is required; the rest mirror `rig tree`.
        // NOTE: `view` (paths/full/effects) and `only`/`exclude` effect filters are applied CLIENT-side — the
        // endpoint returns the one canonical tree + all effects, and the SPA projects/filters without refetch.
        app.MapGet(
            "/api/tree",
            async (HttpContext http, string? from, int? depth, bool? async, string? store) =>
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
                    SetStoreCache(http, store);
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

    // A commit-scoped store is IMMUTABLE — its derived facts never change once indexed. So when the caller
    // pinned an explicit ?store=<id>, mark the response cacheable forever: the browser (and any proxy) may
    // reuse it without revalidation. Implicit LATEST is deliberately NOT marked — the LATEST pointer moves on
    // reindex, so that response is not frozen. (The SPA also caches by resolved store id — see index.html.)
    private static void SetStoreCache(HttpContext http, string? store)
    {
        if (string.IsNullOrWhiteSpace(store))
        {
            return;
        }

        http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        http.Response.Headers.ETag = $"\"{store}\"";
    }
}

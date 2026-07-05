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

        // The call tree from an entry-point / symbol pattern. `from` is required; the rest mirror `rig tree`.
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
                        storeRef: string.IsNullOrWhiteSpace(store) ? null : store,
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
}

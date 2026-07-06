using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rig.Cli.Services;

namespace Rig.Cli.Web;

// The /api/reaches endpoint — web equivalent of `rig reaches <from>`: the flat effect inventory (total
// reachable-method count + effects aggregated by provider:operation with site counts + glyph) reachable
// from an entry-point/symbol pattern. Delegates to ReachesQueryService (the same engine `rig reaches`
// runs) and projects to ReachesResponseDto. Errors surface as a 400 with the message (a bad pattern /
// missing store is user error, not a 500) — matches RigApiEndpoints' convention.
internal static class ReachesEndpoint
{
    public static void MapReaches(this WebApplication app, string workingDirectory)
    {
        // The flat effect inventory from an entry-point / symbol pattern. `from` is required; `store` picks
        // a specific commit/id (default LATEST, mirrors the other endpoints); `async` mirrors
        // `rig reaches --async` (async-handoff-reached effects included; delivery fan-out excluded).
        app.MapGet(
            "/api/reaches",
            async (string? from, string? store, bool? async) =>
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
                    var result = await ReachesQueryService.BuildAsync(
                        workingDirectory: workingDirectory,
                        fromPattern: from,
                        storeRef: NullIfBlank(store),
                        async: async ?? false
                    );
                    var response = new ReachesResponseDto(
                        From: result.FromPattern,
                        Matched: result.Matched,
                        ReachableCount: result.ReachableCount,
                        Effects: result
                            .Effects.Select(e => new EffectDto(Provider: e.Provider, Operation: e.Operation, Glyph: e.Glyph, Sites: e.Sites))
                            .ToList()
                    );
                    return Results.Json(response);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Reaches query failed", detail: ex.Message, statusCode: 400);
                }
            }
        );
    }

    // A blank query-string value (?store=) arrives as "" not null; normalize so the service sees null
    // (LATEST). Duplicated from RigApiEndpoints (private there) — small enough not to warrant extracting
    // a shared helper for one line.
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

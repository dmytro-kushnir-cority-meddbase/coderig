using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rig.Cli.Services;

namespace Rig.Cli.Web;

// The /api/refs/{unused,usage} endpoints — web equivalent of `rig refs --unused` / `--usage`: the assembly-
// reference analysis (declared <ProjectReference> edges with zero symbol usage; inbound usage counts per
// assembly). Delegates to UnusedRefsQueryService (the same orchestration `rig refs` runs) and projects to the
// Refs*ResponseDto. Both take an optional `filter` substring, applied exactly as the CLI applies its optional
// pattern (unused → declaring assemblies; usage → target assemblies). Errors surface as a 400 with the message
// (a missing store / unavailable solution is user error, not a 500) — matches the other endpoints' convention.
internal static class RefsEndpoint
{
    public static void MapRefs(this WebApplication app, string workingDirectory)
    {
        // Declared references with zero first-party usage, grouped by declaring assembly (sorted) — the shape
        // the CLI renders. `store` picks a specific commit/id (default LATEST); `filter` narrows DECLARING
        // assemblies by case-insensitive substring, mirroring `rig refs --unused [pattern]`.
        app.MapGet(
            "/api/refs/unused",
            async (string? store, string? filter) =>
            {
                try
                {
                    var result = await UnusedRefsQueryService.UnusedAsync(workingDirectory: workingDirectory, storeRef: NullIfBlank(store));

                    var candidates = result.Candidates;
                    var f = NullIfBlank(filter);
                    if (f is not null)
                    {
                        candidates = candidates.Where(c => c.DeclaringAsm.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
                    }

                    var groups = candidates
                        .GroupBy(c => c.DeclaringAsm, StringComparer.Ordinal)
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => new RefsUnusedGroupDto(
                            DeclaringAsm: g.Key,
                            UnusedAsms: g.Select(c => c.UnusedAsm).OrderBy(a => a, StringComparer.Ordinal).ToList()
                        ))
                        .ToList();

                    var response = new RefsUnusedResponseDto(
                        SolutionAvailable: result.SolutionAvailable,
                        Groups: groups,
                        CandidateCount: candidates.Count,
                        ProjectCount: groups.Count
                    );
                    return Results.Json(response);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Refs unused query failed", detail: ex.Message, statusCode: 400);
                }
            }
        );

        // Inbound first-party reference count per assembly, ascending (least-used first). `filter` narrows
        // TARGET assemblies by case-insensitive substring, mirroring `rig refs --usage [pattern]`.
        app.MapGet(
            "/api/refs/usage",
            async (string? store, string? filter) =>
            {
                try
                {
                    var rows = await UnusedRefsQueryService.UsageAsync(workingDirectory: workingDirectory, storeRef: NullIfBlank(store));

                    var f = NullIfBlank(filter);
                    if (f is not null)
                    {
                        rows = rows.Where(r => r.Assembly.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
                    }

                    var response = new RefsUsageResponseDto(
                        Rows: rows.Select(r => new RefsUsageRowDto(Assembly: r.Assembly, Refs: r.Refs, FromMethods: r.FromMethods)).ToList()
                    );
                    return Results.Json(response);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Refs usage query failed", detail: ex.Message, statusCode: 400);
                }
            }
        );
    }

    // A blank query-string value (?store=) arrives as "" not null; normalize so the service sees null
    // (LATEST). Duplicated from the other endpoints (private there) — small enough not to warrant extracting.
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

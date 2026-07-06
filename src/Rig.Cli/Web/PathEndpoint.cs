using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rig.Analysis.Rules;
using Rig.Cli.Services;
using Rig.Domain.Data;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Web;

// /api/path — the first concrete call path between two symbols (web equivalent of `rig path <from> <to>`).
// Split out of RigApiEndpoints.MapApi into its own MapPath extension (same shape as MapApi itself) so the
// path feature's service + contracts + registration live together; call `app.MapPath(workingDirectory)`
// alongside `RigApiEndpoints.MapApi(app, workingDirectory)` in RigWebHost.Build. Thin: delegates to
// PathQueryService (the same engine `rig path` runs) and projects to PathResponseDto. Errors surface as a
// 400 with the message (a bad pattern / missing store is user error, not a 500) — matching every other
// endpoint in RigApiEndpoints.
public static class PathEndpoint
{
    public static void MapPath(this WebApplication app, string workingDirectory)
    {
        // The first concrete call path from `from` to `to`. Both required; `store`/`async` mirror the
        // equivalent `/api/tree` params and `rig path --store`/`--async`.
        app.MapGet(
            "/api/path",
            async (string? from, string? to, string? store, bool? async) =>
            {
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                {
                    return Results.Problem(
                        title: "Missing from/to",
                        detail: "Provide ?from=<pattern>&to=<pattern>.",
                        statusCode: 400
                    );
                }

                try
                {
                    var result = await PathQueryService.BuildAsync(
                        workingDirectory: workingDirectory,
                        fromPattern: from,
                        toPattern: to,
                        storeRef: NullIfBlank(store),
                        async: async ?? false
                    );

                    var nodes = result
                        .Steps.Select(step => new PathNodeDto(
                            Id: step.SymbolId,
                            Name: ShortName(step.SymbolId),
                            EdgeKind: step.Kind,
                            LoopKind: step.LoopKind,
                            Fanout: step.Fanout,
                            HandoffVia: step.HandoffVia,
                            DispatchBasis: step.DispatchBasis,
                            File: step.FilePath,
                            Line: step.Line,
                            Effects: ToEffectDtos(
                                result.EffectsBySymbol.TryGetValue(step.SymbolId, out var effects) ? effects : [],
                                result.EffectEmoji
                            )
                        ))
                        .ToList();

                    var response = new PathResponseDto(
                        From: from,
                        To: to,
                        Matched: result.Matched,
                        Nodes: nodes,
                        FromMatches: result.FromMatches,
                        ToMatches: result.ToMatches
                    );
                    return Results.Json(response);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Path query failed", detail: ex.Message, statusCode: 400);
                }
            }
        );
    }

    // Aggregate one method's DerivedEffects into distinct provider:operation EffectDtos with a call-site
    // count and the repo's glyph — the same projection TreeMapper.BuildEffectIndex produces per node, kept
    // as a small local helper here rather than exposing TreeMapper's (internal, tree-shaped) version.
    private static IReadOnlyList<EffectDto> ToEffectDtos(IReadOnlyList<DerivedEffect> effects, IReadOnlyDictionary<string, string> emoji) =>
        effects
            .GroupBy(e => (e.Provider, e.Operation))
            .Select(g => new EffectDto(
                Provider: g.Key.Provider,
                Operation: g.Key.Operation,
                Glyph: EmojiLookup.For(emoji, provider: g.Key.Provider, operation: g.Key.Operation),
                Sites: g.Count()
            ))
            .OrderBy(e => e.Provider, StringComparer.Ordinal)
            .ThenBy(e => e.Operation, StringComparer.Ordinal)
            .ToList();

    // A blank query-string value (?store=) arrives as "" not null; normalize so services see null (LATEST).
    // Mirrors RigApiEndpoints.NullIfBlank (private there) — kept as a tiny local copy rather than exposing
    // a shared helper across the two registration sites.
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rig.Analysis.Rules;
using Rig.Cli.Rendering;
using Rig.Cli.Services;

namespace Rig.Cli.Web;

// The /api surface for `rig serve`. Thin: each endpoint delegates to a Service (the same engine the CLI runs)
// and projects to a DTO. Errors surface as a 400 with the message (a bad pattern / missing store is user
// error, not a 500). Responses are `no-store` (see RigWebHost) — the CLIENT caches, keyed by a DERIVATION
// VERSION (store facts are immutable, but derived output also depends on the rules + tool build). /api/meta
// exposes that version so the client can key + purge its persistent cache correctly.
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

        // The derivation version = hash(tool build ⊕ rules fingerprint). It changes when the tool is rebuilt
        // (new MVID) or the rule set is edited — i.e. whenever the DERIVED output for a given store could
        // differ. The client keys its cache by this and purges on change, so a rules edit / rig upgrade never
        // serves stale derived data (the store id alone is not enough).
        app.MapGet(
            "/api/meta",
            () =>
            {
                try
                {
                    return Results.Json(new { derivationVersion = DerivationVersion(workingDirectory) });
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Failed to compute meta", detail: ex.Message, statusCode: 400);
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

        // Store-vs-store impact diff (same as `rig impact --base --head`): per-EP behavioral effect/hazard
        // deltas + entry-point add/remove. Both refs required. Expensive (loads + derives BOTH stores) —
        // minutes on a big store; the client shows a busy state.
        app.MapGet(
            "/api/impact",
            async (string? @base, string? head, bool? async) =>
            {
                if (string.IsNullOrWhiteSpace(@base) || string.IsNullOrWhiteSpace(head))
                {
                    return Results.Problem(title: "Missing base/head", detail: "Provide ?base=<store>&head=<store>.", statusCode: 400);
                }

                try
                {
                    var art = await ImpactQueryService.DiffAsync(workingDirectory, baseRef: @base, headRef: head, async: async ?? false);
                    return Results.Json(ImpactMapper.ToResponse(art));
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Impact diff failed", detail: ex.Message, statusCode: 400);
                }
            }
        );

        // Hacky live-progress stream for the expensive cold diff (SSE): runs the diff, emitting a `phase` event
        // per top-level phase as it completes (which ALSO warms the disk cache), then `done`. The client shows
        // the phases live, then GETs the now-warm /api/impact for the data. On a warm cache it just fires
        // `cache hit` → `done` immediately. Progress-only — the result comes from /api/impact.
        app.MapGet(
            "/api/impact/stream",
            async (HttpContext http, string? @base, string? head, bool? async) =>
            {
                http.Response.Headers.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers.Append("X-Accel-Buffering", "no"); // don't let a proxy buffer the stream

                async Task Send(string ev, string data)
                {
                    await http.Response.WriteAsync($"event: {ev}\ndata: {data}\n\n");
                    await http.Response.Body.FlushAsync();
                }

                if (string.IsNullOrWhiteSpace(@base) || string.IsNullOrWhiteSpace(head))
                {
                    await Send("failed", "provide ?base=&head=");
                    return;
                }

                try
                {
                    await Send("phase", "starting…");
                    await ImpactQueryService.DiffAsync(
                        workingDirectory,
                        baseRef: @base,
                        headRef: head,
                        async: async ?? false,
                        onPhase: (name, ms) => Send("phase", $"{name} · {ms} ms")
                    );
                    await Send("done", "ready");
                }
                catch (Exception ex)
                {
                    // custom "failed" (not "error") so it doesn't collide with EventSource's built-in error.
                    await Send("failed", ex.Message);
                }
            }
        );

        // Per-EP STRUCTURAL reach delta (for the tree diff overlay): the methods newly reachable (Added) /
        // no-longer reachable (Removed) for ONE entry point, looked up from the (warm-cached) impact diff's
        // AffectedEps by (kind, route). Bounded to one EP — no recompute; reads the same cached artifact.
        app.MapGet(
            "/api/impact/reach",
            async (string? @base, string? head, string? kind, string? route, bool? async) =>
            {
                if (string.IsNullOrWhiteSpace(@base) || string.IsNullOrWhiteSpace(head) || string.IsNullOrWhiteSpace(route))
                {
                    return Results.Problem(title: "Missing base/head/route", detail: "Provide ?base=&head=&route=.", statusCode: 400);
                }

                try
                {
                    var art = await ImpactQueryService.DiffAsync(workingDirectory, baseRef: @base, headRef: head, async: async ?? false);
                    var ep = art.Diff.AffectedEps.FirstOrDefault(e => e.Route == route && (string.IsNullOrEmpty(kind) || e.Kind == kind));
                    ImpactReachNodeDto Node(string id) => new(id, SymbolNameFormatter.ShortName(id));
                    return Results.Json(
                        ep is null
                            ? new ImpactReachDto([], [])
                            : new ImpactReachDto(ep.Added.Select(Node).ToList(), ep.Removed.Select(Node).ToList())
                    );
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Impact reach lookup failed", detail: ex.Message, statusCode: 400);
                }
            }
        );

        // Per-method hazard marks for a tree (same set as `rig tree --view hazards`) — the client overlays
        // these on nodes when "hazards" is toggled. Separate from /api/tree because it's an expensive whole-
        // store derivation, independently cacheable by store.
        app.MapGet(
            "/api/hazards",
            async (string? from, string? store) =>
            {
                if (string.IsNullOrWhiteSpace(from))
                {
                    return Results.Problem(title: "Missing 'from'", detail: "Provide a ?from= pattern.", statusCode: 400);
                }

                try
                {
                    var marks = await HazardsService.ForTreeAsync(workingDirectory, from, storeRef: NullIfBlank(store));
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

    // The cache-invalidation version for DERIVED output. A store's facts are immutable, but tree/effects/
    // hazards also depend on (a) the tool's derivation LOGIC and (b) the rule set — so the client must key its
    // cache by more than the store id. MVID changes on every tool rebuild; RulesFingerprint changes on any
    // rule edit. Their hash is a compact token: same input ⇒ same output ⇒ cache is safe; either moves ⇒ the
    // client's keys change and it purges. Loaded fresh per call so a mid-session rig.rules.json edit is caught.
    private static string DerivationVersion(string workingDirectory)
    {
        RuleSetLoader.Load(workingDirectory, [], out var loadedPaths);
        var rulesHash = RulesFingerprint.ComputeFromPaths(loadedPaths);
        var mvid = typeof(RigApiEndpoints).Module.ModuleVersionId.ToString("N");
        var bytes = System.Text.Encoding.UTF8.GetBytes(mvid + "|" + rulesHash);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }
}

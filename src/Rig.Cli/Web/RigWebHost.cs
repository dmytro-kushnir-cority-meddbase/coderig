using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Rig.Cli.Web;

// Builds the in-process web host for `rig serve`: Kestrel on localhost:<port>, the /api surface, and the
// static React SPA from wwwroot (next to the assembly, NOT the cwd — the cwd is the store dir). Self-
// contained so the whole Web/ folder can move to a standalone Rig.Web project later with only the
// workingDirectory-plumbing changing.
internal static class RigWebHost
{
    public static WebApplication Build(string workingDirectory, int port)
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                // wwwroot ships next to the dll (see Rig.Cli.csproj), so the content root is the assembly
                // directory — the process cwd is the user's store directory and holds no web assets.
                ContentRootPath = AppContext.BaseDirectory,
            }
        );
        builder.WebHost.UseUrls($"http://localhost:{port}");
        // A dev-facing local tool: quiet the request-logging spam, keep warnings+.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // The call tree is deeply nested and System.Text.Json counts a depth level for BOTH each node object
        // AND its children array — so the effective JSON depth is ~2× the tree depth. The default cap (64)
        // truncates mid-serialization on real trees (MedDBase SaveLetter is 38 deep ⇒ ~78 JSON levels). 512
        // covers a ~250-deep tree; BuildTree's cycle/shared-callee truncation bounds any single chain well
        // under that, so this is headroom, not unbounded recursion.
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.MaxDepth = 512);

        var app = builder.Build();
        app.UseDefaultFiles(); // serve index.html at "/"
        app.UseStaticFiles();
        // API responses are `no-store`: derived output isn't frozen by store id alone (it also depends on the
        // rules + tool build), so the browser HTTP cache must not hold it. The SPA does the caching itself,
        // keyed by the derivation version (/api/meta) so it invalidates correctly on a rules edit / rig upgrade.
        app.Use(
            async (ctx, next) =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.Headers.CacheControl = "no-store";
                }

                await next();
            }
        );
        RigApiEndpoints.MapApi(app, workingDirectory);
        // SPA fallback: any non-/api, non-file route serves index.html so client-side routing works.
        app.MapFallbackToFile("index.html");
        return app;
    }
}

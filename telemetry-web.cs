#:sdk Microsoft.NET.Sdk.Web

// Self-contained single-file web app for the rig index telemetry dashboard.
//
//   dotnet run telemetry-web.cs                      # serve on http://localhost:8740, latest CSV auto-loaded
//   dotnet run telemetry-web.cs -- --urls http://localhost:9000
//   then open /?csv=<absolute path to a rig-index-telemetry.csv>  to view a specific run
//
// It serves tools/telemetry-dashboard.html (the same standalone dashboard) with the chosen
// rig-index-telemetry.csv spliced into its embedded default, so the page renders immediately instead of
// waiting for a manual file upload. Run it from the repo root. No external dependencies beyond the web SDK.

using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
if (!args.Any(a => a.StartsWith("--urls", StringComparison.Ordinal)))
{
    builder.WebHost.UseUrls("http://localhost:8740");
}

var app = builder.Build();

var root = Directory.GetCurrentDirectory();
var dashboardPath = Path.Combine(root, "tools", "telemetry-dashboard.html");

// Where rig writes telemetry by default; the MedDBase analysis store. Override per-request with ?csv=.
const string DefaultCsvPath = @"C:\git\meddbase-analysis\rig-index-telemetry.csv";

// Replace the dashboard's `const DEFAULT_CSV = ` ... ` ;` literal with live CSV text. A MatchEvaluator is
// used so `$` in the data is never treated as a substitution; CSV carries no backticks, so the literal stays
// well-formed (guarded anyway).
static string Inject(string html, string csv)
{
    var safe = csv.Replace("`", "'");
    return Regex.Replace(html, "const DEFAULT_CSV = `[\\s\\S]*?`;", _ => "const DEFAULT_CSV = `" + safe + "`;");
}

app.MapGet(
    "/",
    (string? csv) =>
    {
        if (!File.Exists(dashboardPath))
        {
            return Results.Text($"Dashboard not found at {dashboardPath}. Run from the repo root.", "text/plain", statusCode: 500);
        }

        var html = File.ReadAllText(dashboardPath);
        var csvPath = string.IsNullOrWhiteSpace(csv) ? DefaultCsvPath : csv;
        if (File.Exists(csvPath))
        {
            html = Inject(html, File.ReadAllText(csvPath));
        }

        return Results.Content(html, "text/html; charset=utf-8");
    }
);

// The raw CSV currently being served, handy for scripting / sanity checks.
app.MapGet(
    "/csv",
    (string? csv) =>
    {
        var csvPath = string.IsNullOrWhiteSpace(csv) ? DefaultCsvPath : csv;
        return File.Exists(csvPath) ? Results.Text(File.ReadAllText(csvPath), "text/csv") : Results.NotFound($"No CSV at {csvPath}");
    }
);

app.Run();

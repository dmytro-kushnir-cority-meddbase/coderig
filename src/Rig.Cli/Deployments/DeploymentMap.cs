using System.Text.Json;

namespace Rig.Cli.Deployments;

// A deployed service from deployments.json with the capability tokens it `provides`. A token is opaque;
// an EP whose rule `requires` a token is ACTIVE-IN this service only when Provides intersects that
// requirement (ANY semantics). A service that declares no `provides` activates every EP (ungated), so the
// gate is strictly opt-in. See DeploymentMap.ActiveServices.
internal sealed record ServiceDef(string Name, string Host, string? Kind, string? Note, IReadOnlyList<string> Provides);

// Maps source files to the deployed service(s) whose process loads them. Built query-side (no re-index):
// load deployments.json, walk each service's entry-csproj transitive <ProjectReference> closure, index
// every project back to the owning service(s). Membership is "this code is LOADED in service X" — an upper
// bound for actor/background EPs, which only ACTIVATE in the Echo-bootstrapping host.
internal sealed class DeploymentMap
{
    public static readonly DeploymentMap Empty = new([], new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase), []);

    private readonly Dictionary<string, List<string>> _projectToServices; // project full path -> service names
    private readonly (string Dir, string Project)[] _projectDirs; // owning-dir prefix, longest first

    private readonly Dictionary<string, ServiceDef> _byName;

    public IReadOnlyList<ServiceDef> Services { get; }
    public bool IsEmpty => Services.Count == 0;

    internal DeploymentMap(
        IReadOnlyList<ServiceDef> services,
        Dictionary<string, List<string>> projectToServices,
        (string Dir, string Project)[] projectDirs
    )
    {
        Services = services;
        _projectToServices = projectToServices;
        _projectDirs = projectDirs;
        _byName = services.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public ServiceDef? Service(string name) => _byName.GetValueOrDefault(name);

    public IReadOnlyList<string> ServicesForFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || _projectDirs.Length == 0)
        {
            return [];
        }

        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch
        {
            return [];
        }

        foreach (var (dir, project) in _projectDirs)
        {
            if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            {
                return _projectToServices.TryGetValue(project, out var svcs) ? svcs : [];
            }
        }

        return [];
    }

    // The subset of `loadedServices` in which an EP requiring `requires` actually ACTIVATES: a loaded
    // service activates it iff it `provides` at least one required token (ANY / non-empty-intersection).
    // When `requires` is null/empty the EP is ungated and active in every service that loads it, so
    // active-in collapses to loaded-in. Tokens are opaque (a future ALL mode would change only this predicate).
    public IReadOnlyList<string> ActiveServices(IReadOnlyList<string> loadedServices, IReadOnlyList<string>? requires)
    {
        if (requires is null || requires.Count == 0 || loadedServices.Count == 0)
        {
            return loadedServices;
        }

        var required = new HashSet<string>(requires, StringComparer.OrdinalIgnoreCase);
        return loadedServices.Where(s => Service(s) is { } def && def.Provides.Any(required.Contains)).ToArray();
    }

    public static async Task<DeploymentMap> LoadAsync(string workingDirectory, string? solutionPath, TextWriter? log = null)
    {
        var configPath = Path.Combine(workingDirectory, "deployments.json");
        if (!File.Exists(configPath))
        {
            return Empty;
        }

        if (solutionPath is null || !File.Exists(solutionPath))
        {
            log?.WriteLine("deployments.json found but the indexed solution path is unavailable — skipping deployment attribution.");
            return Empty;
        }

        var services = ParseServices(configPath, log);
        if (services.Count == 0)
        {
            return Empty;
        }

        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? workingDirectory;
        var depGraph = await DependencyGraph.BuildAsync(solutionPath, log);

        var projectToServices = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services)
        {
            var hostFull = Path.GetFullPath(
                Path.Combine(solutionDir, service.Host.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))
            );
            if (!depGraph.ContainsKey(hostFull) && !File.Exists(hostFull))
            {
                log?.WriteLine($"deployments.json: host project not found for '{service.Name}': {service.Host}");
                continue;
            }

            foreach (var project in DependencyGraph.TransitiveClosure(hostFull, depGraph))
            {
                if (!projectToServices.TryGetValue(project, out var list))
                {
                    list = [];
                    projectToServices[project] = list;
                }
                if (!list.Contains(service.Name))
                {
                    list.Add(service.Name);
                }
            }
        }

        // Owning-directory prefixes, longest first, so a file attributes to its most specific project
        // (a nested project wins over its parent).
        var projectDirs = projectToServices
            .Keys.Select(p => (Dir: EnsureTrailingSeparator(Path.GetDirectoryName(p) ?? p), Project: p))
            .OrderByDescending(x => x.Dir.Length)
            .ToArray();

        return new DeploymentMap(services, projectToServices, projectDirs);
    }

    private static string EnsureTrailingSeparator(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;

    private static List<ServiceDef> ParseServices(string configPath, TextWriter? log)
    {
        try
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(configPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }
            );
            if (!doc.RootElement.TryGetProperty("services", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<ServiceDef>();
            foreach (var e in arr.EnumerateArray())
            {
                var name = GetString(e, "name");
                var host = GetString(e, "host");
                if (name is null || host is null)
                {
                    continue;
                }

                result.Add(new ServiceDef(name, host, GetString(e, "kind"), GetString(e, "note"), GetStringArray(e, "provides")));
            }
            return result;
        }
        catch (Exception ex)
        {
            log?.WriteLine($"deployments.json: failed to parse ({ex.Message}) — skipping deployment attribution.");
            return [];
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
    }
}

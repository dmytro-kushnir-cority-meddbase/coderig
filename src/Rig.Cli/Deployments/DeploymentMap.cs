using System.Text.Json;

namespace Rig.Cli.Deployments;

// A deployed service from deployments.json: a name, its entry csproj (relative to the solution dir),
// and a best-effort host kind. `BootstrapsEcho` flags the host that initialises the Echo actor system
// (used to refine actor/handoff EP attribution — see DeploymentMap remarks).
internal sealed record ServiceDef(string Name, string Host, string? Kind, string? Note, bool BootstrapsEcho);

// Maps source files to the deployed service(s) whose process loads them.
//
// Built entirely query-side (no re-index): load deployments.json, walk each service's entry-csproj
// transitive <ProjectReference> closure via DependencyGraph, then index every project in a closure back
// to the owning service(s). A source file attributes to a service when its owning project is in that
// service's closure. Membership is therefore "this code is LOADED in service X" — an upper bound for
// actor/background EPs, which only ACTIVATE in the Echo-bootstrapping host (BootstrapsEcho).
internal sealed class DeploymentMap
{
    public static readonly DeploymentMap Empty = new([], new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase), []);

    private readonly Dictionary<string, List<string>> _projectToServices; // project full path -> service names
    private readonly (string Dir, string Project)[] _projectDirs; // owning-dir prefix, longest first

    private readonly Dictionary<string, ServiceDef> _byName;

    public IReadOnlyList<ServiceDef> Services { get; }
    public bool IsEmpty => Services.Count == 0;

    private DeploymentMap(
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

    // The service definition for a name (for kind/note lookup), or null if unknown.
    public ServiceDef? Service(string name) => _byName.GetValueOrDefault(name);

    // The deployed service(s) whose process loads this source file, in deployments.json order.
    // Empty when the file belongs to no service closure (e.g. a test/tool project) or no config exists.
    public IReadOnlyList<string> ServicesForFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || _projectDirs.Length == 0)
            return [];

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
            if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                return _projectToServices.TryGetValue(project, out var svcs) ? svcs : [];
        return [];
    }

    // Loads deployments.json from workingDirectory and resolves it against the indexed solution.
    // Returns Empty (feature off, no output noise) when the config is absent or the solution path is
    // unknown/missing — so deployment annotations are strictly opt-in.
    public static async Task<DeploymentMap> LoadAsync(string workingDirectory, string? solutionPath, TextWriter? log = null)
    {
        var configPath = Path.Combine(workingDirectory, "deployments.json");
        if (!File.Exists(configPath))
            return Empty;
        if (solutionPath is null || !File.Exists(solutionPath))
        {
            log?.WriteLine("deployments.json found but the indexed solution path is unavailable — skipping deployment attribution.");
            return Empty;
        }

        var services = ParseServices(configPath, log);
        if (services.Count == 0)
            return Empty;

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

            foreach (var project in Closure(hostFull, depGraph))
            {
                if (!projectToServices.TryGetValue(project, out var list))
                {
                    list = [];
                    projectToServices[project] = list;
                }
                if (!list.Contains(service.Name))
                    list.Add(service.Name);
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

    // BFS the transitive ProjectReference closure from the entry project (inclusive). Paths are the
    // graph's already-normalised full paths; a host outside the graph yields just itself.
    private static IEnumerable<string> Closure(string entry, Dictionary<string, List<string>> depGraph)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(entry);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (!visited.Add(p))
                continue;
            if (depGraph.TryGetValue(p, out var deps))
                foreach (var d in deps)
                    if (!visited.Contains(d))
                        queue.Enqueue(d);
        }
        return visited;
    }

    private static string EnsureTrailingSeparator(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;

    private static IReadOnlyList<ServiceDef> ParseServices(string configPath, TextWriter? log)
    {
        try
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(configPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }
            );
            if (!doc.RootElement.TryGetProperty("services", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<ServiceDef>();
            foreach (var e in arr.EnumerateArray())
            {
                var name = GetString(e, "name");
                var host = GetString(e, "host");
                if (name is null || host is null)
                    continue;
                var bootstraps = e.TryGetProperty("bootstrapsEcho", out var b) && b.ValueKind == JsonValueKind.True;
                result.Add(new ServiceDef(name, host, GetString(e, "kind"), GetString(e, "note"), bootstraps));
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
}

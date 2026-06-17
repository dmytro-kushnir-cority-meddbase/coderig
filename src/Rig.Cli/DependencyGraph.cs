using System.Xml.Linq;

namespace Rig.Cli;

/// <summary>
/// Builds a project-level dependency graph from a solution file by parsing
/// &lt;ProjectReference&gt; elements in each .csproj — no MSBuild required.
/// </summary>
internal static class DependencyGraph
{
    // Returns: projectPath -> list of project paths it directly references
    public static async Task<Dictionary<string, List<string>>> BuildAsync(string solutionPath, TextWriter? log = null)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Collect all .csproj paths from the solution file
        var projectPaths = await DiscoverProjectsAsync(solutionPath, log);

        foreach (var projPath in projectPaths)
        {
            if (!File.Exists(projPath))
            {
                continue;
            }

            try
            {
                var refs = ParseProjectReferences(projPath);
                graph[projPath] = refs;
            }
            catch
            {
                graph[projPath] = [];
            }
        }

        return graph;
    }

    // Transitive closure (BFS) of `entry` over a project dependency graph: the entry plus every project
    // reachable by following ProjectReference edges. The single "everything reachable from a root" walk
    // shared by `index --from` and deployment attribution (which previously each open-coded the same
    // visited/queue loop). Paths are matched case-insensitively (the graph's key comparer).
    public static HashSet<string> TransitiveClosure(string entry, IReadOnlyDictionary<string, List<string>> graph)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(entry);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (!visited.Add(p))
            {
                continue;
            }

            if (graph.TryGetValue(p, out var deps))
            {
                foreach (var d in deps)
                {
                    if (!visited.Contains(d))
                    {
                        queue.Enqueue(d);
                    }
                }
            }
        }
        return visited;
    }

    private static List<string> ParseProjectReferences(string projPath)
    {
        var dir = Path.GetDirectoryName(projPath) ?? "";
        var doc = XDocument.Load(projPath);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => v is not null)
            .Select(v => Path.GetFullPath(Path.Combine(dir, v!.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> DiscoverProjectsAsync(string solutionPath, TextWriter? log)
    {
        var ext = Path.GetExtension(solutionPath).ToLowerInvariant();
        var dir = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();

        if (ext == ".slnx")
        {
            return ParseSlnx(slnxPath: solutionPath, baseDir: dir);
        }

        if (ext is ".sln")
        {
            return ParseSln(content: await File.ReadAllTextAsync(solutionPath), baseDir: dir);
        }

        if (ext is ".slnf")
        {
            // solution filter — projects listed under solution.projects
            var json = await File.ReadAllTextAsync(solutionPath);
            return ParseSlnf(json: json, baseDir: dir);
        }

        if (ext is ".csproj")
        {
            return [solutionPath];
        }

        log?.WriteLine($"[mine] Warning: unrecognised solution format {ext}");
        return [];
    }

    private static string[] ParseSlnx(string slnxPath, string baseDir)
    {
        var doc = XDocument.Load(slnxPath);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Project")
            .Select(e => e.Attribute("Path")?.Value)
            .Where(v => v is not null && v.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(v => Path.GetFullPath(Path.Combine(baseDir, v!.Replace('\\', Path.DirectorySeparatorChar))))
            .ToArray();
    }

    private static List<string> ParseSln(string content, string baseDir)
    {
        var result = new List<string>();
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Project(", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = trimmed.Split('"');
            if (parts.Length < 6)
            {
                continue;
            }

            var projRelPath = parts[5].Trim();
            if (!projRelPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(Path.GetFullPath(Path.Combine(baseDir, projRelPath.Replace('\\', Path.DirectorySeparatorChar))));
        }
        return result;
    }

    private static string[] ParseSlnf(string json, string baseDir)
    {
        // Minimal JSON parse — extract paths from "projects":["...","..."]
        var idx = json.IndexOf("\"projects\"", StringComparison.Ordinal);
        if (idx < 0)
        {
            return [];
        }

        var start = json.IndexOf('[', idx);
        var end = json.IndexOf(']', start);
        if (start < 0 || end < 0)
        {
            return [];
        }

        var segment = json[(start + 1)..end];
        return segment
            .Split(',')
            .Select(s =>
                s.Trim()
                    .Trim('"')
                    .Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar)
                    .Replace(oldChar: '\\', newChar: Path.DirectorySeparatorChar)
            )
            .Where(s => s.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(s => Path.GetFullPath(Path.Combine(baseDir, s)))
            .ToArray();
    }
}

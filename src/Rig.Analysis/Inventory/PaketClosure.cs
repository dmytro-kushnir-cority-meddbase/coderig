using System.Collections.Concurrent;
using System.Text;

namespace Rig.Analysis.Inventory;

// Per-project Paket dependency closure, derived from COMMITTED inputs (the root paket.lock + paket.dependencies
// and the project's own paket.references) — never from the obj/* generated props, which the design-time build
// rewrites and so race the fingerprint (see docs/build-cache.md).
//
// WHY: BuildInputFingerprint used to content-hash the WHOLE root paket.lock (and paket.dependencies) into
// EVERY project's key, so a single version bump anywhere invalidated all ~300 projects even though only a
// handful actually resolve the changed package. This scopes that: a project's paket contribution is the
// resolved entries in ITS transitive closure plus the global resolution settings. A package bump then
// invalidates exactly the projects whose closure contains it (e.g. a Parlot bump touches 2 projects, not 300).
//
// CONSERVATIVE BY CONSTRUCTION: dependency-edge framework RESTRICTIONS are NOT applied (we walk every edge),
// so the computed closure is a SUPERSET of Paket's framework-filtered resolution. It can only OVER-invalidate
// (rebuild a project that didn't strictly need it), never UNDER-invalidate (replay a stale build) — the safe
// direction, matching the fingerprint's "errs toward changed" contract. The global settings blob (sources /
// framework / redirects / per-group RESTRICTION + GROUP/NUGET headers) is folded wholesale, so any global
// resolution-affecting change still invalidates every paket-managed project.
internal static class PaketClosure
{
    // The implicit (default) Paket group, used before any GROUP/`group` line. Matched case-insensitively, so
    // the lock's `GROUP Foo`, the references'/dependencies' `group Foo`, and this sentinel line up.
    private const string MainGroup = "Main";

    internal sealed record LockPackage(string RawLine, List<string> DepNamesLower);

    // group -> (packageLower -> resolved entry), plus the lock's non-package "settings" lines (headers,
    // sources, RESTRICTION/REDIRECTS, GROUP/NUGET markers) folded globally.
    internal sealed record LockModel(Dictionary<string, Dictionary<string, LockPackage>> Groups, string Settings);

    // group -> (packageLower -> raw `nuget` declaration line), plus the dependencies' non-`nuget` settings.
    internal sealed record DepsModel(Dictionary<string, Dictionary<string, string>> Groups, string Settings);

    // Memoised per (path, length, mtime) so the ~2k-line lock is parsed once per run, not once per project;
    // the identity in the key self-invalidates if a file is edited mid-session (tests).
    private static readonly ConcurrentDictionary<string, LockModel?> LockCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DepsModel?> DepsCache = new(StringComparer.Ordinal);

    // IMPERATIVE SHELL: the fingerprint material for a project's Paket dependencies, or null when the project
    // is NOT paket-managed (no paket.references) or no paket.lock is found up-tree — then the build-cache
    // fingerprint folds nothing for Paket (correct: such a project's build doesn't depend on the lock). All IO
    // (up-tree lookup, reads, per-path parse memo) lives here; the closure + fold is the pure Material core.
    public static string? Compute(string projectDir)
    {
        var referencesPath = Path.Combine(projectDir, "paket.references");
        if (!File.Exists(referencesPath))
        {
            return null;
        }

        var lockDir = FindUpwards(startDir: projectDir, fileName: "paket.lock");
        if (lockDir is null)
        {
            return null;
        }

        var lockPath = Path.Combine(lockDir, "paket.lock");
        var lockModel = LockCache.GetOrAdd(MemoKey(lockPath), _ => ReadLockModel(lockPath));
        if (lockModel is null)
        {
            return null;
        }

        var depsPath = Path.Combine(lockDir, "paket.dependencies");
        var depsModel = File.Exists(depsPath) ? DepsCache.GetOrAdd(MemoKey(depsPath), _ => ReadDepsModel(depsPath)) : null;

        string[] referenceLines;
        try
        {
            referenceLines = File.ReadAllLines(referencesPath);
        }
        catch (IOException)
        {
            return null;
        }

        return Material(lockModel: lockModel, depsModel: depsModel, directByGroup: ParseReferences(referenceLines));
    }

    // PURE entry point for tests + reuse: parse the three manifests from raw text and produce the same closure
    // material the shell folds from disk. No IO — the natural unit to drive exhaustively from in-memory strings.
    internal static string ComputeMaterial(string lockText, string? depsText, string referencesText) =>
        Material(
            lockModel: ParseLock(SplitLines(lockText)),
            depsModel: depsText is null ? null : ParseDeps(SplitLines(depsText)),
            directByGroup: ParseReferences(SplitLines(referencesText))
        );

    // PURE CORE: the transitive closure of the project's direct references over the lock's dependency edges,
    // folded with the global resolution settings into deterministic, order-stable fingerprint material. No IO,
    // no clock — a function of the parsed manifests alone, so it is exhaustively unit-testable in isolation.
    internal static string Material(LockModel lockModel, DepsModel? depsModel, Dictionary<string, List<string>> directByGroup)
    {
        // Transitive closure per group over the lock's dependency edges. Deps stay within their own group
        // (Paket groups are isolated), so the walk never crosses group boundaries. Keyed "group\tpackageLower"
        // in a SortedSet for a stable, order-independent fold.
        var closure = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (group, directs) in directByGroup)
        {
            lockModel.Groups.TryGetValue(group, out var groupLock);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(directs);
            while (stack.Count > 0)
            {
                var pkg = stack.Pop();
                if (!visited.Add(pkg))
                {
                    continue;
                }

                closure.Add($"{group}\t{pkg}");
                if (groupLock is not null && groupLock.TryGetValue(pkg, out var entry))
                {
                    foreach (var dep in entry.DepNamesLower)
                    {
                        stack.Push(dep);
                    }
                }
            }
        }

        // Fold the global resolution settings once (any global change still invalidates every paket project),
        // then each closure member's resolved line (lock) and declared line (dependencies). A package not in
        // the lock (e.g. a direct ref Paket couldn't resolve) still folds its key, so its presence flips.
        var material = new StringBuilder();
        material.Append("paket-lock-settings\n").Append(lockModel.Settings).Append('\n');
        if (depsModel is not null)
        {
            material.Append("paket-deps-settings\n").Append(depsModel.Settings).Append('\n');
        }

        foreach (var key in closure)
        {
            var tab = key.IndexOf('\t', StringComparison.Ordinal);
            var group = key[..tab];
            var pkgLower = key[(tab + 1)..];
            material.Append(key).Append('|');
            if (lockModel.Groups.TryGetValue(group, out var gl) && gl.TryGetValue(pkgLower, out var lp))
            {
                material.Append(lp.RawLine);
            }

            material.Append('|');
            if (depsModel is not null && depsModel.Groups.TryGetValue(group, out var gd) && gd.TryGetValue(pkgLower, out var dl))
            {
                material.Append(dl);
            }

            material.Append('\n');
        }

        return material.ToString();
    }

    // Split raw manifest text into lines the way File.ReadAllLines does (normalise CRLF, and a trailing
    // newline yields no final empty line), so the pure ComputeMaterial matches the shell's per-file parse.
    private static string[] SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    // The nearest ancestor directory (inclusive) that contains fileName, or null. Mirrors BuildInputFingerprint's
    // up-tree walk so the lock is found at the repo root regardless of how deep the project sits.
    private static string? FindUpwards(string startDir, string fileName)
    {
        for (var dir = startDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, fileName)))
            {
                return dir;
            }
        }

        return null;
    }

    private static string MemoKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{path}|{info.Length}|{File.GetLastWriteTimeUtc(path).Ticks}" : $"{path}|-";
        }
        catch (IOException)
        {
            return $"{path}|-";
        }
    }

    // Parse paket.lock. Indentation is the grammar: 4-space lines are RESOLVED packages (`Name (version)…`),
    // 6-space lines are their dependency edges (`Name (constraint)…`); everything else (GROUP/NUGET/GITHUB/HTTP
    // markers, `remote:`, RESTRICTION/REDIRECTS/STRATEGY) is a settings line folded globally. Package + edge
    // names are lower-cased for matching; the package's RAW line (version + any restriction) is kept for the
    // fingerprint so a version OR restriction change flips it.
    private static LockModel? ReadLockModel(string path)
    {
        try
        {
            return ParseLock(File.ReadAllLines(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static LockModel ParseLock(string[] lines)
    {
        var groups = new Dictionary<string, Dictionary<string, LockPackage>>(StringComparer.OrdinalIgnoreCase)
        {
            [MainGroup] = new(StringComparer.Ordinal),
        };
        var settings = new StringBuilder();
        var current = MainGroup;
        LockPackage? lastPackage = null;

        foreach (var raw in lines)
        {
            var indent = LeadingSpaces(raw);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (indent == 4 && trimmed.Contains('(', StringComparison.Ordinal))
            {
                var nameLower = PackageName(trimmed).ToLowerInvariant();
                lastPackage = new LockPackage(RawLine: trimmed, DepNamesLower: []);
                GroupFor(groups, current)[nameLower] = lastPackage;
            }
            else if (indent == 6 && lastPackage is not null && trimmed.Contains('(', StringComparison.Ordinal))
            {
                lastPackage.DepNamesLower.Add(PackageName(trimmed).ToLowerInvariant());
            }
            else
            {
                // Header / source / restriction — global resolution settings.
                settings.Append(raw).Append('\n');
                if (trimmed.StartsWith("GROUP ", StringComparison.OrdinalIgnoreCase))
                {
                    current = trimmed["GROUP ".Length..].Trim();
                    GroupFor(groups, current);
                    lastPackage = null;
                }
                else if (
                    trimmed.Equals("NUGET", StringComparison.Ordinal)
                    || trimmed.Equals("GITHUB", StringComparison.Ordinal)
                    || trimmed.Equals("HTTP", StringComparison.Ordinal)
                )
                {
                    lastPackage = null;
                }
            }
        }

        return new LockModel(Groups: groups, Settings: settings.ToString());
    }

    // Parse paket.dependencies. `nuget <Name> …` lines are the per-group direct declarations (kept raw so a
    // version/flag change flips that package's contribution); `group <name>` switches the current group;
    // everything else (source / framework / redirects / comments) is a global settings line.
    private static DepsModel? ReadDepsModel(string path)
    {
        try
        {
            return ParseDeps(File.ReadAllLines(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static DepsModel ParseDeps(string[] lines)
    {
        var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [MainGroup] = new(StringComparer.Ordinal),
        };
        var settings = new StringBuilder();
        var current = MainGroup;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("nuget ", StringComparison.OrdinalIgnoreCase))
            {
                var name = FirstToken(trimmed["nuget ".Length..].TrimStart());
                if (name.Length > 0)
                {
                    GroupForDeps(groups, current)[name.ToLowerInvariant()] = trimmed;
                }
            }
            else if (trimmed.StartsWith("group ", StringComparison.OrdinalIgnoreCase))
            {
                current = trimmed["group ".Length..].Trim();
                GroupForDeps(groups, current);
                settings.Append(raw).Append('\n'); // the group boundary itself is a settings marker
            }
            else
            {
                settings.Append(raw).Append('\n');
            }
        }

        return new DepsModel(Groups: groups, Settings: settings.ToString());
    }

    // The project's direct package references per group. `group <name>` switches group; blank/comment lines are
    // skipped; otherwise the first token is the package name (the `redirects: force`-style suffix is ignored —
    // a suffix-only change is still caught because BuildInputFingerprint hashes the paket.references file whole).
    internal static Dictionary<string, List<string>> ParseReferences(string[] lines)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var current = MainGroup;
        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith("group ", StringComparison.OrdinalIgnoreCase))
            {
                current = trimmed["group ".Length..].Trim();
                continue;
            }

            var name = FirstToken(trimmed);
            if (name.Length == 0)
            {
                continue;
            }

            if (!result.TryGetValue(current, out var list))
            {
                result[current] = list = [];
            }

            list.Add(name.ToLowerInvariant());
        }

        return result;
    }

    private static Dictionary<string, LockPackage> GroupFor(Dictionary<string, Dictionary<string, LockPackage>> groups, string group)
    {
        if (!groups.TryGetValue(group, out var g))
        {
            groups[group] = g = new Dictionary<string, LockPackage>(StringComparer.Ordinal);
        }

        return g;
    }

    private static Dictionary<string, string> GroupForDeps(Dictionary<string, Dictionary<string, string>> groups, string group)
    {
        if (!groups.TryGetValue(group, out var g))
        {
            groups[group] = g = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return g;
    }

    private static int LeadingSpaces(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ')
        {
            n++;
        }

        return n;
    }

    // The package name at the start of a trimmed lock/dependency token: everything up to the first space or '('.
    private static string PackageName(string trimmed)
    {
        var end = trimmed.Length;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] is ' ' or '(')
            {
                end = i;
                break;
            }
        }

        return trimmed[..end];
    }

    private static string FirstToken(string trimmed)
    {
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? trimmed : trimmed[..space];
    }
}

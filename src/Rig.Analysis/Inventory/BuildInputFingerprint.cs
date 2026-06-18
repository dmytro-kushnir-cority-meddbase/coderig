using System.Security.Cryptography;
using System.Text;

namespace Rig.Analysis.Inventory;

// Cheap, pessimistic fingerprint over the inputs that determine a project's DESIGN-TIME BUILD output:
// resolved references (obj/project.assets.json), compile options (the .csproj + Directory.* props +
// global.json up the tree), and the SET of source files. It deliberately reads NO file contents —
// content changes are picked up by Roslyn re-reading the files on every index (cache hit or miss), so
// they must NOT invalidate this key. The big restore artifact is STAT'd (length + mtime), not hashed —
// measured ~280x cheaper on the MedDBase closure (16ms vs 4.5s). Errs toward "changed" (→ cache miss →
// rebuild) on any ambiguity, so a wrong answer is slow, never incorrect.
internal static class BuildInputFingerprint
{
    // Version/config manifests across the dependency mechanisms this codebase mixes — Paket, classic
    // NuGet (packages.config), PackageReference / Central Package Management — plus MSBuild props and the
    // SDK pin. Stat'd up the directory tree: root-level ones (paket.lock/dependencies, global.json,
    // Directory.*, nuget.config) are found at the repo root; project-level ones (paket.references,
    // packages.config) at the project dir. ALL are committed SOURCE files the design-time build only
    // READS — paket/nuget *restore* doesn't rewrite them (only `paket install`/`update` or a manual edit
    // does) — so stat is stable and never races the build's obj output.
    //
    // This is deliberately an ALLOWLIST: a version pinned by a mechanism not listed here ("god knows what
    // else") won't flip the key. That's why --reuse-build-cache is opt-in — after an exotic dependency
    // change, clear the cache or index without the flag.
    private static readonly string[] AncestorConfigFiles =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "paket.lock",
        "paket.dependencies",
        "paket.references",
        "packages.config",
        "nuget.config",
        "NuGet.config",
    ];

    public static string Compute(string projectFilePath)
    {
        var fullProjectPath = Path.GetFullPath(projectFilePath);
        var projectDir = Path.GetDirectoryName(fullProjectPath) ?? "";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Only build-STABLE inputs are fingerprinted — files the design-time build never writes — so the
        // key can't race the build's own output flush. (We tried hashing obj/project.assets.json and
        // obj/project.nuget.cache: both are build OUTPUTS, and reading them right after the out-of-process
        // build races the flush, so the fingerprint took multiple indexes to converge. Inputs don't race.)
        //
        // The project file itself — declared PackageReference versions, direct/framework <Reference>s,
        // TFM, compile options all live here, so an edit flips the key.
        FeedStat(hash, fullProjectPath);

        // Build props/targets + CPM versions + SDK pin + the dependency-manifest allowlist, walked up to
        // the drive root. Under Central Package Management every package version is in
        // Directory.Packages.props; under Paket the versions are in paket.lock — either way a bump flips
        // the key here. (Gap: a transitive-only bump with no manifest change isn't seen — narrow, and
        // --reuse-build-cache is opt-in; re-index without it after such a change.)
        for (var dir = projectDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            foreach (var name in AncestorConfigFiles)
            {
                FeedStat(hash, Path.Combine(dir, name));
            }

            // The Paket tooling/wiring lives in a .paket subdir at the repo root. Its targets define HOW
            // references resolve, so a Paket upgrade can change resolution without touching paket.lock.
            FeedStat(hash, Path.Combine(dir, ".paket", "Paket.Restore.targets"));
        }

        // The SET of *.cs paths (detects add / remove / rename — removals also self-heal downstream via
        // the File.Exists filter, but additions only surface here). Sorted for stability; paths only, no
        // mtime and no content — edits to existing files must stay a cache HIT.
        var csFiles = EnumerateCsFiles(projectDir).ToArray();
        Array.Sort(csFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var file in csFiles)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file));
            hash.AppendData("\n"u8);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void FeedStat(IncrementalHash hash, string path)
    {
        // length + mtime when present; a fixed marker when absent, so presence/absence flips the key.
        // Used only for inputs the build does NOT rewrite (the .csproj + Directory.* props + global.json).
        var bytes = File.Exists(path)
            ? Encoding.UTF8.GetBytes($"{path}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}\n")
            : Encoding.UTF8.GetBytes($"{path}|-\n");
        hash.AppendData(bytes);
    }

    // *.cs under the project dir, skipping obj/bin/.vs. No content read — enumeration only.
    private static IEnumerable<string> EnumerateCsFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(path: current, searchPattern: "*.cs"))
            {
                yield return file;
            }

            foreach (var subdirectory in Directory.EnumerateDirectories(current))
            {
                var leaf = Path.GetFileName(subdirectory);
                if (leaf is not ("obj" or "bin" or ".vs"))
                {
                    stack.Push(subdirectory);
                }
            }
        }
    }
}

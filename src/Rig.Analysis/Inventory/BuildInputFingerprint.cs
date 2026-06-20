using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Rig.Analysis.Inventory;

// Cheap, pessimistic fingerprint over the inputs that determine a project's DESIGN-TIME BUILD output:
// compile options (the .csproj + Directory.* props + global.json up the tree), the dependency manifests
// (paket.lock, packages.config, Directory.Packages.props, nuget.config), and the SET of source files.
//
// The small, build-STABLE inputs (the .csproj + the manifests) are CONTENT-HASHED, NOT stat'd. An earlier
// version stat'd them (length + mtime) for speed, but `git switch` rewrites file mtimes without changing
// content, so a branch switch — especially a round-trip — invalidated the whole closure even though the
// builds were identical. Content-hashing makes the key stable across switches: only a real edit flips it.
// These files are KBs, and a shared root manifest (paket.lock / Directory.Packages.props) is hashed once
// per run via ContentHashCache, not once per project, so the cost stays negligible.
//
// Source FILE CONTENT is still NOT read — only the SET of *.cs paths is folded (add/remove/rename), because
// body edits are picked up by Roslyn re-reading the files on every index (cache hit or miss) and must NOT
// invalidate this key. Errs toward "changed" (→ cache miss → rebuild) on any ambiguity, so a wrong answer
// is slow, never incorrect.
internal static class BuildInputFingerprint
{
    // Version/config manifests across the dependency mechanisms this codebase mixes — Paket, classic
    // NuGet (packages.config), PackageReference / Central Package Management — plus MSBuild props and the
    // SDK pin. Stat'd up the directory tree: root-level ones (global.json, Directory.*, nuget.config) are
    // found at the repo root; project-level ones (paket.references, packages.config) at the project dir.
    // ALL are committed SOURCE files the design-time build only READS — paket/nuget *restore* doesn't
    // rewrite them (only `paket install`/`update` or a manual edit does) — so stat is stable and never
    // races the build's obj output.
    //
    // The root paket.lock + paket.dependencies are deliberately NOT here: hashing them WHOLESALE keyed every
    // project to the entire dependency graph, so one version bump invalidated all ~300 projects. They are now
    // folded PER PROJECT via PaketClosure (only the project's transitive closure + the global resolution
    // settings), so a bump invalidates exactly the projects that resolve the changed package. paket.references
    // stays here (per-project already) so a binding-redirect/suffix-only edit PaketClosure ignores still flips.
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
        // TFM, compile options all live here, so an edit flips the key. Content-hashed (not stat'd) so a
        // branch switch that rewrites its mtime but not its content stays a cache HIT.
        FeedContent(hash, fullProjectPath);

        // Build props/targets + CPM versions + SDK pin + the dependency-manifest allowlist, walked up to
        // the drive root. Under Central Package Management every package version is in
        // Directory.Packages.props; under Paket the versions are in paket.lock — folded PER PROJECT below
        // (PaketClosure), not here, so a bump only flips the projects that resolve the changed package.
        for (var dir = projectDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            foreach (var name in AncestorConfigFiles)
            {
                FeedContent(hash, Path.Combine(dir, name));
            }

            // The Paket tooling/wiring lives in a .paket subdir at the repo root. Its targets define HOW
            // references resolve, so a Paket upgrade can change resolution without touching paket.lock.
            FeedContent(hash, Path.Combine(dir, ".paket", "Paket.Restore.targets"));
        }

        // Paket's per-project resolved closure, derived from the COMMITTED root paket.lock + paket.dependencies
        // scoped to THIS project's paket.references (PaketClosure). Replaces hashing the whole lock into every
        // project: a version bump now invalidates only the projects whose transitive closure contains it.
        // Null when the project isn't paket-managed (no paket.references) — then there's no lock dependency to
        // fold. Conservative (over-approximates the closure), so it errs toward "changed", never stale.
        var paketClosure = PaketClosure.Compute(projectDir);
        if (paketClosure is not null)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(paketClosure));
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

    // Per-run memo of file CONTENT hashes, keyed by (path, length, mtime) so a shared root manifest
    // (paket.lock, Directory.Packages.props, …) is hashed once even though every project in the closure
    // walks up to it. The mtime is only a memo-validity check — the fingerprint folds the content hash, so
    // a `git switch` that bumps mtime without changing content recomputes the SAME hash → still a cache hit.
    // A one-shot CLI process; the key self-invalidates if a file is genuinely edited mid-session (tests).
    private static readonly ConcurrentDictionary<string, string> ContentHashCache = new(StringComparer.Ordinal);

    private static void FeedContent(IncrementalHash hash, string path)
    {
        // The SHA-256 of the file's bytes when present; a fixed marker when absent, so presence/absence
        // flips the key. Content (not mtime) is what determines the build, so this is stable across the
        // mtime churn a branch checkout produces.
        if (!File.Exists(path))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{path}|-\n"));
            return;
        }

        var info = new FileInfo(path);
        var memoKey = $"{path}|{info.Length}|{File.GetLastWriteTimeUtc(path).Ticks}";
        var contentHash = ContentHashCache.GetOrAdd(memoKey, static (_, arg) => HashFile(arg), path);
        hash.AppendData(Encoding.UTF8.GetBytes($"{path}|{contentHash}\n"));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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

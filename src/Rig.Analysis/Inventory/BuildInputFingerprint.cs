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

    // A materialised build-input file: its path and the SHA-256 hex of its bytes, or null when absent (so
    // presence/absence flips the key). Content, not mtime — stable across the mtime churn a branch switch makes.
    public readonly record struct FileFold(string Path, string? Sha256);

    // Everything the design-time-build fingerprint is a function of, materialised off disk by Gather so Of can
    // fold it purely: the project file, the ancestor manifest walk (in walk order), the paket closure material
    // (null when the project isn't paket-managed), and the sorted *.cs path set. The functional-core boundary —
    // a recompute is Of(Gather(path)); the pure half is independently testable and reused by --verify-build-cache.
    public sealed record BuildInputs(
        FileFold ProjectFile,
        IReadOnlyList<FileFold> ConfigFiles,
        string? PaketClosureMaterial,
        IReadOnlyList<string> CsPaths
    );

    // PURE CORE: fold a materialised input set into the fingerprint — no IO, no clock. The order mirrors the
    // old inline Compute exactly (project file, then the ancestor walk in order, then the paket closure
    // material, then the sorted *.cs paths), so existing cache keys stay valid across this refactor.
    public static string Of(BuildInputs inputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Feed(hash, inputs.ProjectFile);
        foreach (var file in inputs.ConfigFiles)
        {
            Feed(hash, file);
        }

        // Paket's per-project resolved closure (PaketClosure) — only the project's transitive closure + the
        // global resolution settings, so a version bump flips just the projects that resolve the package.
        if (inputs.PaketClosureMaterial is not null)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(inputs.PaketClosureMaterial));
        }

        // The SET of *.cs paths (detects add / remove / rename). Paths only, no content — body edits are
        // Roslyn's job on every index and must NOT flip this key.
        foreach (var cs in inputs.CsPaths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(cs));
            hash.AppendData("\n"u8);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // IMPERATIVE SHELL: read everything Of needs off disk. Only build-STABLE inputs are read — files the
    // design-time build never writes — so the fingerprint can't race the build's obj output. The project file
    // (declared versions/refs/TFM/options), the ancestor manifest walk up to the drive root (each content-
    // hashed via the per-run memo), the paket closure material, and the *.cs path set.
    public static BuildInputs Gather(string projectFilePath)
    {
        var fullProjectPath = Path.GetFullPath(projectFilePath);
        var projectDir = Path.GetDirectoryName(fullProjectPath) ?? "";

        var configFiles = new List<FileFold>();
        for (var dir = projectDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            foreach (var name in AncestorConfigFiles)
            {
                configFiles.Add(Fold(Path.Combine(dir, name)));
            }

            // The Paket tooling/wiring lives in a .paket subdir at the repo root. Its targets define HOW
            // references resolve, so a Paket upgrade can change resolution without touching paket.lock.
            configFiles.Add(Fold(Path.Combine(dir, ".paket", "Paket.Restore.targets")));
        }

        var csFiles = EnumerateCsFiles(projectDir).ToArray();
        Array.Sort(csFiles, StringComparer.OrdinalIgnoreCase);

        return new BuildInputs(
            ProjectFile: Fold(fullProjectPath),
            ConfigFiles: configFiles,
            PaketClosureMaterial: PaketClosure.Compute(projectDir),
            CsPaths: csFiles
        );
    }

    // The fingerprint of a project on disk: gather (IO) then fold (pure).
    public static string Compute(string projectFilePath) => Of(Gather(projectFilePath));

    // Per-run memo of file CONTENT hashes, keyed by (path, length, mtime) so a shared root manifest
    // (paket.lock, Directory.Packages.props, …) is hashed once even though every project in the closure
    // walks up to it. The mtime is only a memo-validity check — the fingerprint folds the content hash, so
    // a `git switch` that bumps mtime without changing content recomputes the SAME hash → still a cache hit.
    // A one-shot CLI process; the key self-invalidates if a file is genuinely edited mid-session (tests).
    private static readonly ConcurrentDictionary<string, string> ContentHashCache = new(StringComparer.Ordinal);

    // PURE: fold one file's (path, content-hash-or-absent) into the hash — `path|sha\n` when present,
    // `path|-\n` when absent, so presence/absence and content both flip the key.
    private static void Feed(IncrementalHash hash, FileFold file) =>
        hash.AppendData(Encoding.UTF8.GetBytes(file.Sha256 is null ? $"{file.Path}|-\n" : $"{file.Path}|{file.Sha256}\n"));

    // IO: a file's content fold — SHA-256 hex when present (memoised per run by path+len+mtime so a shared
    // root manifest is hashed once), null when absent. Content (not mtime) is what determines the build, so
    // this is stable across the mtime churn a branch checkout produces.
    private static FileFold Fold(string path)
    {
        if (!File.Exists(path))
        {
            return new FileFold(Path: path, Sha256: null);
        }

        var info = new FileInfo(path);
        var memoKey = $"{path}|{info.Length}|{File.GetLastWriteTimeUtc(path).Ticks}";
        return new FileFold(Path: path, Sha256: ContentHashCache.GetOrAdd(memoKey, static (_, arg) => HashFile(arg), path));
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

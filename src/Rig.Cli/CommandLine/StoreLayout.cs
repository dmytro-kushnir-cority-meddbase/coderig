using System.Globalization;
using Rig.Domain.Data;

namespace Rig.Cli.CommandLine;

// Resolves the on-disk location of the .rig store. As of the per-commit layout an index is written into a
// per-commit subdirectory `.rig/<store-id>/` (store-id = the source commit, or a timestamp when the source
// is not a git work tree), and `.rig/LATEST` names the most-recently-indexed one. Read commands resolve the
// LATEST store transparently. A pre-layout flat `.rig/rig.db` is still READ as a fallback (so an existing
// store keeps working until re-indexed); `rig index` moves it aside (`.legacy.bak`) on the next index.
// Back-compat is deliberately allowed to break here — see docs/design-impact-behavioral-diff.md §4.4.
internal static class StoreLayout
{
    internal const string RigDirName = ".rig";
    internal const string DbFileName = "rig.db";
    internal const string LatestPointerName = "LATEST";

    internal static string RigDir(string workingDirectory) => Path.Combine(workingDirectory, RigDirName);

    // The store DIRECTORY to read from: the latest per-commit store, else the `.rig` dir itself (which
    // resolves to the legacy flat `.rig/rig.db`, or — when nothing is indexed — a path whose open fails
    // into CommandGuard's clean "no store" error).
    internal static string ResolveStoreDir(string workingDirectory)
    {
        var rig = RigDir(workingDirectory);
        if (!Directory.Exists(rig))
        {
            return rig;
        }

        return LatestStoreDir(rig) ?? rig;
    }

    // The db path of the resolved read store.
    internal static string DbPath(string workingDirectory) => Path.Combine(ResolveStoreDir(workingDirectory), DbFileName);

    // The db path a read command should open, honoring an explicit `--store` ref. Empty/null => the LATEST
    // store (the default). A given ref must match an indexed per-commit store (by store-id, or a commit
    // sha prefix either way — see ResolveStoreDirByRef); an unmatched ref throws StoreRefNotFoundException
    // so CommandGuard can list what IS indexed instead of failing into a raw "no such file" open error.
    internal static string DbPathForRef(WorkspaceLocation ws) => Path.Combine(ResolveReadStoreDir(ws), DbFileName);

    // The store DIRECTORY a read command should use, honoring `--store`. Empty/null => the LATEST store dir
    // (ResolveStoreDir). A given ref must match an indexed per-commit store; an unmatched ref throws
    // StoreRefNotFoundException. Commands that key a cache to the store dir (tree) resolve through here too,
    // so `--store` steers the context AND the cache consistently.
    internal static string ResolveReadStoreDir(WorkspaceLocation ws)
    {
        if (string.IsNullOrWhiteSpace(ws.StoreRef))
        {
            return ResolveStoreDir(ws.WorkingDirectory);
        }

        return ResolveStoreDirByRef(ws) ?? throw new StoreRefNotFoundException(ws.StoreRef, AvailableStoreIds(ws.WorkingDirectory));
    }

    // The newest per-commit store dir — the LATEST pointer wins, else the newest subdir holding a rig.db by
    // write time; null when no per-commit store exists (caller falls back to the flat layout).
    private static string? LatestStoreDir(string rigDir)
    {
        var pointer = Path.Combine(rigDir, LatestPointerName);
        if (File.Exists(pointer))
        {
            var id = File.ReadAllText(pointer).Trim();
            if (id.Length > 0)
            {
                var dir = Path.Combine(rigDir, id);
                if (File.Exists(Path.Combine(dir, DbFileName)))
                {
                    return dir;
                }
            }
        }

        return Directory
            .EnumerateDirectories(rigDir)
            .Where(d => File.Exists(Path.Combine(d, DbFileName)))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, DbFileName)))
            .FirstOrDefault();
    }

    // --- write side (index / mine) ---

    // The store-id for a new index: the short source commit (suffixed `-dirty` when the work tree had
    // uncommitted edits — that store is NOT a clean commit), or a timestamp when the source is not a git
    // work tree. Stable for a clean commit, so re-indexing it rebuilds the same immutable store in place.
    internal static string NewStoreId(GitProvenance provenance)
    {
        if (provenance.Commit is { Length: > 0 } commit)
        {
            var shortSha = commit.Length >= 12 ? commit[..12] : commit;
            return provenance.Dirty ? shortSha + "-dirty" : shortSha;
        }

        return "ts-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    }

    // The per-commit store directory for a new index (created if absent).
    internal static string NewStoreDir(string workingDirectory, string storeId)
    {
        var dir = Path.Combine(RigDir(workingDirectory), storeId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Record the most-recently-indexed store-id so read commands default to it.
    internal static void WriteLatestPointer(string workingDirectory, string storeId) =>
        File.WriteAllText(Path.Combine(RigDir(workingDirectory), LatestPointerName), storeId);

    // Move a pre-layout flat store (`.rig/rig.db` + sidecars) aside, once, so the per-commit layout owns
    // `.rig` going forward. Kept as flat `.legacy.bak` files — NOT a subdir, so LatestStoreDir (which only
    // scans subdirs for a rig.db) never mistakes the backup for a live store.
    internal static void BackupLegacyFlatStore(string workingDirectory)
    {
        var rig = RigDir(workingDirectory);
        foreach (var name in new[] { "rig.db", "rig.db-wal", "rig.db-shm", "cache.db", "cache.db-wal", "cache.db-shm" })
        {
            var path = Path.Combine(rig, name);
            if (File.Exists(path))
            {
                var backup = path + ".legacy.bak";
                File.Delete(backup); // no-op if absent; clears an earlier backup so Move won't throw
                File.Move(path, backup);
            }
        }
    }

    // --- addressing a specific store (the two-store diff, step 3) ---

    // Resolve a per-commit store dir by an explicit store-id or a commit sha (full or short). Matches the
    // sub-store whose id (minus any `-dirty` suffix) shares a sha prefix with `refOrId` — so a full HEAD
    // sha resolves the short-sha store dir, and a short prefix resolves too. Exact id wins outright.
    // Returns null when nothing matches.
    internal static string? ResolveStoreDirByRef(WorkspaceLocation ws)
    {
        if (string.IsNullOrWhiteSpace(ws.StoreRef))
        {
            return null;
        }

        var rig = RigDir(ws.WorkingDirectory);
        if (!Directory.Exists(rig))
        {
            return null;
        }

        string? match = null;
        foreach (var dir in Directory.EnumerateDirectories(rig))
        {
            if (!File.Exists(Path.Combine(dir, DbFileName)))
            {
                continue;
            }

            var id = Path.GetFileName(dir);
            if (string.Equals(id, ws.StoreRef, StringComparison.OrdinalIgnoreCase))
            {
                return dir; // exact store-id wins
            }

            var stem = id.EndsWith("-dirty", StringComparison.Ordinal) ? id[..^"-dirty".Length] : id;
            if (
                stem.StartsWith(ws.StoreRef, StringComparison.OrdinalIgnoreCase)
                || ws.StoreRef.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            )
            {
                match = dir;
            }
        }

        return match;
    }

    // The store-id read commands default to: the LATEST pointer's target (else the newest store by write
    // time). Null when nothing is indexed or only a legacy flat store exists. Lets `rig runs` mark which of
    // the per-commit stores is the default the other read commands resolve to.
    internal static string? LatestStoreId(string workingDirectory)
    {
        var rig = RigDir(workingDirectory);
        if (!Directory.Exists(rig))
        {
            return null;
        }

        var dir = LatestStoreDir(rig);
        return dir is null ? null : Path.GetFileName(dir);
    }

    // The ids of every per-commit store present (for discovery / "base not indexed" messages).
    internal static IReadOnlyList<string> AvailableStoreIds(string workingDirectory)
    {
        var rig = RigDir(workingDirectory);
        if (!Directory.Exists(rig))
        {
            return [];
        }

        return Directory
            .EnumerateDirectories(rig)
            .Where(d => File.Exists(Path.Combine(d, DbFileName)))
            .Select(d => Path.GetFileName(d))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }
}

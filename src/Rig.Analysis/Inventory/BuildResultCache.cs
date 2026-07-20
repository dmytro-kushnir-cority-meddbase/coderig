using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rig.Analysis.Inventory;

// Per-project sidecar cache of the design-time build output (ProjectBuildInfo), keyed by an input
// fingerprint (BuildInputFingerprint). On a HIT the expensive out-of-process design-time build is
// skipped entirely and the cached references/sources/options are replayed — Roslyn still reads the
// actual source + reference bytes fresh, so a hit is only safe because the fingerprint already proved
// the build INPUTS (refs/options/file-set) are unchanged. Best-effort: any IO/JSON failure degrades to
// a miss (rebuild); the cache can never block, corrupt, or wrong an index. Sidecars live OUTSIDE the
// per-commit store so they persist/shared across indexes.
internal sealed class BuildResultCache(string cacheDirectory, string? framework = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // IO PORT: the sidecar's stored payload (fingerprint + build output) if one exists and parses, else null
    // (absent or garbled → treated as a miss). Deliberately does NOT compare fingerprints — whether it still
    // matches is the pure BuildCacheDecision.Decide, kept out of the IO so it can be tested in isolation.
    public StoredBuild? Load(string projectFilePath)
    {
        try
        {
            var path = SidecarPath(projectFilePath);
            return File.Exists(path) ? JsonSerializer.Deserialize<StoredBuild>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch
        {
            return null; // unreadable/garbled sidecar → treat as miss, rebuild
        }
    }

    public void Store(string projectFilePath, string fingerprint, ProjectBuildInfo info)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            File.WriteAllText(
                SidecarPath(projectFilePath),
                JsonSerializer.Serialize(new StoredBuild(Fingerprint: fingerprint, Info: info), JsonOptions)
            );
        }
        catch
        {
            // best-effort: a failed write just means a rebuild next index
        }
    }

    // Stable filename from the normalised project path (content of the path, not the project).
    private string SidecarPath(string projectFilePath)
    {
        var identity = Path.GetFullPath(projectFilePath);
        if (framework is not null)
        {
            identity += $"\nframework:{framework.ToUpperInvariant()}";
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return Path.Combine(cacheDirectory, key + ".json");
    }
}

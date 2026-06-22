using System.Security.Cryptography;
using System.Text;

namespace Rig.Analysis.Rules;

// A content hash of the EFFECTIVE merged rule set (builtin-rules.json + global ~/.rig + local
// rig.rules.json + any --rules files) for use in query-cache keys: any rule edit, or adding/removing a
// layer, changes the hash → the cached artifact misses → recompute. RuleSetLoader.ResolveLoadedPaths
// already resolves exactly which files the cascade loaded, so this hashes each of those by path +
// content — no need to re-implement the resolution.
public static class RulesFingerprint
{
    public static string Compute(string workingDirectory, IReadOnlyList<string>? extraRulesPaths = null)
    {
        var loadedPaths = RuleSetLoader.ResolveLoadedPaths(workingDirectory, extraRulesPaths);
        return ComputeFromPaths(loadedPaths);
    }

    // Hash an ALREADY-RESOLVED set of loaded rule file paths (the list RuleSetLoader returns). Lets a caller
    // that already resolved the cascade (e.g. via RuleSetLoader.Load's out-param) reuse those paths instead of
    // re-running the cascade merge just to re-discover them. Compute() is the path-resolving wrapper over this.
    public static string ComputeFromPaths(IReadOnlyList<string> loadedPaths)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in loadedPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(path));
            sha.AppendData([0]);
            try
            {
                sha.AppendData(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                // A file that vanished mid-run just contributes its path; recompute is harmless.
            }
            sha.AppendData([0]);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}

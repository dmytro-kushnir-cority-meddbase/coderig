using System.Security.Cryptography;
using System.Text;

namespace Rig.Analysis.Rules;

// A content hash of the EFFECTIVE merged rule set (builtin-rules.json + global ~/.rig + local
// rig.rules.json + any --rules files) for use in query-cache keys: any rule edit, or adding/removing a
// layer, changes the hash → the cached artifact misses → recompute. AnalysisRuleSet.LoadForSolution
// already resolves exactly which files the cascade loaded (LoadedRulesPaths), so this hashes each of
// those by path + content — no need to re-implement the resolution.
public static class RulesFingerprint
{
    public static string Compute(string workingDirectory, IReadOnlyList<string>? extraRulesPaths = null)
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        var ruleSet = AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in ruleSet.LoadedRulesPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
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

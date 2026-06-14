using System.Security.Cryptography;
using System.Text;

namespace Rig.Analysis;

// Deterministic, order- and path-independent digest of an assembly's source texts. Drives assembly
// dedup in the multi-solution store: re-indexing skips an assembly whose (name, ContentHash) is already
// present (see docs/multi-solution-storage.md).
//
// The digest is the SHA-256 of the sorted multiset of per-file content hashes. Consequences:
//   * order-independent — enumeration order of the files never changes the result;
//   * path-independent — portable across checkout directories (the store is keyed on what the SYMBOLS
//     are, and a pure rename changes no DocID, so it is correctly treated as "unchanged");
//   * change-sensitive — editing any file, or adding/removing one, changes the multiset and the digest.
public static class ProjectContentHash
{
    public static string Compute(IEnumerable<string> sourceTexts)
    {
        var perFile = sourceTexts
            .Select(text => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))))
            .OrderBy(hash => hash, StringComparer.Ordinal);
        var combined = string.Join('\n', perFile);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined)));
    }
}

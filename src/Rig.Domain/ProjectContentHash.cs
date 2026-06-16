using System.Security.Cryptography;
using System.Text;

namespace Rig.Domain;

// Deterministic, order-independent digest of a multiset of content items. Drives assembly dedup in the
// multi-solution store: re-indexing skips an assembly whose (name, ContentHash) is already present (see
// docs/multi-solution-storage.md). Callers choose the items:
//   * write-time dedup (slice 2) feeds the assembly's emitted FACT identities (symbol DocIDs + reference
//     target/enclosing/line) — directly captures "did the stored facts change";
//   * extract-time skip (slice 3) feeds the assembly's SOURCE texts — captures "did the source change"
//     before paying for Roslyn.
//
// The digest is the SHA-256 of the sorted multiset of per-item SHA-256 hashes. Consequences:
//   * order-independent — enumeration order never changes the result;
//   * path-independent — portable across checkout directories;
//   * change-sensitive — editing any item, or adding/removing one, changes the multiset and the digest.
public static class ProjectContentHash
{
    public static string Compute(IEnumerable<string> items)
    {
        using var sha = SHA256.Create();
        var perItem = items
            .Select(item => ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(item))))
            .OrderBy(hash => hash, StringComparer.Ordinal);
        var combined = string.Join("\n", perItem);
        return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(combined)));
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            builder.Append(b.ToString("x2"));
        return builder.ToString();
    }
}

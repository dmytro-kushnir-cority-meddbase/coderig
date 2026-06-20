using System.Collections.Concurrent;

namespace Rig.Analysis;

// Deduplicates equal-valued strings to a single retained instance. rig's facts store DocIDs
// (TargetSymbolId / EnclosingSymbolId / SymbolId …), file paths, assembly names and namespaces that
// repeat massively across the fact set: GetDocumentationCommentId() allocates a FRESH string per call,
// so the same target's DocID exists once per reference site (2.1M references on MedDBase) — millions of
// duplicate copies of a few hundred-thousand distinct values. Interning collapses them to one instance
// each, a pure peak-memory win on the retained fact strings (it does NOT change the values written to the
// store). Ordinal comparison (DocIDs are case-sensitive); thread-safe so the dedup pass can run parallel.
public sealed class StringInterner
{
    private readonly ConcurrentDictionary<string, string> _pool = new(StringComparer.Ordinal);

    public string Intern(string value) => _pool.GetOrAdd(value, value);

    public string? InternNullable(string? value) => value is null ? null : _pool.GetOrAdd(value, value);
}

namespace Rig.Analysis.Inventory;

// PURE comparison of two ProjectBuildInfo for the --verify-build-cache guardrail: does a FRESHLY-built
// design-time result match the one a cache HIT would have replayed? This is what no fingerprint unit test
// can prove — that the fingerprint captured every input affecting the build OUTPUT. A mismatch means the
// fingerprint is under-specified (a real input it doesn't fold changed the build), i.e. a latent stale-hit.
//
// The out-of-process build orders references/sources nondeterministically across parallel runs, so each
// list is compared as a SET (order-independent) and Properties as an unordered map — otherwise verify would
// cry wolf on mere ordering. Returns the differing fields so a mismatch report names what drifted. No IO.
internal static class BuildInfoEquivalence
{
    internal sealed record Result(bool IsEquivalent, IReadOnlyList<string> Differences)
    {
        public string Summary => IsEquivalent ? "match" : string.Join(separator: ", ", values: Differences);
    }

    public static Result Compare(ProjectBuildInfo fresh, ProjectBuildInfo cached)
    {
        var diffs = new List<string>();
        CheckSet(diffs, label: "References", fresh: fresh.References, cached: cached.References);
        CheckSet(diffs, label: "ProjectReferences", fresh: fresh.ProjectReferences, cached: cached.ProjectReferences);
        CheckSet(diffs, label: "SourceFiles", fresh: fresh.SourceFiles, cached: cached.SourceFiles);
        CheckSet(diffs, label: "AnalyzerReferences", fresh: fresh.AnalyzerReferences, cached: cached.AnalyzerReferences);
        CheckSet(diffs, label: "PreprocessorSymbols", fresh: fresh.PreprocessorSymbols, cached: cached.PreprocessorSymbols);
        if (!PropertiesEqual(fresh: fresh.Properties, cached: cached.Properties))
        {
            diffs.Add("Properties");
        }

        return new Result(IsEquivalent: diffs.Count == 0, Differences: diffs);
    }

    private static void CheckSet(List<string> diffs, string label, IReadOnlyList<string> fresh, IReadOnlyList<string> cached)
    {
        var f = new HashSet<string>(fresh, StringComparer.Ordinal);
        var c = new HashSet<string>(cached, StringComparer.Ordinal);
        if (f.SetEquals(c))
        {
            return;
        }

        // +added = in fresh but not cached, -removed = in cached but not fresh — the shape of the drift.
        diffs.Add($"{label} (+{f.Except(c, StringComparer.Ordinal).Count()}/-{c.Except(f, StringComparer.Ordinal).Count()})");
    }

    private static bool PropertiesEqual(IReadOnlyDictionary<string, string> fresh, IReadOnlyDictionary<string, string> cached) =>
        fresh.Count == cached.Count
        && fresh.All(kv => cached.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));
}

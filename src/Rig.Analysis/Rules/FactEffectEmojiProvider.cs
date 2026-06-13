namespace Rig.Analysis.Rules;

// Exposes the effect-glyph map from the rules cascade to the CLI rendering layer.
// Lookup: "provider:operation" key first, then "provider" key, then "•" fallback.
public static class FactEffectEmojiProvider
{
    public static IReadOnlyDictionary<string, string> LoadForWorkingDirectory(
        string workingDirectory,
        IReadOnlyList<string>? extraRulesPaths = null
    )
    {
        var anchor = Path.Combine(workingDirectory, "rig.rules.json");
        return AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths).EffectEmoji;
    }

    public static string For(IReadOnlyDictionary<string, string> map, string provider, string operation)
    {
        if (map.TryGetValue($"{provider}:{operation}", out var glyph))
            return glyph;
        if (map.TryGetValue(provider, out glyph))
            return glyph;
        return "•";
    }
}

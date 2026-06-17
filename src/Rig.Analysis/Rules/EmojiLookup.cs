namespace Rig.Analysis.Rules;

// The effect-glyph lookup over the emoji map (carried on RuleSet.EffectEmoji). No loader — the map comes
// from the merged rule set; this is purely the 3-tier lookup: "provider:operation" key first, then
// "provider" key, then "⚡" fallback.
public static class EmojiLookup
{
    public static string For(IReadOnlyDictionary<string, string> map, string provider, string operation)
    {
        if (map.TryGetValue(key: $"{provider}:{operation}", value: out var glyph))
        {
            return glyph;
        }

        if (map.TryGetValue(key: provider, value: out glyph))
        {
            return glyph;
        }

        return "⚡";
    }
}

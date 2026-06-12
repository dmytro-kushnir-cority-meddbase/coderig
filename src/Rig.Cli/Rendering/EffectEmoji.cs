using System.Text.Json;

namespace Rig.Cli.Rendering;

// Maps an effect (provider[:operation]) to a glyph for the tree / effect renderers. Built-in defaults,
// overridable per-working-directory via `rig.effect-emoji.json` or `.rig/effect-emoji.json` — a flat
// JSON object { "llblgen:write": "💾", "soap": "☎️", "throw": "🛑" }. Lookup tries the precise
// "provider:operation" key first, then the "provider" key, then a generic bullet. Detectors (and thus
// provider/operation names) are data-driven, so the glyph map is data too — extend it without a rebuild.
public static class EffectEmoji
{
    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["llblgen:write"] = "💾",
        ["llblgen:read"] = "🔍",
        ["llblgen:fetch"] = "📥",
        ["llblgen:tx_commit"] = "✅",
        ["llblgen:tx_rollback"] = "↩️",
        ["llblgen"] = "🗄️",
        ["soap"] = "☎️",
        ["http"] = "🌐",
        ["queue"] = "📤",
        ["echo_publish"] = "📣",
        ["eventbus"] = "📡",
        ["entity_cache"] = "🗃️",
        ["object_store"] = "📦",
        ["io"] = "📁",
        ["throw"] = "⚠️",
    };

    // Loads the effective glyph map: defaults merged with any per-working-directory override file.
    // A malformed override is ignored (rendering must never fail on a cosmetic config).
    public static IReadOnlyDictionary<string, string> Load(string workingDirectory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Defaults)
            map[kv.Key] = kv.Value;

        foreach (
            var path in new[]
            {
                Path.Combine(workingDirectory, "rig.effect-emoji.json"),
                Path.Combine(workingDirectory, ".rig", "effect-emoji.json"),
            }
        )
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (overrides is not null)
                    foreach (var kv in overrides)
                        map[kv.Key] = kv.Value;
            }
            catch
            {
                // Cosmetic override only — never let a bad file break the query output.
            }
        }
        return map;
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

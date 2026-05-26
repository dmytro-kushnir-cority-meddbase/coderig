using Rig.Analysis;

namespace Rig.Cli.Rendering;

internal static class EffectRenderFormatter
{
    public static string FormatEffect(EffectInfo effect)
    {
        var obs = string.Join(" ", effect.Observations.Select(o => $"[{o.Type}:{o.Context}]"));
        var obsStr = obs.Length > 0 ? $"  {obs}" : "";
        return $"EFFECT {effect.Provider} {effect.Operation}  {effect.Method}  {effect.Resource}{obsStr}";
    }

    public static EffectInfo? FindEffectForBoundary(BoundaryCallInfo boundary, IReadOnlyList<EffectInfo> effects)
    {
        return effects.FirstOrDefault(effect =>
            effect.Line == boundary.Line &&
            string.Equals(effect.FilePath, boundary.FilePath, StringComparison.OrdinalIgnoreCase) &&
            BoundaryMethodMatchesEffect(boundary.Method, effect.Method));
    }

    public static IReadOnlyList<EffectInfo> GetUnmatchedEffects(
        IReadOnlyList<BoundaryCallInfo> boundaries,
        IReadOnlyList<EffectInfo> effects)
    {
        var matched = boundaries
            .Select(boundary => FindEffectForBoundary(boundary, effects))
            .Where(effect => effect is not null)
            .ToHashSet();

        return effects
            .Where(effect => !matched.Contains(effect))
            .ToArray();
    }

    private static bool BoundaryMethodMatchesEffect(string boundaryMethod, string effectMethod)
    {
        return string.Equals(boundaryMethod, effectMethod, StringComparison.Ordinal) ||
            boundaryMethod.EndsWith($".{effectMethod}", StringComparison.Ordinal);
    }
}

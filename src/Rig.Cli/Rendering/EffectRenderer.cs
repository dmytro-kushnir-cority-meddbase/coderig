using Rig.Domain.Data;

namespace Rig.Cli.Rendering;

internal static class EffectRenderer
{
    public static void Render(IReadOnlyList<EffectInfo> effects, int? entryPointIndex, TextWriter output)
    {
        output.WriteLine(entryPointIndex.HasValue ? $"Effects [{entryPointIndex}]" : "Effects");

        foreach (
            var effect in effects
                .OrderBy(e => e.Provider, StringComparer.Ordinal)
                .ThenBy(e => e.Resource, StringComparer.Ordinal)
                .ThenBy(e => e.Operation, StringComparer.Ordinal)
        )
        {
            var obs = string.Join(" ", effect.Observations.Select(o => $"[{o.Type}:{o.Context}]"));
            var obsStr = obs.Length > 0 ? $"  {obs}" : "";
            output.WriteLine(
                $"  {effect.Provider} {effect.Operation}  {effect.Method}  {effect.Resource}  {Path.GetFileName(effect.FilePath)}:{effect.Line}{obsStr}"
            );
        }
    }
}

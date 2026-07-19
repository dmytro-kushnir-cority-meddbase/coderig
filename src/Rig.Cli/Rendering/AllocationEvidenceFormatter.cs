using Rig.Domain.Data;

namespace Rig.Cli.Rendering;

internal static class AllocationEvidenceFormatter
{
    public static string Suffix(DerivedEffect effect)
    {
        if (effect.Provider != "alloc" || string.IsNullOrEmpty(effect.Mechanism))
        {
            return "";
        }

        var cardinality = effect.Cardinality is null or "per_evaluation" ? "" : $", {effect.Cardinality}";
        var size = effect.ShallowSizeBytes is { } bytes ? $", ~{bytes} B" : "";
        return $" [{effect.Mechanism}{cardinality}{size}]";
    }
}

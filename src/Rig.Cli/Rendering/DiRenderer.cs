using Rig.Domain.Data;

namespace Rig.Cli.Rendering;

internal static class DiRenderer
{
    public static void Render(IReadOnlyList<DiRegistrationInfo> registrations, TextWriter output)
    {
        output.WriteLine("DI Registrations");
        foreach (
            var group in registrations
                .OrderBy(r => r.ServiceType, StringComparer.Ordinal)
                .ThenBy(r => r.Lifetime, StringComparer.Ordinal)
                .GroupBy(r => r.ServiceType, StringComparer.Ordinal)
        )
        {
            var registrationsForService = group.ToArray();
            var collectionMarker = registrationsForService.Length > 1 ? $" ({registrationsForService.Length} registrations)" : "";
            output.WriteLine($"  {group.Key}{collectionMarker}");

            foreach (
                var reg in registrationsForService
                    .OrderBy(r => r.ImplementationType ?? "", StringComparer.Ordinal)
                    .ThenBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Line)
            )
            {
                output.WriteLine($"    -> {reg.ImplementationType ?? "(self)"}  lifetime={reg.Lifetime} kind={reg.RegistrationKind}");
                output.WriteLine($"       conf={reg.Confidence} basis={reg.Basis} reason={reg.Reason}");
                output.WriteLine($"       loc={Path.GetFileName(reg.FilePath)}:{reg.Line} evidence={reg.Evidence}");
            }
        }
    }
}

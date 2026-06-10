using System.Xml.Linq;
using Rig.Analysis.Rules;
using Rig.Domain.Data;

namespace Rig.Analysis.Extraction;

/// <summary>
/// Reads XML service descriptor files (MedDBase App_Data/Common/Xml/Services pattern)
/// and produces DiRegistrationInfo entries for each interface→implementation mapping.
///
/// Schema:
///   &lt;Service assembly="..." type="ConcreteClass"&gt;
///     &lt;Implements type="IInterface" /&gt;
///   &lt;/Service&gt;
/// </summary>
internal static class XmlDiMiner
{
    public static IReadOnlyList<DiRegistrationInfo> Mine(AnalysisRuleSet rules)
    {
        if (rules.XmlDiFiles.Count == 0)
            return [];

        var results = new List<DiRegistrationInfo>();
        foreach (var path in rules.XmlDiFiles)
        {
            if (Directory.Exists(path))
                foreach (var file in Directory.EnumerateFiles(path, "*.xml", SearchOption.AllDirectories))
                    ParseFile(file, results);
            else if (File.Exists(path))
                ParseFile(path, results);
        }
        return results;
    }

    private static void ParseFile(string filePath, List<DiRegistrationInfo> results)
    {
        try
        {
            var doc = XDocument.Load(filePath);
            var service = doc.Root;
            if (service is null || service.Name.LocalName != "Service")
                return;

            var impl = service.Attribute("type")?.Value;
            if (string.IsNullOrWhiteSpace(impl))
                return;

            foreach (
                var iface in service
                    .Elements()
                    .Where(e => e.Name.LocalName == "Implements")
                    .Select(e => e.Attribute("type")?.Value)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
            )
            {
                results.Add(
                    new DiRegistrationInfo(
                        ServiceType: iface!,
                        ImplementationType: impl,
                        Lifetime: "singleton",
                        RegistrationKind: "xml_service_descriptor",
                        FilePath: filePath,
                        Line: 0,
                        Confidence: "high",
                        Basis: "xml_config",
                        Reason: "xml_di_miner",
                        Evidence: Path.GetFileName(filePath)
                    )
                );
            }
        }
        catch
        { /* skip malformed files */
        }
    }
}

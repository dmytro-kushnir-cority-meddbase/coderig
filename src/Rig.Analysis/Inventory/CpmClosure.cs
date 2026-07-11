using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Rig.Analysis.Inventory;

// Per-project Central Package Management (CPM) closure — the NuGet analog of PaketClosure. CPM declares
// package versions centrally in Directory.Packages.props (`<PackageVersion Include="X" Version=".."/>`) while
// each project's csproj carries version-LESS `<PackageReference Include="X"/>`. Hashing the whole props into
// every project (the old AncestorConfigFiles behaviour) means one `<PackageVersion>` bump invalidates every
// project under it — the same over-invalidation PaketClosure fixed for Paket. This scopes it: a project folds
// only the central versions for packages IT references, plus the props' global part (properties / imports /
// `<GlobalPackageReference>`, which apply to all). A bump then invalidates only the referencing projects.
//
// Simpler than Paket — no transitive walk: CPM versions only a project's DIRECT references (transitive
// versions are resolved by NuGet into the racy obj/project.assets.json, which we never read). The exception is
// `<CentralPackageTransitivePinningEnabled>`, where `<PackageVersion>` governs transitive deps too and the
// true closure is unknowable from the props alone — then we conservatively fold ALL versions. Likewise if a
// csproj/props can't be parsed for its references: fold all (over-invalidate, never replay stale).
internal static class CpmClosure
{
    // The parsed central props: each `<PackageVersion>` by name (lower) → its element XML (so a version OR
    // condition change flips it); the "global blob" = the props with all `<PackageVersion>` removed (every
    // other change — properties, imports, `<GlobalPackageReference>` — folded wholesale for all CPM projects);
    // and whether transitive pinning is on.
    internal sealed record CpmModel(Dictionary<string, string> Versions, string GlobalBlob, bool TransitivePinning);

    private static readonly ConcurrentDictionary<string, CpmModel?> ModelCache = new(StringComparer.Ordinal);

    // IMPERATIVE SHELL: material for a project's CPM dependencies, or null when the project is NOT under a
    // Directory.Packages.props (not CPM-managed) — then the fingerprint folds nothing for CPM. All IO (up-tree
    // lookup, reads, parse memo) here; the fold is the pure Material core.
    public static string? Compute(string projectFilePath)
    {
        var fullPath = Path.GetFullPath(projectFilePath);
        var projectDir = Path.GetDirectoryName(fullPath) ?? "";

        var propsDir = FindUpwards(startDir: projectDir, fileName: "Directory.Packages.props");
        if (propsDir is null)
        {
            return null;
        }

        var propsPath = Path.Combine(propsDir, "Directory.Packages.props");
        var model = ModelCache.GetOrAdd(MemoKey(propsPath), _ => ReadModel(propsPath));
        if (model is null)
        {
            return null; // transient IO error reading the props — re-read next index
        }

        // The package names this project references: its own csproj, plus any ancestor Directory.Build.props
        // (a shared `<PackageReference>` injected there applies to the project too). null from any source =
        // "couldn't parse" → conservative fold-all in Material.
        var referenced = ReadReferences(fullPath);
        if (referenced is not null)
        {
            for (var dir = projectDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                var dbp = Path.Combine(dir, "Directory.Build.props");
                if (!File.Exists(dbp))
                {
                    continue;
                }

                var injected = ReadReferences(dbp);
                if (injected is null)
                {
                    referenced = null; // unparseable injected refs → fold all to stay safe
                    break;
                }

                referenced.UnionWith(injected);
            }
        }

        return Material(model: model, referenced: referenced);
    }

    // PURE entry for tests + reuse: parse the central props + a csproj's references from raw text and produce
    // the same material the shell folds from disk. No IO.
    internal static string ComputeMaterial(string packagesPropsXml, string csprojXml) =>
        Material(model: ParsePackagesProps(packagesPropsXml), referenced: ParsePackageReferences(csprojXml));

    // PURE CORE: fold the global props blob (settings/imports/global refs — all CPM projects), then the central
    // version line for each package the project references, into deterministic, order-stable material. When
    // transitive pinning is on, or the reference set is unknown (parse failure → null), fold ALL versions —
    // the conservative superset that can only over-invalidate, never replay stale.
    internal static string Material(CpmModel model, IReadOnlyCollection<string>? referenced)
    {
        var foldAll = referenced is null || model.TransitivePinning;
        var keys = (foldAll ? model.Versions.Keys : referenced!).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);

        var material = new StringBuilder();
        material.Append("cpm-settings\n").Append(model.GlobalBlob).Append('\n');
        foreach (var name in keys)
        {
            material.Append(name).Append('|');
            if (model.Versions.TryGetValue(name, out var line))
            {
                material.Append(line);
            }

            material.Append('\n');
        }

        return material.ToString();
    }

    // PURE + total (never throws): parse Directory.Packages.props. On malformed XML, degrade to a model whose
    // global blob is the RAW text (so the whole file is folded wholesale — safe), no scoping.
    internal static CpmModel ParsePackagesProps(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return new CpmModel(
                Versions: new Dictionary<string, string>(StringComparer.Ordinal),
                GlobalBlob: xml,
                TransitivePinning: false
            );
        }

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "PackageVersion").ToList())
        {
            var name = el.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                versions[name.Trim().ToLowerInvariant()] = el.ToString();
            }

            el.Remove(); // strip from the global blob so only referenced versions are folded per project
        }

        var transitivePinning = doc.Descendants()
            .Any(e => e.Name.LocalName == "CentralPackageTransitivePinningEnabled" && bool.TryParse(e.Value, out var on) && on);

        return new CpmModel(Versions: versions, GlobalBlob: doc.ToString(), TransitivePinning: transitivePinning);
    }

    // PURE: the package names a csproj/props references (`<PackageReference Include=…>` or `Update=…`),
    // lower-cased. Null on malformed XML — the caller treats that as "unknown" and folds all versions.
    internal static HashSet<string>? ParsePackageReferences(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var name = el.Attribute("Include")?.Value ?? el.Attribute("Update")?.Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name.Trim().ToLowerInvariant());
            }
        }

        return names;
    }

    private static CpmModel? ReadModel(string path)
    {
        try
        {
            return ParsePackagesProps(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static HashSet<string>? ReadReferences(string path)
    {
        try
        {
            return ParsePackageReferences(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? FindUpwards(string startDir, string fileName)
    {
        for (var dir = startDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, fileName)))
            {
                return dir;
            }
        }

        return null;
    }

    private static string MemoKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{path}|{info.Length}|{File.GetLastWriteTimeUtc(path).Ticks}" : $"{path}|-";
        }
        catch (IOException)
        {
            return $"{path}|-";
        }
    }
}

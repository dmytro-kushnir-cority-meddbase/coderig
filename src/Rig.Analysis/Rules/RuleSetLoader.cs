using System.Text.Json;
using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Loads the rule cascade and projects it to the immutable Rig.Domain RuleSet every receiver consumes.
// This is the single seam rules are loaded through — the decoupling the rest of the system relies on:
// LOADING (file IO + JSON + cascade merge, here, the only layer that can read the JSON authoring model)
// is separated from the immutable REPRESENTATION (RuleSet, in Domain). Within one CLI invocation the
// filesystem is a fixed snapshot, so one load is correct for the whole run — load once at the top of a
// command and pass the RuleSet by value to every receiver; nothing re-loads.
//
// The cascade is: builtin-rules.json -> global ~/.rig/rig.rules.json -> a colocated rig.rules.json ->
// any --rules paths. Documents are folded into one merged document (lists concatenated, the emoji map
// last-write-wins), which is then projected once. There is no per-project rule discovery.
public static class RuleSetLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Load the effective rule set rooted at <workingDirectory> (the directory holding the .rig store, or
    // the solution directory at index time). A non-solution anchor inside the working dir picks up a
    // colocated rig.rules.json on top of the global (~/.rig) + built-in cascade; --rules paths append last.
    public static RuleSet Load(string workingDirectory, IReadOnlyList<string>? extraRules = null)
    {
        var merged = LoadMergedDocument(Anchor(workingDirectory), extraRules, out _);
        return Project(merged);
    }

    // Load variant that ALSO surfaces the files the cascade resolved (built-in + global + local + extras), in
    // load order — the SAME list ResolveLoadedPaths returns. A caller needing both the projected RuleSet AND
    // the fingerprint paths (e.g. `rig derive`) takes this overload to avoid a second cascade merge just to
    // re-resolve the paths. Pass `loadedPaths` to RulesFingerprint.ComputeFromPaths.
    public static RuleSet Load(string workingDirectory, IReadOnlyList<string>? extraRules, out IReadOnlyList<string> loadedPaths)
    {
        var merged = LoadMergedDocument(Anchor(workingDirectory), extraRules, out var paths);
        loadedPaths = paths;
        return Project(merged);
    }

    // Load rooted at a real solution/project path (rather than the working dir): the cascade keys rule
    // discovery off the path's directory, and a .csproj/.fsproj anchor walks ancestors for rig.rules.json.
    // Used where the caller holds the actual solution path (e.g. profile validation), as the old
    // AnalysisRuleSet.LoadForSolution did.
    public static RuleSet LoadForSolution(string solutionPath, IReadOnlyList<string>? extraRules = null)
    {
        var merged = LoadMergedDocument(solutionPath, extraRules, out _);
        return Project(merged);
    }

    // The files the cascade actually resolved (built-in + global + local + extras that exist), in load
    // order. RulesFingerprint hashes these by path + content for cache keys without paying the projection.
    public static IReadOnlyList<string> ResolveLoadedPaths(string workingDirectory, IReadOnlyList<string>? extraRules = null)
    {
        _ = LoadMergedDocument(Anchor(workingDirectory), extraRules, out var loadedPaths);
        return loadedPaths;
    }

    private static string Anchor(string workingDirectory) => Path.Combine(workingDirectory, "_factrules_.slnx");

    private static RuleSet Project(AnalysisRulesDocument doc) =>
        new()
        {
            Handoff = FactHandoffRuleProvider.Project(doc),
            Factory = FactGenericFactoryRuleProvider.Project(doc),
            Cut = FactTraversalCutRuleProvider.Project(doc),
            Context = FactContextDispatchRuleProvider.Project(doc),
            Effects = FactEffectRuleProvider.Project(doc),
            Observations = FactObservationRuleProvider.Project(doc),
            EntryPoints = FactEntryPointRuleProvider.ProjectTypeEntryPoints(doc),
            ClassInheritance = FactEntryPointRuleProvider.ProjectClassInheritance(doc),
            Render = FactRenderRuleProvider.Project(doc),
            Delivery = FactDeliveryRuleProvider.Project(doc),
            EffectEmoji = doc.EffectEmoji is not null
                ? new Dictionary<string, string>(doc.EffectEmoji, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DiRegistrations = doc.DiRegistrations ?? [],
            FileInclude = (doc.Files?.Include ?? []).Select(rule => rule.ToFileRule("include")).ToArray(),
            FileExclude = (doc.Files?.Exclude ?? []).Select(rule => rule.ToFileRule("exclude")).ToArray(),
            TestProjectPatterns = doc.Files?.TestProjectPatterns ?? [],
            ProjectExcludePatterns = doc.Projects?.Exclude ?? [],
            StaticDiMappings = doc.StaticDiMappings ?? [],
            XmlDiFiles = doc.XmlDiFiles ?? [],
        };

    private static AnalysisRulesDocument LoadMergedDocument(
        string anchorPath,
        IReadOnlyList<string>? extraRulesPaths,
        out List<string> loadedPaths
    )
    {
        loadedPaths = [];
        var merged = LoadBuiltIn(loadedPaths);

        var globalRulesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rig", "rig.rules.json");
        merged = MergeWithFile(merged, globalRulesPath, loadedPaths);

        var solutionDirectory = Path.GetDirectoryName(anchorPath) ?? Directory.GetCurrentDirectory();
        var isProjectFile =
            anchorPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || anchorPath.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

        if (isProjectFile)
        {
            var dir = solutionDirectory;
            for (var depth = 0; depth < 8 && dir is not null; depth++)
            {
                var candidate = Path.Combine(dir, "rig.rules.json");
                if (File.Exists(candidate))
                {
                    merged = MergeWithFile(merged, candidate, loadedPaths);
                    break;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        else
        {
            merged = MergeWithFile(merged, Path.Combine(solutionDirectory, "rig.rules.json"), loadedPaths);
        }

        if (extraRulesPaths is not null)
        {
            foreach (var path in extraRulesPaths)
            {
                merged = MergeWithFile(merged, path, loadedPaths);
            }
        }

        return merged;
    }

    private static AnalysisRulesDocument LoadBuiltIn(List<string> loadedPaths)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "builtin-rules.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Rig.Cli", "builtin-rules.json"),
        };

        var rulesPath =
            candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Could not find built-in analysis rules.");

        using var stream = File.OpenRead(rulesPath);
        var document =
            JsonSerializer.Deserialize<AnalysisRulesDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Built-in analysis rules are invalid: {rulesPath}");

        loadedPaths.Add(Path.GetFullPath(rulesPath));
        return document;
    }

    // Merge a single rules file into the accumulator. A missing file is a no-op; a path already loaded is
    // skipped (so a global path that equals the local one isn't double-counted), preserving the original
    // LoadedRulesPaths dedup.
    private static AnalysisRulesDocument MergeWithFile(AnalysisRulesDocument acc, string rulesPath, List<string> loadedPaths)
    {
        var normalizedPath = Path.GetFullPath(rulesPath);
        if (!File.Exists(normalizedPath) || loadedPaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
        {
            return acc;
        }

        using var stream = File.OpenRead(normalizedPath);
        var document = JsonSerializer.Deserialize<AnalysisRulesDocument>(stream, JsonOptions);
        if (document is null)
        {
            return acc;
        }

        loadedPaths.Add(normalizedPath);
        return Merge(acc, document);
    }

    // Fold `next` into `acc`: list sections concatenate, the emoji map is last-write-wins, and string
    // sections (project/test/xml-di) de-duplicate. Mutates `acc` (freshly deserialized per load).
    private static AnalysisRulesDocument Merge(AnalysisRulesDocument acc, AnalysisRulesDocument next)
    {
        acc.Effects = Concat(acc.Effects, next.Effects);
        acc.DiRegistrations = Concat(acc.DiRegistrations, next.DiRegistrations);
        acc.HandoffDispatchers = Concat(acc.HandoffDispatchers, next.HandoffDispatchers);
        acc.DeliveryRules = Concat(acc.DeliveryRules, next.DeliveryRules);
        acc.StaticDiMappings = Concat(acc.StaticDiMappings, next.StaticDiMappings);
        acc.XmlDiFiles = ConcatDistinct(acc.XmlDiFiles, next.XmlDiFiles);
        acc.GenericFactories = Concat(acc.GenericFactories, next.GenericFactories);
        acc.TraversalCuts = Concat(acc.TraversalCuts, next.TraversalCuts);
        acc.ContextDispatch = Concat(acc.ContextDispatch, next.ContextDispatch);
        acc.EntryPoints = MergeEntryPoints(acc.EntryPoints, next.EntryPoints);
        acc.Files = MergeFiles(acc.Files, next.Files);
        acc.Projects = MergeProjects(acc.Projects, next.Projects);
        acc.Observations = MergeObservations(acc.Observations, next.Observations);
        acc.Render = MergeRender(acc.Render, next.Render);
        acc.EffectEmoji = MergeEmoji(acc.EffectEmoji, next.EffectEmoji);
        return acc;
    }

    private static List<T> Concat<T>(List<T>? a, List<T>? b) => [.. a ?? [], .. b ?? []];

    private static List<string> ConcatDistinct(List<string>? a, List<string>? b) =>
        (a ?? []).Concat(b ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static EntryPointRulesDocument? MergeEntryPoints(EntryPointRulesDocument? a, EntryPointRulesDocument? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        return new EntryPointRulesDocument
        {
            ClassInheritance = Concat(a.ClassInheritance, b.ClassInheritance),
            TypeEntryPoints = Concat(a.TypeEntryPoints, b.TypeEntryPoints),
            PageModel = Concat(a.PageModel, b.PageModel),
        };
    }

    private static FileRulesSection? MergeFiles(FileRulesSection? a, FileRulesSection? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        return new FileRulesSection
        {
            Include = Concat(a.Include, b.Include),
            Exclude = Concat(a.Exclude, b.Exclude),
            TestProjectPatterns = ConcatDistinct(a.TestProjectPatterns, b.TestProjectPatterns),
        };
    }

    private static ProjectsSection? MergeProjects(ProjectsSection? a, ProjectsSection? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        return new ProjectsSection { Exclude = ConcatDistinct(a.Exclude, b.Exclude) };
    }

    private static ObservationsSection? MergeObservations(ObservationsSection? a, ObservationsSection? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        return new ObservationsSection
        {
            ReadBeforeCommit = Concat(a.ReadBeforeCommit, b.ReadBeforeCommit),
            ConcurrencyHandled = Concat(a.ConcurrencyHandled, b.ConcurrencyHandled),
            ResilienceRetry = Concat(a.ResilienceRetry, b.ResilienceRetry),
            ResourceSpan = Concat(a.ResourceSpan, b.ResourceSpan),
            SerializationHazard = Concat(a.SerializationHazard, b.SerializationHazard),
            NPlusOne = Concat(a.NPlusOne, b.NPlusOne),
        };
    }

    private static RenderRulesSection? MergeRender(RenderRulesSection? a, RenderRulesSection? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        return new RenderRulesSection
        {
            CollapseSeams = Concat(a.CollapseSeams, b.CollapseSeams),
            OpaqueTypes = Concat(a.OpaqueTypes, b.OpaqueTypes),
        };
    }

    private static Dictionary<string, string>? MergeEmoji(Dictionary<string, string>? existing, Dictionary<string, string>? incoming)
    {
        if (incoming is null || incoming.Count == 0)
        {
            return existing;
        }

        var merged = new Dictionary<string, string>(existing ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var kv in incoming)
        {
            merged[kv.Key] = kv.Value;
        }

        return merged;
    }
}

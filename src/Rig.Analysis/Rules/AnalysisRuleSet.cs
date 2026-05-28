using System.Text.Json;
using System.Text.RegularExpressions;
using Rig.Domain.Functions;

namespace Rig.Analysis.Rules;

internal sealed record AnalysisRuleSet(
    IReadOnlyList<MinimalApiEntryPointRule> MinimalApiEntryPoints,
    IReadOnlyList<MvcHttpAttributeRule> MvcHttpAttributes,
    IReadOnlyList<ClassInheritanceEntryPointRule> ClassInheritanceEntryPoints,
    IReadOnlyList<PageModelEntryPointRule> PageModelEntryPoints,
    IReadOnlyList<EffectRule> Effects,
    IReadOnlyList<DiRegistrationRule> DiRegistrations,
    IReadOnlyList<FileRule> FileInclude,
    IReadOnlyList<FileRule> FileExclude,
    IReadOnlyList<string> TestProjectPatterns,
    IReadOnlyList<string> ProjectExcludePatterns,
    IReadOnlyList<ReadBeforeCommitObservationRule> ReadBeforeCommitObservations,
    IReadOnlyList<ConcurrencyHandledObservationRule> ConcurrencyHandledObservations,
    IReadOnlyList<ResilienceRetryObservationRule> ResilienceRetryObservations,
    IReadOnlyList<string> LoadedRulesPaths
)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static AnalysisRuleSet LoadForSolution(string solutionPath, IReadOnlyList<string>? extraRulesPaths = null)
    {
        var rules = LoadBuiltIn();

        var globalRulesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rig", "rig.rules.json");
        rules = rules.MergeWithFile(globalRulesPath);

        var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();
        // For project files (.csproj/.fsproj), walk up to find rig.rules.json at repo/solution root.
        // For solution files (.sln/.slnx), the rules file is expected right next to the solution.
        var isProjectFile = solutionPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || solutionPath.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);
        if (isProjectFile)
        {
            var dir = solutionDirectory;
            for (var depth = 0; depth < 8 && dir is not null; depth++)
            {
                var candidate = Path.Combine(dir, "rig.rules.json");
                if (File.Exists(candidate))
                {
                    rules = rules.MergeWithFile(candidate);
                    break;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        else
        {
            rules = rules.MergeWithFile(Path.Combine(solutionDirectory, "rig.rules.json"));
        }

        if (extraRulesPaths is not null)
        {
            foreach (var path in extraRulesPaths)
            {
                rules = rules.MergeWithFile(path);
            }
        }

        return rules;
    }

    public AnalysisRuleSet MergeWithProjectDirectories(IReadOnlyList<string> projectDirectories)
    {
        var rules = this;
        foreach (var dir in projectDirectories)
        {
            rules = rules.MergeWithFile(Path.Combine(dir, "rig.rules.json"));
        }
        return rules;
    }

    private AnalysisRuleSet MergeWithFile(string rulesPath)
    {
        var normalizedPath = Path.GetFullPath(rulesPath);
        if (!File.Exists(normalizedPath))
            return this;
        if (LoadedRulesPaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
            return this;

        using var stream = File.OpenRead(normalizedPath);
        var document = JsonSerializer.Deserialize<AnalysisRulesDocument>(stream, JsonOptions);
        if (document is null)
            return this;

        return MergeDocument(document) with
        {
            LoadedRulesPaths = LoadedRulesPaths.Append(normalizedPath).ToArray(),
        };
    }

    private AnalysisRuleSet MergeDocument(AnalysisRulesDocument document)
    {
        return this with
        {
            MinimalApiEntryPoints = MinimalApiEntryPoints.Concat(document.EntryPoints?.MinimalApi ?? []).ToArray(),
            MvcHttpAttributes = MvcHttpAttributes.Concat(document.EntryPoints?.MvcHttpAttributes ?? []).ToArray(),
            ClassInheritanceEntryPoints = ClassInheritanceEntryPoints.Concat(document.EntryPoints?.ClassInheritance ?? []).ToArray(),
            PageModelEntryPoints = PageModelEntryPoints.Concat(document.EntryPoints?.PageModel ?? []).ToArray(),
            Effects = Effects.Concat(document.Effects ?? []).ToArray(),
            DiRegistrations = DiRegistrations.Concat(document.DiRegistrations ?? []).ToArray(),
            FileInclude = FileInclude.Concat(document.Files?.Include?.Select(rule => rule.ToFileRule("include")) ?? []).ToArray(),
            FileExclude = FileExclude.Concat(document.Files?.Exclude?.Select(rule => rule.ToFileRule("exclude")) ?? []).ToArray(),
            TestProjectPatterns = TestProjectPatterns
                .Concat(document.Files?.TestProjectPatterns ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ProjectExcludePatterns = ProjectExcludePatterns
                .Concat(document.Projects?.Exclude ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ReadBeforeCommitObservations = ReadBeforeCommitObservations.Concat(document.Observations?.ReadBeforeCommit ?? []).ToArray(),
            ConcurrencyHandledObservations = ConcurrencyHandledObservations.Concat(document.Observations?.ConcurrencyHandled ?? []).ToArray(),
            ResilienceRetryObservations = ResilienceRetryObservations.Concat(document.Observations?.ResilienceRetry ?? []).ToArray(),
        };
    }

    public FileRule? FindIncludedFile(string relativePath)
    {
        return FileInclude.FirstOrDefault(rule => rule.IsMatch(relativePath));
    }

    public FileRule? FindExcludedFile(string relativePath)
    {
        return FileExclude.LastOrDefault(rule => rule.IsMatch(relativePath));
    }

    public bool IsTestProject(string projectName)
    {
        return TestProjectPatterns.Any(pattern => GlobMatcher.IsMatch(projectName, pattern));
    }

    public bool IsExcludedProject(string projectName)
    {
        return ProjectExcludePatterns.Any(pattern => GlobMatcher.IsMatch(projectName, pattern));
    }

    private static AnalysisRuleSet LoadBuiltIn()
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

        return new AnalysisRuleSet(
            document.EntryPoints?.MinimalApi ?? [],
            document.EntryPoints?.MvcHttpAttributes ?? [],
            document.EntryPoints?.ClassInheritance ?? [],
            document.EntryPoints?.PageModel ?? [],
            document.Effects ?? [],
            document.DiRegistrations ?? [],
            document.Files?.Include?.Select(rule => rule.ToFileRule("include")).ToArray() ?? [],
            document.Files?.Exclude?.Select(rule => rule.ToFileRule("exclude")).ToArray() ?? [],
            document.Files?.TestProjectPatterns ?? [],
            document.Projects?.Exclude ?? [],
            document.Observations?.ReadBeforeCommit ?? [],
            document.Observations?.ConcurrencyHandled ?? [],
            document.Observations?.ResilienceRetry ?? [],
            [Path.GetFullPath(rulesPath)]
        );
    }
}

internal sealed record FileRule(string Id, string Glob, string Reason, Regex Regex)
{
    public bool IsMatch(string relativePath)
    {
        return Regex.IsMatch(relativePath.Replace('\\', '/'));
    }
}

internal sealed record MinimalApiEntryPointRule(string Method, string HttpMethod);

internal sealed record MvcHttpAttributeRule(string Attribute, string HttpMethod);

internal sealed record PageModelEntryPointRule(
    string Id,
    string Kind,
    IReadOnlyList<string> BaseTypes,
    string NamespacePrefix,
    string? DefaultMethod = null
);

internal sealed record ClassInheritanceEntryPointRule(
    string Id,
    string Kind,
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<string> RouteProviderMethods,
    IReadOnlyList<RouteMethodRule> RouteMethods,
    IReadOnlyList<string> HandlerMethods,
    bool RequireOverride,
    string? DefaultMethod = null,
    IReadOnlyList<string>? HandlerParameterTypes = null
);

internal sealed record RouteMethodRule(string Method, string HttpMethod);

internal sealed record EffectRule(
    string Provider,
    string Operation,
    IReadOnlyList<string> Methods,
    IReadOnlyList<string>? DeclaringTypes,
    IReadOnlyList<string>? ReceiverTypes,
    IReadOnlyList<string>? ContainingNamespaces,
    IReadOnlyList<string>? ContainingTypes,
    IReadOnlyList<string>? ContainingMethods,
    string Resource,
    string Confidence,
    string Basis,
    string Reason,
    bool TreatAsDispatch = false
)
{
    public bool Matches(string methodName)
    {
        return Methods.Contains(methodName, StringComparer.Ordinal);
    }
}

internal sealed record DiRegistrationRule(IReadOnlyList<string> Methods, string Lifetime, string RegistrationKind, string Reason)
{
    public bool Matches(string methodName)
    {
        return Methods.Contains(methodName, StringComparer.Ordinal);
    }
}

internal sealed record ReadBeforeCommitObservationRule(
    IReadOnlyList<string> CommitMethods,
    IReadOnlyList<string> ReadMethods,
    IReadOnlyList<string> ReadReceiverTypePatterns
);

internal sealed record ConcurrencyHandledObservationRule(
    IReadOnlyList<string> CommitMethods,
    IReadOnlyList<string> CatchTypePatterns
);

internal sealed record ResilienceRetryObservationRule(
    IReadOnlyList<string> WrapperMethods,
    IReadOnlyList<string> ReceiverTypePatterns
);

internal sealed class AnalysisRulesDocument
{
    public EntryPointRulesDocument? EntryPoints { get; set; }

    public List<EffectRule>? Effects { get; set; }

    public List<DiRegistrationRule>? DiRegistrations { get; set; }

    public FileRulesSection? Files { get; set; }

    public ProjectsSection? Projects { get; set; }

    public ObservationsSection? Observations { get; set; }
}

internal sealed class ObservationsSection
{
    public List<ReadBeforeCommitObservationRule>? ReadBeforeCommit { get; set; }

    public List<ConcurrencyHandledObservationRule>? ConcurrencyHandled { get; set; }

    public List<ResilienceRetryObservationRule>? ResilienceRetry { get; set; }
}

internal sealed class ProjectsSection
{
    public List<string>? Exclude { get; set; }
}

internal sealed class EntryPointRulesDocument
{
    public List<MinimalApiEntryPointRule>? MinimalApi { get; set; }

    public List<MvcHttpAttributeRule>? MvcHttpAttributes { get; set; }

    public List<ClassInheritanceEntryPointRule>? ClassInheritance { get; set; }

    public List<PageModelEntryPointRule>? PageModel { get; set; }
}

internal sealed class FileRulesSection
{
    public List<FileRuleDocument>? Include { get; set; }

    public List<FileRuleDocument>? Exclude { get; set; }

    public List<string>? TestProjectPatterns { get; set; }
}

internal sealed class FileRuleDocument
{
    public string? Id { get; set; }

    public string? Glob { get; set; }

    public string? Reason { get; set; }

    public FileRule ToFileRule(string direction)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new InvalidOperationException($"File rule in `{direction}` is missing `id`.");
        }

        if (string.IsNullOrWhiteSpace(Glob))
        {
            throw new InvalidOperationException($"File rule `{Id}` is missing `glob`.");
        }

        return new FileRule(
            Id,
            Glob,
            string.IsNullOrWhiteSpace(Reason) ? $"{direction}_file_rule" : Reason,
            new Regex(GlobMatcher.ToRegex(Glob), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        );
    }
}

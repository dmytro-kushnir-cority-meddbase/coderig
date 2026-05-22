using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rig.Analysis;

internal sealed record AnalysisRuleSet(
    IReadOnlyList<MinimalApiEntryPointRule> MinimalApiEntryPoints,
    IReadOnlyList<MvcHttpAttributeRule> MvcHttpAttributes,
    IReadOnlyList<ClassInheritanceEntryPointRule> ClassInheritanceEntryPoints,
    IReadOnlyList<EffectRule> Effects,
    IReadOnlyList<DiRegistrationRule> DiRegistrations,
    IReadOnlyList<FileRule> FileInclude,
    IReadOnlyList<FileRule> FileExclude,
    IReadOnlyList<string> TestProjectPatterns)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static AnalysisRuleSet LoadForSolution(string solutionPath)
    {
        var builtIn = LoadBuiltIn();
        var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();
        var rulesPath = Path.Combine(solutionDirectory, "rig.rules.json");
        if (!File.Exists(rulesPath))
        {
            return builtIn;
        }

        using var stream = File.OpenRead(rulesPath);
        var document = JsonSerializer.Deserialize<AnalysisRulesDocument>(stream, JsonOptions);

        return builtIn with
        {
            MinimalApiEntryPoints = builtIn.MinimalApiEntryPoints
                .Concat(document?.EntryPoints?.MinimalApi ?? [])
                .ToArray(),
            MvcHttpAttributes = builtIn.MvcHttpAttributes
                .Concat(document?.EntryPoints?.MvcHttpAttributes ?? [])
                .ToArray(),
            ClassInheritanceEntryPoints = builtIn.ClassInheritanceEntryPoints
                .Concat(document?.EntryPoints?.ClassInheritance ?? [])
                .ToArray(),
            Effects = builtIn.Effects
                .Concat(document?.Effects ?? [])
                .ToArray(),
            DiRegistrations = builtIn.DiRegistrations
                .Concat(document?.DiRegistrations ?? [])
                .ToArray(),
            FileInclude = builtIn.FileInclude
                .Concat(document?.Files?.Include?.Select(rule => rule.ToFileRule("include")) ?? [])
                .ToArray(),
            FileExclude = builtIn.FileExclude
                .Concat(document?.Files?.Exclude?.Select(rule => rule.ToFileRule("exclude")) ?? [])
                .ToArray(),
            TestProjectPatterns = builtIn.TestProjectPatterns
                .Concat(document?.Files?.TestProjectPatterns ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
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

    private static AnalysisRuleSet LoadBuiltIn()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Rules", "builtin-rules.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Rig", "Rules", "builtin-rules.json")
        };

        var rulesPath = candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Could not find built-in analysis rules.");

        using var stream = File.OpenRead(rulesPath);
        var document = JsonSerializer.Deserialize<AnalysisRulesDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Built-in analysis rules are invalid: {rulesPath}");

        return new AnalysisRuleSet(
            document.EntryPoints?.MinimalApi ?? [],
            document.EntryPoints?.MvcHttpAttributes ?? [],
            document.EntryPoints?.ClassInheritance ?? [],
            document.Effects ?? [],
            document.DiRegistrations ?? [],
            document.Files?.Include?.Select(rule => rule.ToFileRule("include")).ToArray() ?? [],
            document.Files?.Exclude?.Select(rule => rule.ToFileRule("exclude")).ToArray() ?? [],
            document.Files?.TestProjectPatterns ?? []);
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

internal sealed record ClassInheritanceEntryPointRule(
    string Id,
    string Kind,
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<string> RouteProviderMethods,
    IReadOnlyList<RouteMethodRule> RouteMethods,
    IReadOnlyList<string> HandlerMethods,
    bool RequireOverride);

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
    string Reason)
{
    public bool Matches(string methodName)
    {
        return Methods.Contains(methodName, StringComparer.Ordinal);
    }
}

internal sealed record DiRegistrationRule(
    IReadOnlyList<string> Methods,
    string Lifetime,
    string RegistrationKind,
    string Reason)
{
    public bool Matches(string methodName)
    {
        return Methods.Contains(methodName, StringComparer.Ordinal);
    }
}

internal sealed class AnalysisRulesDocument
{
    public EntryPointRulesDocument? EntryPoints { get; set; }

    public List<EffectRule>? Effects { get; set; }

    public List<DiRegistrationRule>? DiRegistrations { get; set; }

    public FileRulesSection? Files { get; set; }
}

internal sealed class EntryPointRulesDocument
{
    public List<MinimalApiEntryPointRule>? MinimalApi { get; set; }

    public List<MvcHttpAttributeRule>? MvcHttpAttributes { get; set; }

    public List<ClassInheritanceEntryPointRule>? ClassInheritance { get; set; }
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
            new Regex(GlobMatcher.ToRegex(Glob), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }
}

internal static class GlobMatcher
{
    public static bool IsMatch(string value, string glob)
    {
        return Regex.IsMatch(
            value.Replace('\\', '/'),
            ToRegex(glob),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string ToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var regex = Regex.Escape(normalized)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);

        return $"^{regex}$";
    }
}

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
    IReadOnlyList<string> LoadedRulesPaths,
    // Pre-declared interface→implementation mappings (e.g. from XML service descriptors).
    // These are merged directly into the SingleImplIndex without requiring code-level DI patterns.
    IReadOnlyList<StaticDiMapping> StaticDiMappings,
    // Paths to XML service descriptor directories/files whose mappings are mined at index time.
    IReadOnlyList<string> XmlDiFiles,
    // Curated async-handoff dispatchers (background/timer/actor/event schedulers). When one of these
    // consumes a method-group, the graph layer reclassifies that edge as a handoff (default-cut from
    // synchronous reach; --async walks it tagged). See HandoffClassifier.
    IReadOnlyList<HandoffDispatcherRule> HandoffDispatchers,
    // Codebase-specific `rig tree` render rules (presentation only — never affects reach).
    IReadOnlyList<RenderRule> RenderCollapseSeams,
    IReadOnlyList<RenderRule> RenderOpaqueTypes,
    // Codebase-specific generic-factory monomorphization rules: rewrite a generic factory call edge to
    // its constructed type's method, collapsing the generic plumbing. Affects the call graph (tree /
    // reaches), unlike render rules. See FactGenericFactoryRule.
    IReadOnlyList<GenericFactoryRule> GenericFactories,
    // Traversal-cut rules: nodes matching these patterns are emitted as leaves — their successors are
    // NOT walked. Unlike render rules (presentation-only), these stop the TRAVERSAL so deep infra
    // seams can't steal shallow direct-call expansions. `--raw` bypasses cuts. See FactTraversalCutRule.
    IReadOnlyList<TraversalCutRule> TraversalCuts,
    // Context-bound interface-dispatch rules: narrow a context-interface's dispatch fan-out to the impls
    // bound (via a generic BindingBase<C>) to the ENCLOSING context type. Affects the call graph (tree /
    // reaches). See FactContextDispatchRule.
    IReadOnlyList<ContextDispatchRule> ContextDispatch,
    // Resource-span observation rules: flag a span-sensitive effect (soap/http/io/…) that occurs
    // lexically inside a transaction-`using` or `lock` scope (ordering/nesting). See FactResourceSpanRule.
    IReadOnlyList<ResourceSpanObservationRule> ResourceSpanObservations
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
        var isProjectFile =
            solutionPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
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
            StaticDiMappings = StaticDiMappings.Concat(document.StaticDiMappings ?? []).ToArray(),
            XmlDiFiles = XmlDiFiles.Concat(document.XmlDiFiles ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
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
            ConcurrencyHandledObservations = ConcurrencyHandledObservations
                .Concat(document.Observations?.ConcurrencyHandled ?? [])
                .ToArray(),
            ResilienceRetryObservations = ResilienceRetryObservations.Concat(document.Observations?.ResilienceRetry ?? []).ToArray(),
            ResourceSpanObservations = ResourceSpanObservations.Concat(document.Observations?.ResourceSpan ?? []).ToArray(),
            HandoffDispatchers = HandoffDispatchers.Concat(document.HandoffDispatchers ?? []).ToArray(),
            RenderCollapseSeams = RenderCollapseSeams.Concat(document.Render?.CollapseSeams ?? []).ToArray(),
            RenderOpaqueTypes = RenderOpaqueTypes.Concat(document.Render?.OpaqueTypes ?? []).ToArray(),
            GenericFactories = GenericFactories.Concat(document.GenericFactories ?? []).ToArray(),
            TraversalCuts = TraversalCuts.Concat(document.TraversalCuts ?? []).ToArray(),
            ContextDispatch = ContextDispatch.Concat(document.ContextDispatch ?? []).ToArray(),
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
            [Path.GetFullPath(rulesPath)],
            document.StaticDiMappings ?? [],
            document.XmlDiFiles ?? [],
            document.HandoffDispatchers ?? [],
            document.Render?.CollapseSeams ?? [],
            document.Render?.OpaqueTypes ?? [],
            document.GenericFactories ?? [],
            document.TraversalCuts ?? [],
            document.ContextDispatch ?? [],
            document.Observations?.ResourceSpan ?? []
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
    string? DefaultMethod = null,
    // When set, methods decorated with any of these attributes become the entry points
    // rather than constructors.  Route = <page-route>.<MethodName>(<params>).
    IReadOnlyList<string>? HandlerMethodAttributes = null
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
    IReadOnlyList<string>? HandlerParameterTypes = null,
    // When set, a matched method must additionally carry one of these attributes
    // (e.g. WCF [OperationContract]).  Gates rules with baseTypes:["*"]+handlerMethods:["*"]
    // so they don't match every method in the project.
    IReadOnlyList<string>? HandlerMethodAttributes = null
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
    bool TreatAsDispatch = false,
    // Optional suffix gate on the declaring type's simple name. Narrows a broad namespace-prefix
    // gate: e.g. declaringTypeNameEndsWith:["Proxy"] + declaringTypes:["MedDBase.Pages"] matches
    // XxxProxy.Show() but not MessageBox.Show().
    IReadOnlyList<string>? DeclaringTypeNameEndsWith = null,
    // Optional base-type gate: the declaring type must derive (BFS over base edges) one of these
    // base types. The faithful gate for generated navigation proxies — a Show/ShowDialog/Redirect
    // call is a clientpage_proxy effect iff its declaring type derives MedDBase.Pages.ProxyBase.
    IReadOnlyList<string>? DeclaringTypeBaseTypes = null,
    // When true, match CONSTRUCTOR refs (new XxxEntity(pk[, txn])) rather than invocations — the
    // llblgen entity-ctor fetch (gap G5). Type gates apply to the constructed type; MinArguments
    // separates the fetch ctor from the empty `new XxxEntity()`.
    bool MatchConstructor = false,
    int MinArguments = 0,
    // When true, match THROW refs (`throw new XxxException(...)`) rather than invocations. The type
    // gates (declaringTypes / declaringTypeNameEndsWith / declaringTypeBaseTypes) apply to the THROWN
    // exception type, and the effect resource is that exception type. Surfaces guard/permission exits
    // (e.g. AccessDeniedException) as effects so a read path that drops its check is visible.
    bool MatchThrow = false,
    // Wrapper gate: match an invocation whose TARGET method itself calls one of these patterns (e.g.
    // "Echo.Process.ask") — recognizes request/response wrappers from data, no per-type curation. The
    // effect emits at the wrapper's call sites; resource:type_argument yields the caller's concrete
    // type-arg combo. See FactEffectRule.TargetCallsMethods.
    IReadOnlyList<string>? TargetCallsMethods = null,
    // Selects ONE top-level position (0-based) of the comma-joined type_argument resource instead of
    // the whole combo. Null = whole combo. See FactEffectRule.TypeArgumentIndex.
    int? TypeArgumentIndex = null
);

// A curated async-handoff dispatcher: when its consuming ctor/method is handed a method-group, the
// graph layer reclassifies that edge as a handoff. `consumerPatterns` are substrings matched against
// the (arity-stripped) consuming invocation/ctor target DocID (e.g.
// "RepeatingBackgroundProcessSchedule.#ctor", "Echo.Process.spawn", "IAsyncEvent.Add"); `kind` is the
// execution-origin kind the callback gets (background|timer|actor|event); `repeating` flags a
// re-firing schedule. Projected to FactHandoffRule for the Domain classifier.
internal sealed record HandoffDispatcherRule(string Id, string Kind, IReadOnlyList<string> ConsumerPatterns, bool Repeating = false);

// A `rig tree` render rule: a DocID substring `Pattern` + a human `Label`/`Reason` shown in the
// rendered marker. Used for both collapse-seams (fold a fan-out hub's children) and opaque-types
// (draw a node as a leaf). Codebase-specific presentation data; projected to FactRenderRule.
internal sealed record RenderRule(string Pattern, string? Label = null, string? Reason = null, string? Id = null);

// A traversal-cut rule: a node whose DocID matches `Pattern` is a traversal leaf — emitted but
// successors are not walked. Only pattern + label are required; id/reason are documentation.
// Projected to FactTraversalCutRule by FactTraversalCutRuleProvider.
internal sealed record TraversalCutRule(string Pattern, string? Label = null, string? Reason = null, string? Id = null);

// A generic-factory monomorphization rule: rewrite a call to `Method` (matched as "<declType>.<name>")
// to its constructed type's `TargetMethod`, where the construct is type-arg `ConstructArgIndex`.
// Codebase-specific; projected to FactGenericFactoryRule. `Reason`/`Id` are documentation only.
internal sealed record GenericFactoryRule(
    string Method,
    int ConstructArgIndex = 0,
    string TargetMethod = "New",
    string? Reason = null,
    string? Id = null
);

// A context-bound interface-dispatch rule: impls of `Interface` are each bound to a context type via a
// generic `BindingBase<C>` base, so the interface's dispatch narrows to the enclosing context's family.
// Codebase-specific; projected to FactContextDispatchRule. `Reason`/`Id` are documentation only.
internal sealed record ContextDispatchRule(string Interface, string BindingBase, string? Reason = null, string? Id = null);

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

internal sealed record ConcurrencyHandledObservationRule(IReadOnlyList<string> CommitMethods, IReadOnlyList<string> CatchTypePatterns);

internal sealed record ResilienceRetryObservationRule(IReadOnlyList<string> WrapperMethods, IReadOnlyList<string> ReceiverTypePatterns);

internal sealed record ResourceSpanObservationRule(
    string ScopeKind,
    IReadOnlyList<string> ScopeTypePatterns,
    IReadOnlyList<string> ExcludeProviders,
    string ObservationType,
    string Context
);

// Pre-declared interface→implementation mapping sourced from external DI descriptors
// (e.g. XML service files, web.config appSettings) rather than from code patterns.
internal sealed record StaticDiMapping(
    string ServiceType,
    string ImplementationType,
    string Lifetime = "singleton",
    string RegistrationKind = "static"
);

internal sealed class AnalysisRulesDocument
{
    public EntryPointRulesDocument? EntryPoints { get; set; }

    public List<EffectRule>? Effects { get; set; }

    public List<DiRegistrationRule>? DiRegistrations { get; set; }

    public List<HandoffDispatcherRule>? HandoffDispatchers { get; set; }

    public List<StaticDiMapping>? StaticDiMappings { get; set; }

    // Paths to directories (or individual files) containing XML service descriptors.
    // Each <Service type="Impl"><Implements type="IFace"/></Service> becomes a
    // DiRegistrationInfo entry fed into SingleImplIndex at callgraph build time.
    public List<string>? XmlDiFiles { get; set; }

    public FileRulesSection? Files { get; set; }

    public ProjectsSection? Projects { get; set; }

    public ObservationsSection? Observations { get; set; }

    public RenderRulesSection? Render { get; set; }

    public List<GenericFactoryRule>? GenericFactories { get; set; }

    // Top-level key "traversalCuts": list of {pattern, label, id?, reason?} cut rules.
    public List<TraversalCutRule>? TraversalCuts { get; set; }

    // Top-level key "contextDispatch": list of {interface, bindingBase, id?, reason?} rules.
    public List<ContextDispatchRule>? ContextDispatch { get; set; }
}

// `render` rule section — codebase-specific `rig tree` presentation rules (collapse fan-out hubs,
// draw infra types as opaque leaves). Pure presentation; never affects reach. See FactRenderRules.
internal sealed class RenderRulesSection
{
    public List<RenderRule>? CollapseSeams { get; set; }

    public List<RenderRule>? OpaqueTypes { get; set; }
}

internal sealed class ObservationsSection
{
    public List<ReadBeforeCommitObservationRule>? ReadBeforeCommit { get; set; }

    public List<ConcurrencyHandledObservationRule>? ConcurrencyHandled { get; set; }

    public List<ResilienceRetryObservationRule>? ResilienceRetry { get; set; }

    public List<ResourceSpanObservationRule>? ResourceSpan { get; set; }
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

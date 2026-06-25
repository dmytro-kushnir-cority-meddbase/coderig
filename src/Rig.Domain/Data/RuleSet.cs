using System.Text.RegularExpressions;
using Rig.Domain.Functions;

namespace Rig.Domain.Data;

// The effective, immutable rule blob: the whole cascade (built-in + global ~/.rig + local rig.rules.json
// + --rules) merged ONCE and projected to every collection a receiver consumes. This is the single rule
// currency that crosses every layer — query commands, the graph materializer, and the index/extraction
// pass all take a RuleSet by value. It is pure data: loading lives in Rig.Analysis (RuleSetLoader), which
// is the only layer that can read the JSON authoring model. Construct it there; everyone else receives it.
//
// Slices fall in two families. The Fact* slices (Handoff/Factory/Cut/Context/Effects/Observations/
// EntryPoints/ClassInheritance/Render/Delivery) are the fact-matchable projections the Domain matchers consume. The
// remaining slices (DiRegistrations/File*/TestProjectPatterns/ProjectExcludePatterns/StaticDiMappings/
// XmlDiFiles) are consumed by the index/extraction pass in their authoring form.
public sealed record RuleSet
{
    public IReadOnlyList<FactHandoffRule> Handoff { get; init; } = [];

    // External-virtual-override-orphan redirects (docs/backlog.md): rewrite a call to an external convenience
    // overload to the virtual hatch it trampolines into, applied at the reference→edge projection.
    public IReadOnlyList<FactRedirectRule> Redirect { get; init; } = [];

    // FR-7 cache-coherence POLICY (declared cached entities + an optional generated-ORM-noise namespace-suffix
    // filter) for the cache-coherence INSTANCE of the generic effect-correlation deriver (wired in
    // DeriveCommand). A single object; null when the `cacheCoherence` section is absent.
    public FactCacheCoherenceRule? CacheCoherence { get; init; }

    // write_set_divergence rule: declared EP pairs + write-effect predicates for the structural write-set
    // divergence detector (wired in DeriveCommand). A single object; null when the `writeSetDivergence`
    // section is absent.
    public FactWriteSetDivergenceRule? WriteSetDivergence { get; init; }
    public IReadOnlyList<FactGenericFactoryRule> Factory { get; init; } = [];
    public IReadOnlyList<FactTraversalCutRule> Cut { get; init; } = [];
    public IReadOnlyList<FactContextDispatchRule> Context { get; init; } = [];
    public IReadOnlyList<FactEffectRule> Effects { get; init; } = [];
    public FactObservationRules Observations { get; init; } = new([], [], [], [], [], []);
    public IReadOnlyList<FactEntryPointRule> EntryPoints { get; init; } = [];
    public IReadOnlyList<FactClassInheritanceRule> ClassInheritance { get; init; } = [];
    public FactRenderRules Render { get; init; } = new(CollapseSeams: [], OpaqueTypes: []);
    public IReadOnlyList<DeliveryRule> Delivery { get; init; } = [];
    public IReadOnlyDictionary<string, string> EffectEmoji { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Index/extraction-side slices, consumed in authoring form by SolutionSourceLoader / SourceFileClassifier
    // / XmlDiMiner / DiRegistrationExtractor.
    public IReadOnlyList<DiRegistrationRule> DiRegistrations { get; init; } = [];
    public IReadOnlyList<FileRule> FileInclude { get; init; } = [];
    public IReadOnlyList<FileRule> FileExclude { get; init; } = [];
    public IReadOnlyList<string> TestProjectPatterns { get; init; } = [];
    public IReadOnlyList<string> ProjectExcludePatterns { get; init; } = [];
    public IReadOnlyList<StaticDiMapping> StaticDiMappings { get; init; } = [];
    public IReadOnlyList<string> XmlDiFiles { get; init; } = [];

    public FileRule? FindIncludedFile(string relativePath) => FileInclude.FirstOrDefault(rule => rule.IsMatch(relativePath));

    public FileRule? FindExcludedFile(string relativePath) => FileExclude.LastOrDefault(rule => rule.IsMatch(relativePath));

    public bool IsTestProject(string projectName) =>
        TestProjectPatterns.Any(pattern => GlobMatcher.IsMatch(value: projectName, glob: pattern));

    public bool IsExcludedProject(string projectName) =>
        ProjectExcludePatterns.Any(pattern => GlobMatcher.IsMatch(value: projectName, glob: pattern));
}

public sealed record FileRule(string Id, string Glob, string Reason, Regex Regex)
{
    public bool IsMatch(string relativePath) => Regex.IsMatch(relativePath.Replace('\\', '/'));
}

public sealed record DiRegistrationRule(IReadOnlyList<string> Methods, string Lifetime, string RegistrationKind, string Reason)
{
    public bool Matches(string methodName) => Methods.Contains(methodName, StringComparer.Ordinal);
}

// Pre-declared interface->implementation mapping sourced from external DI descriptors
// (e.g. XML service files, web.config appSettings) rather than from code patterns.
public sealed record StaticDiMapping(
    string ServiceType,
    string ImplementationType,
    string Lifetime = "singleton",
    string RegistrationKind = "static"
);

using System.Text.RegularExpressions;
using Rig.Domain.Data;
using Rig.Domain.Functions;

namespace Rig.Analysis.Rules;

// The JSON authoring model for rig.rules.json (and builtin-rules.json): the shape a human writes, bound
// directly by System.Text.Json. RuleSetLoader loads the cascade of these documents, folds them into one
// merged document, and projects it to the immutable Rig.Domain RuleSet every receiver consumes. These
// types are loader-internal — the merged, projected RuleSet is the only thing that crosses a boundary.

// A type-shaped entry point: a type deriving one of BaseTypes within NamespacePrefix whose construction
// (or DefaultMethod) is an entry point — as opposed to ClassInheritanceEntryPointRule, which keys off
// named handler methods. Generic matching infra; the rule DATA lives in JSON (key `typeEntryPoints`,
// with `pageModel` accepted as a deprecated alias — the original sole use case this generalised from).
internal sealed record TypeEntryPointRule(
    string Id,
    string Kind,
    IReadOnlyList<string> BaseTypes,
    string NamespacePrefix,
    string? DefaultMethod = null,
    // When set, methods decorated with any of these attributes become the entry points rather than the
    // constructor (entry route = <type>.<MethodName>(<params>)).
    IReadOnlyList<string>? HandlerMethodAttributes = null,
    // Capability tokens (JSON `requires`) a deployment must `provides` for EPs from this rule to be
    // active-in it. Null/empty = ungated. Opaque tokens; see Deployments/DeploymentMap.
    IReadOnlyList<string>? Requires = null
);

internal sealed record ClassInheritanceEntryPointRule(
    string Id,
    string Kind,
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<string> HandlerMethods,
    bool RequireOverride,
    string? DefaultMethod = null,
    IReadOnlyList<string>? HandlerParameterTypes = null,
    // When set, a matched method must additionally carry one of these attributes
    // (e.g. WCF [OperationContract]).  Gates rules with baseTypes:["*"]+handlerMethods:["*"]
    // so they don't match every method in the project.
    IReadOnlyList<string>? HandlerMethodAttributes = null,
    // Capability tokens (JSON `requires`) a deployment must `provides` for EPs from this rule to be
    // active-in it. Null/empty = ungated. Opaque tokens; see Deployments/DeploymentMap.
    IReadOnlyList<string>? Requires = null
);

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
    int? TypeArgumentIndex = null,
    // Selects a positional argument (0-based) for the string_argument/argument_name resource instead
    // of the first. Null = argument 0. See FactEffectRule.ArgumentIndex.
    int? ArgumentIndex = null
);

// A curated async-handoff dispatcher: when its consuming ctor/method is handed a method-group, the
// graph layer reclassifies that edge as a handoff. `consumerPatterns` are substrings matched against
// the (arity-stripped) consuming invocation/ctor target DocID (e.g.
// "RepeatingBackgroundProcessSchedule.#ctor", "Echo.Process.spawn", "IAsyncEvent.Add"); `kind` is the
// execution-origin kind the callback gets (background|timer|actor|event); `repeating` flags a
// re-firing schedule. Projected to FactHandoffRule for the Domain classifier.
internal sealed record HandoffDispatcherRule(
    string Id,
    string Kind,
    IReadOnlyList<string> ConsumerPatterns,
    bool Repeating = false,
    // Capability tokens (JSON `requires`) a deployment must `provides` for the handoffs this dispatcher
    // produces to be active-in it. Null/empty = ungated. Opaque tokens; see Deployments/DeploymentMap.
    IReadOnlyList<string>? Requires = null
);

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

    // Top-level key "effectEmoji": flat { "provider:operation": "emoji", "provider": "emoji" } map.
    // Later-loaded files override earlier entries; builtin-rules.json carries the defaults.
    public Dictionary<string, string>? EffectEmoji { get; set; }
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
    public List<ClassInheritanceEntryPointRule>? ClassInheritance { get; set; }

    public List<TypeEntryPointRule>? TypeEntryPoints { get; set; }

    // Deprecated alias for TypeEntryPoints — the original framework-specific key this rule kind
    // generalised from. Still bound and merged so existing rig.rules.json files keep working.
    public List<TypeEntryPointRule>? PageModel { get; set; }
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
            Id: Id,
            Glob: Glob,
            Reason: string.IsNullOrWhiteSpace(Reason) ? $"{direction}_file_rule" : Reason,
            Regex: new Regex(GlobMatcher.ToRegex(Glob), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        );
    }
}

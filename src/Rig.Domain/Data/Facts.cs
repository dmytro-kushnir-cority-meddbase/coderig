namespace Rig.Domain.Data;

// Rule-agnostic, resolved structural facts emitted by stage 1 (the fused Roslyn pass).
// These are the durable artifact rules and queries derive from — see
// docs/fact-layer-refactor.md.

/// <summary>A declared symbol (type, method, property, field, event, namespace).</summary>
/// <remarks>SymbolId is the DocumentationCommentId (DocID) — the global, cross-assembly join key.</remarks>
public sealed record SymbolFact(
    string SymbolId,
    string Kind, // type|method|property|field|event|namespace (the DocID prefix expanded)
    string Name,
    string Namespace,
    string? ContainingSymbolId,
    string Modifiers, // space-joined: static abstract sealed async partial readonly etc.
    string TypeKind, // class|struct|interface|enum|record|delegate|"" for non-types
    string Signature, // human display signature
    string FilePath,
    int Line,
    string DefiningAssembly,
    bool IsOverride
);

/// <summary>A resolved reference to a symbol at a usage site.</summary>
public sealed record ReferenceFact(
    string TargetSymbolId,
    string RefKind, // invocation|ctor|methodGroup|typeUse|read|write|attributeUse
    string? EnclosingSymbolId,
    string TargetAssembly,
    bool TargetInSource, // true => target is first-party (declared in the indexed source set)
    string FilePath,
    int Line
);

/// <summary>A base-type or implemented-interface edge between two types.</summary>
public sealed record TypeRelationFact(
    string TypeSymbolId,
    string RelatedSymbolId,
    string RelationKind // base|interface
);

// --- Stage-3 (read) query projections ---

public sealed record SymbolSearchHit(
    string SymbolId,
    string Kind,
    string Signature,
    string FilePath,
    int Line,
    string DefiningAssembly
);

public sealed record ReferenceHit(
    string TargetSymbolId,
    string RefKind,
    string? EnclosingSymbolId,
    string FilePath,
    int Line,
    bool TargetInSource
);

// A caller->callee edge derived from a reference fact (invocation/methodGroup/ctor).
public sealed record CallEdge(string Caller, string Callee, string Kind, string FilePath, int Line);

// An "implType implements ifaceType" edge (from a type-relation fact).
public sealed record ImplementsEdge(string ImplType, string InterfaceType);

// Minimal method descriptor for interface->concrete resolution.
public sealed record MethodRef(string SymbolId, string Name, string? ContainingTypeId);

// The fact-derived call graph loaded for cross-project path finding (stage 2 over facts).
public sealed record FactGraphData(
    IReadOnlyList<CallEdge> CallEdges,
    IReadOnlyList<ImplementsEdge> ImplementsEdges,
    IReadOnlyList<MethodRef> Methods
);

// One hop in a found path.
public sealed record PathStep(string SymbolId, string Kind, string? FilePath, int Line);

// A method handed off as a delegate (method-group) — a deferred/background entry point the
// structural entry-point rules don't catch (e.g. RepeatingBackgroundProcessSchedule(.., Process)).
public sealed record HandoffEntryPoint(string Target, string RegisteredIn, string FilePath, int Line);

// An effect re-derived from the reference index by matching an invocation target against the
// encoded effect rules (stage 2 over facts).
public sealed record DerivedEffect(
    string Provider, string Operation, string ResourceType, string? EnclosingSymbolId, string FilePath, int Line);

// An entry point re-derived from facts (type_relation_facts BFS + symbol_facts + reference_facts).
// Covers the two pageModel cases: constructor-per-overload (page kind) and
// attribute-decorated methods (action kind).
public sealed record DerivedEntryPoint(
    string Kind,   // e.g. "page" or "action"
    string Method, // e.g. "PAGE" or "ACTION"
    string Route,  // e.g. "Accounts/MakePaymentComponents/Create2"
    string DisplayName, // e.g. "page PAGE Accounts/MakePaymentComponents/Create2(pkInvoice)"
    string FilePath,
    int Line
);

// Fact-matchable projection of a pageModel entry-point rule (from AnalysisRuleSet.PageModel).
// The generic BFS deriver (FactEntryPointDeriver) consumes these — no hardcoded type lists.
public sealed record FactEntryPointRule(
    string Id,
    string Kind,       // "page" or "action"
    string DefaultMethod, // "PAGE" or "ACTION"
    IReadOnlyList<string> BaseTypes,  // BFS roots (e.g. "MMS.Web.UI.ClientPage")
    string NamespacePrefix, // strip prefix from namespace to build route (e.g. "MedDBase.Pages.")
    // When set: methods decorated with any of these attribute DocID prefixes are action entry points.
    // When null/empty: the rule emits constructor-overload page entry points instead.
    IReadOnlyList<string> HandlerMethodAttributePrefixes
);

// Fact-matchable projection of a classInheritance entry-point rule (from
// AnalysisRuleSet.ClassInheritanceEntryPoints). Backend handlers — background/service/WCF/HTTP/
// actor/lifecycle — whose declaring type derives one of the base types (BFS over base AND
// interface edges) and whose name matches a handler method. This is the rule family that took
// backend projects from 0 entry points (see docs/effect-capture-validation.md, gap G1).
//
// Fact-layer scope vs. the Roslyn pass: routeProviderMethods / routeMethods / handlerParameterTypes
// are NOT projected — no real rule uses them, and the fact route falls back to the declaring type's
// FQN + ".Method" (exactly the Roslyn fallback when no route provider matches). Attribute gating is
// supported via HandlerMethodAttributePrefixes, but only first-party attribute refs survive indexing
// (System.* attributes like [OperationContract] are dropped by the runtime-assembly filter), so a
// WCF rule gated on a third-party attribute matches in the fixture but not yet in the real index.
public sealed record FactClassInheritanceRule(
    string Id,
    string Kind,          // "background" | "wcf" | "http" | "echoactor" | "startup" ...
    string DefaultMethod, // "RUN" | "INVOKE" | "POST" ...
    IReadOnlyList<string> BaseTypes,      // BFS roots; ["*"] disables the base-type gate
    IReadOnlyList<string> HandlerMethods, // exact method names; ["*"] matches any name
    bool RequireOverride,                 // when true, only override methods qualify
    // Attribute DocID prefixes (e.g. "M:System.ServiceModel.OperationContractAttribute."); when set,
    // a matched method must additionally carry one of these attributes.
    IReadOnlyList<string> HandlerMethodAttributePrefixes,
    // Simple (un-namespaced) parameter-type names the method must ALL carry (e.g. "ServerCallContext"
    // for the gRPC rule). Matched against the fact Signature's parameter-type tokens by simple name —
    // the discriminator that stops a baseTypes:["*"]/handlerMethods:["*"] rule from matching every
    // override. Without honoring it the gRPC rule would degrade to "every override method".
    IReadOnlyList<string> HandlerParameterTypeSimpleNames
);

// The fact-matchable projection of an effect rule — the same rule data the Roslyn pass uses
// (AnalysisRuleSet.Effects), reduced to what stage-1 facts can match: the method name and the
// type gates. Carries rule data into the (Analysis-agnostic) Domain deriver so effect detection
// stays data-driven — see docs/fact-layer-refactor.md and the "detectors are data" agreement.
public sealed record FactEffectRule(
    string Provider,
    string Operation,
    IReadOnlyList<string> Methods,
    IReadOnlyList<string> DeclaringTypes,
    IReadOnlyList<string> ReceiverTypes,
    // Optional suffix gate: the declaring type's simple name (last segment) must end with one
    // of these suffixes. Used to narrow a broad namespace-prefix gate — e.g. "Proxy" narrows
    // declaringTypes:["MedDBase.Pages"] so it matches XxxProxy.Show() but not MessageBox.Show().
    IReadOnlyList<string>? DeclaringTypeNameEndsWith = null,
    // Optional base-type gate: the declaring type must be a subclass (BFS over base edges) of one
    // of these base types. The faithful gate for generated navigation proxies — a call is a
    // clientpage_proxy effect iff its declaring type derives MedDBase.Pages.ProxyBase (the base of
    // every generated <Page>Proxy). Requires the deriver to be given base edges + the generated
    // proxy source to be indexed. When set, it is authoritative (AND-ed with any suffix gate).
    IReadOnlyList<string>? DeclaringTypeBaseTypes = null);

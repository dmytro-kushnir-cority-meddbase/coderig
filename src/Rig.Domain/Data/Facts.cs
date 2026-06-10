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
    int Line,
    // Static type of the invocation receiver (open-generic FQN, e.g.
    // "StackExchange.Redis.IDatabase"), captured at member-access invocation sites. Lets the
    // stage-2 effect deriver gate `receiverTypes` on the real receiver instead of approximating
    // it with the target's declaring type. Null for bare/static calls and non-invocation refs.
    string? ReceiverType = null,
    // First-argument string template: a string literal verbatim, or an interpolated string reduced
    // to its template (e.g. "https://billing.example/invoices/{teamId}"). Captured for invocations
    // (feeds the stage-2 `http_argument` / `string_argument` resource resolution, P2a) and for
    // attribute usages — recorded as "ctor" refs — whose first positional arg is the MVC route
    // literal (`[Route("..")]`, `[HttpGet("..")]`, feeds the MVC entry-point route, P1d/P2). Null
    // when the first argument is not a string-shaped literal/interpolation, or there is none.
    string? FirstArgumentTemplate = null,
    // Static type of the first argument (open-generic FQN). Feeds the stage-2 `argument_type`
    // resource resolution (P2a) — e.g. the message type passed to a queue dispatch. Null when there
    // is no first argument.
    string? FirstArgumentType = null,
    // --- Structural-context facts for the stage-2 observation deriver (P1c → P2b). Rule-agnostic
    //     raw structure mirroring the ancestor walks in the Roslyn EffectObservationExtractor;
    //     captured for invocation refs only (observations attach to invocation effects). ---
    // Nearest enclosing loop kind ("foreach"|"for"|"while") and its detail string (for foreach,
    // "{identifier} in {expression}"). Feeds looped_effect. Null when not inside a loop.
    string? EnclosingLoopKind = null,
    string? EnclosingLoopDetail = null,
    // The chain of enclosing invocations (ancestor InvocationExpressions, innermost-first), each
    // encoded as receiverText/receiverType/methodName and joined into one string. Feeds
    // parallel_fanout (receiverText "Task"/"Parallel" + method "WhenAll"/"ForEach..") and
    // resilience_retry (receiver TYPE pattern + wrapper method). Null when not nested in any
    // member-access invocation. Decode with FactStructuralContext.
    string? EnclosingInvocations = null,
    // Caught exception-type FQNs of all enclosing try/catch clauses, joined via FactStructuralContext.
    // concurrency_handled (catch-type pattern at a commit site). Null when not inside a try/catch.
    string? EnclosingCatchTypes = null
);

/// <summary>A base-type or implemented-interface edge between two types.</summary>
public sealed record TypeRelationFact(
    string TypeSymbolId,
    string RelatedSymbolId,
    string RelationKind // base|interface
);

// --- Stage-3 (read) query projections ---

public sealed record SymbolSearchHit(string SymbolId, string Kind, string Signature, string FilePath, int Line, string DefiningAssembly);

public sealed record ReferenceHit(
    string TargetSymbolId,
    string RefKind,
    string? EnclosingSymbolId,
    string FilePath,
    int Line,
    bool TargetInSource
);

// A caller->callee edge derived from a reference fact (invocation/methodGroup/ctor).
// LoopKind/LoopDetail carry the caller-side enclosing loop of the call SITE (from the reference
// fact's EnclosingLoopKind/Detail): when set, this call happens inside a foreach/for/while in the
// caller's body, so everything reachable through it is fanned out. Null for non-looped call sites
// and for the synthetic dispatch hops (interface->impl, base->override). Optional so existing
// constructions stay source-compatible.
public sealed record CallEdge(
    string Caller,
    string Callee,
    string Kind,
    string FilePath,
    int Line,
    string? LoopKind = null,
    string? LoopDetail = null,
    // Static type of the invocation receiver at this call site (open-generic FQN, e.g.
    // "T:MedDBase.CompanyEntity"), mined into ReferenceFactEntity.ReceiverType. Lets virtual/
    // base/interface dispatch be resolved EDGE-AWARE: an `entity.Save()` whose receiver is
    // CompanyEntity dispatches only to CompanyEntity's Save override (+ its subtypes), not to
    // all 114 CommonEntityBase.Save overrides (CHA over-approximation). Null for bare/static
    // calls and non-invocation refs; falls back to full CHA when null/interface/error-type/base.
    string? ReceiverType = null
);

// An "implType implements ifaceType" edge (from a type-relation fact).
public sealed record ImplementsEdge(string ImplType, string InterfaceType);

// A "subType derives baseType" edge (from a "base" type-relation fact). Drives base-virtual/
// abstract -> override dispatch in the call graph (G6/G3).
public sealed record BaseEdge(string SubType, string BaseType);

// Minimal method descriptor for interface->concrete and base->override resolution.
// IsOverride gates override-dispatch so base.M reaches only subtypes that actually override M.
public sealed record MethodRef(string SymbolId, string Name, string? ContainingTypeId, bool IsOverride = false);

// The fact-derived call graph loaded for cross-project path finding (stage 2 over facts).
public sealed record FactGraphData(
    IReadOnlyList<CallEdge> CallEdges,
    IReadOnlyList<ImplementsEdge> ImplementsEdges,
    IReadOnlyList<MethodRef> Methods,
    // subType -> baseType edges; enables base-virtual/abstract -> override dispatch. Defaults to
    // empty so existing constructions stay source-compatible.
    IReadOnlyList<BaseEdge>? BaseEdges = null
);

// One hop in a found path. LoopKind/LoopDetail describe the enclosing loop of the call that
// reached this step (i.e. the parent invoked it inside a foreach/for/while). Null for the entry
// step, dispatch hops, and non-looped calls. Fanout = the dispatch fan-out degree of the edge that
// reached this step: when the reaching edge is an impl-/override-dispatch that fanned the source
// method out to N(>1) targets, Fanout=N (the step is one of N siblings, not a single concrete call);
// 0 for direct calls and single-target dispatch. Surfaces edge provenance (D3) so a `base.M()` hop
// that explodes to all overrides is visibly a fan-out, not a real call.
public sealed record PathStep(
    string SymbolId,
    string Kind,
    string? FilePath,
    int Line,
    string? LoopKind = null,
    string? LoopDetail = null,
    int Fanout = 0
);

// A node in a call TREE rooted at an entry point (rig tree). EdgeKind/LoopKind describe the call
// that reached this node from its parent (EdgeKind="entry" for a root; "invocation"/"impl-dispatch"/
// "override-dispatch"; LoopKind set when that call sits inside a loop). Truncated=true marks a node
// whose subtree was NOT expanded because the method was already expanded elsewhere (cycle / shared
// callee — shown as "seen") or a depth/budget cap was hit.
public sealed record TraceNode(
    string SymbolId,
    string EdgeKind,
    string? LoopKind,
    string? LoopDetail,
    IReadOnlyList<TraceNode> Children,
    bool Truncated = false,
    // Dispatch fan-out degree of the edge that reached this node from its parent: N(>1) when that
    // edge is an impl-/override-dispatch that fanned its source method out to N targets (this node
    // is one of N siblings — D3 edge provenance), else 0. Lets the renderer mark a fan-out hop
    // (e.g. base.Save() -> all *Entity.Save) distinctly from a real call.
    int Fanout = 0
);

// A method handed off as a delegate (method-group) — a deferred/background entry point the
// structural entry-point rules don't catch (e.g. RepeatingBackgroundProcessSchedule(.., Process)).
public sealed record HandoffEntryPoint(string Target, string RegisteredIn, string FilePath, int Line);

// An invocation reference fact, with the enrichment fed to the stage-2 effect/observation derivers
// (P1a–P1c). Replaces the positional tuple that grew past readability. Receiver/FirstArgument feed
// resource resolution (P2a); the Enclosing* fields feed the observation deriver (P2b).
public sealed record FactInvocation(
    string Target,
    string? Enclosing,
    string FilePath,
    int Line,
    string? Receiver = null,
    string? FirstArgTemplate = null,
    string? FirstArgType = null,
    string? LoopKind = null,
    string? LoopDetail = null,
    string? EnclosingInvocations = null,
    string? CatchTypes = null
);

// An effect re-derived from the reference index by matching an invocation target against the
// encoded effect rules (stage 2 over facts). Observations are the fact-derived structural notes
// (looped_effect / parallel_fanout / resilience_retry / concurrency_handled, P2b); empty for
// constructor-fetch effects (mirrors the Roslyn path, which attaches observations to invocations).
public sealed record DerivedEffect(
    string Provider,
    string Operation,
    string ResourceType,
    string? EnclosingSymbolId,
    string FilePath,
    int Line,
    IReadOnlyList<EffectObservationInfo>? Observations = null
);

// Fact-side projections of the observation rules (the same AnalysisRuleSet.*Observations data the
// Roslyn EffectObservationExtractor uses), carried into the Domain observation deriver (P2b) so
// observation detection stays data-driven. read_before_commit is deferred (cross-invocation
// ordering; EF-only — not the LLBLGen/MedDBase target). The parallel-fanout list is still supplied
// by the Analysis provider (hardcoded there today, like the Roslyn pass) pending its move to rule
// data in P2c.
public sealed record FactResilienceRetryRule(IReadOnlyList<string> WrapperMethods, IReadOnlyList<string> ReceiverTypePatterns);

public sealed record FactConcurrencyHandledRule(IReadOnlyList<string> CommitMethods, IReadOnlyList<string> CatchTypePatterns);

// One fanout wrapper: receiver source text (e.g. "Task"/"Parallel") + the wrapping methods
// (e.g. "WhenAll" / "ForEach"/"ForEachAsync"). Context = "{Receiver}.{method}".
public sealed record FactParallelFanoutRule(string Receiver, IReadOnlyList<string> Methods);

public sealed record FactObservationRules(
    IReadOnlyList<FactResilienceRetryRule> ResilienceRetry,
    IReadOnlyList<FactConcurrencyHandledRule> ConcurrencyHandled,
    IReadOnlyList<FactParallelFanoutRule> ParallelFanout
);

// An entry point re-derived from facts (type_relation_facts BFS + symbol_facts + reference_facts).
// Covers the two pageModel cases: constructor-per-overload (page kind) and
// attribute-decorated methods (action kind).
public sealed record DerivedEntryPoint(
    string Kind, // e.g. "page" or "action"
    string Method, // e.g. "PAGE" or "ACTION"
    string Route, // e.g. "Accounts/MakePaymentComponents/Create2"
    string DisplayName, // e.g. "page PAGE Accounts/MakePaymentComponents/Create2(pkInvoice)"
    string FilePath,
    int Line
);

// Fact-matchable projection of a pageModel entry-point rule (from AnalysisRuleSet.PageModel).
// The generic BFS deriver (FactEntryPointDeriver) consumes these — no hardcoded type lists.
public sealed record FactEntryPointRule(
    string Id,
    string Kind, // "page" or "action"
    string DefaultMethod, // "PAGE" or "ACTION"
    IReadOnlyList<string> BaseTypes, // BFS roots (e.g. "MMS.Web.UI.ClientPage")
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
    string Kind, // "background" | "wcf" | "http" | "echoactor" | "startup" ...
    string DefaultMethod, // "RUN" | "INVOKE" | "POST" ...
    IReadOnlyList<string> BaseTypes, // BFS roots; ["*"] disables the base-type gate
    IReadOnlyList<string> HandlerMethods, // exact method names; ["*"] matches any name
    bool RequireOverride, // when true, only override methods qualify
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
    IReadOnlyList<string>? DeclaringTypeBaseTypes = null,
    // When true, this rule matches CONSTRUCTOR references (RefKind="ctor") instead of invocations —
    // for llblgen entity-constructor fetches: `new XxxEntity(pk[, txn])` is a read, but it is a ctor
    // call, not a Fetch() invocation, so the method-name rules can't see it (gap G5). The type gates
    // (declaringTypes namespace / declaringTypeBaseTypes EntityBase2) apply to the CONSTRUCTED type;
    // MinArguments distinguishes the fetch ctor (pk arg) from the empty `new XxxEntity()`.
    bool MatchConstructor = false,
    int MinArguments = 0,
    // When true, match THROW refs (RefKind="throw") — a `throw new XxxException(...)` site — instead
    // of invocations. The type gates apply to the THROWN exception type (parsed from the throw ref's
    // target type DocID); the resource is that exception type. Surfaces guard/permission exits (e.g.
    // AccessDeniedException) as effects — a read path that drops its check is then visibly missing it.
    bool MatchThrow = false,
    // Enclosing-method gates (P2a) — mirror the Roslyn MatchesContainingNamespace/Type/Method. The
    // effect counts only when the enclosing method's namespace / declaring type / name matches.
    // Parsed from the reference's EnclosingSymbolId DocID; type/namespace matching is equality +
    // prefix (no base-chain walk — the fact layer has no base edges for the *containing* type, so a
    // containingTypes rule that relies on inheritance is a known fidelity gap).
    IReadOnlyList<string>? ContainingNamespaces = null,
    IReadOnlyList<string>? ContainingTypes = null,
    IReadOnlyList<string>? ContainingMethods = null,
    // Resource-resolution strategy (P2a), mirroring the Roslyn EffectExtractor.TryCreateEffect
    // switch, resolved from facts: "receiver_type" -> the receiver's static type (P1a);
    // "argument_type" -> the first argument's static type (P1b); "string_argument" -> the first
    // argument's string template (P1b); "http_argument" -> that template, scheme/slash-normalized.
    // The "ef_*" strategies are EF-specific and not resolvable from current facts (deferred — they
    // resolve to null). When the strategy resolves to null/empty the effect is DROPPED, exactly as
    // the Roslyn path drops a null resource — this is what aligns fact effects with index effects.
    string Resource = "",
    // When true the rule drives call-graph dispatch, not an effect; the Roslyn FindEffects skips it.
    // The fact effect deriver skips it too so dispatch rules don't leak in as effects.
    bool TreatAsDispatch = false
);

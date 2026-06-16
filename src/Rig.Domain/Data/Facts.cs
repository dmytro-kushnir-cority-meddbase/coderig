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
    string? EnclosingCatchTypes = null,
    // Generic TYPE ARGUMENTS at the call site, comma-joined (e.g. `ask<PaymentGatewayResponse<T>>(..)`
    // → "PaymentGatewayResponse<T>"). Concrete at direct call sites; a type-PARAMETER name inside a
    // generic helper (the caller's concrete binding is recovered separately — see B2). Feeds the
    // stage-2 `type_argument` resource (e.g. the asked/published message type). Null for non-generic calls.
    string? TypeArguments = null,
    // The first argument rendered as its member/identifier path when it is a member access or plain
    // identifier (e.g. `tell(PaymentGatewayProcessDns.AccountService, msg)` → "PaymentGatewayProcessDns.
    // AccountService") — the routing target / discriminator, concrete even inside a generic helper.
    // Feeds the stage-2 `argument_name` resource. Null when the first arg is a literal/other shape.
    string? FirstArgumentName = null,
    // For a METHOD-GROUP ref handed as an ARGUMENT to a call/`new` (`new BackgroundProcessSchedule(..,
    // EndOfTerm, ..)`, `Process.spawn("w", Handle)`), the DocID of that consuming invocation/constructor
    // — resolved STRUCTURALLY (ancestor walk), so it is independent of line placement: a multi-line
    // `new(\n .., Callback,\n ..)` links identically to a single-line one. This is the
    // `DelegateConsumer` the async-handoff classifier matches against handoffDispatchers
    // ConsumerPatterns (HandoffClassifier), replacing the old exact-same-line co-location heuristic that
    // missed multi-line registrations (e.g. AgedState.RegisterTermEndProcess → EndOfTerm). Null for
    // non-methodGroup refs and for method-groups that are NOT a call argument (a `+=` handler, a
    // delegate field/local assignment, a return).
    string? DelegateConsumer = null,
    // The chain of enclosing held-resource scopes (ancestor `using`/`lock` statements, innermost-
    // first), each encoded as kind/resource-type and joined into one string. Feeds the resource_span
    // observation (P2b ordering/nesting): a network/IO effect nested in a transaction-`using` or a
    // `lock` is held across that effect ("transaction spans a network call" / "lock held across IO").
    // Null when the invocation is not inside any using/lock. Decode with FactStructuralContext.
    string? EnclosingScopes = null,
    // ALL positional arguments' string templates and member/identifier name paths, index-aligned with
    // the call's argument list, each serialized as a JSON string?[] (comma-safe — unlike the
    // TypeArguments comma-join, an argument string literal can itself contain commas). Feed the
    // nth-argument resource resolution (FactEffectRule.ArgumentIndex) for resources past position 0
    // (e.g. CertificateEntity.HasRight(cert, Rights.X.Y, txn) — the right is arg 1). Null for
    // non-invocation refs and zero-arg calls. Index 0 mirrors FirstArgumentTemplate/Name (kept as the
    // unindexed fast path so the existing derivation is byte-for-byte unchanged).
    string? ArgumentTemplates = null,
    string? ArgumentNames = null,
    // --- Generic monomorphization bindings (RENDERING ONLY) — let the tree label show the real
    //     instantiation (`QueryPipeline<Account, Invoice>.Create<Entity, Account>`) instead of the
    //     arity-synthesized `<T, U>` placeholders, propagated down a call chain of static factories,
    //     generic methods, and instance calls alike. Each is a JSON string[] of per-position tokens:
    //       "C:Ns.Type<…>" — a CONCRETE type at the call site (namespace-stripped at render),
    //       "T:n"          — the n-th type parameter of the ENCLOSING method's containing TYPE,
    //       "M:n"          — the n-th type parameter of the ENCLOSING method itself,
    //       "?"            — unresolvable (a composite like `Seq<T>`); that position keeps its placeholder.
    //     The renderer resolves T:/M: tokens against the PARENT node's already-resolved declaring/method
    //     concretes (a concrete entry seeds the chain with C: tokens; inner forwarding hops carry T:/M:).
    //     Both null for non-generic callees and BCL callees (gated by inSource — only first-party renders). ---
    // The callee's DECLARING TYPE instantiation at the call site (from target.ContainingType.TypeArguments):
    // the receiver/qualifier type's args for an instance/static call, the constructed type for a ctor.
    string? DeclaringTypeArgBinding = null,
    // The callee's own METHOD type arguments at the call site (from the constructed method's TypeArguments;
    // explicit or inferred). Null for non-generic methods.
    string? MethodTypeArgBinding = null
);

/// <summary>A base-type or implemented-interface edge between two types.</summary>
public sealed record TypeRelationFact(
    string TypeSymbolId,
    string RelatedSymbolId,
    string RelationKind // base|interface
);

/// <summary>
/// An EXACT member-level dispatch edge mined by Roslyn at extraction: SourceMember (a base virtual /
/// interface method DocID) dispatches at runtime to TargetMember (the override / implementing method
/// DocID). Kind = "override" (IMethodSymbol.OverriddenMethod, the immediate base→override hop) or
/// "impl" (INamedTypeSymbol.FindImplementationForInterfaceMember). Both are signature-exact and
/// generic-correct (IFoo`1.M(`0) → Bar.M(System.Int32)) — the member correspondence that name/arity
/// CHA matching can only guess at (and got wrong for same-name overloads). Query-time dispatch uses
/// these FIRST (Basis="roslyn") and falls back to the name/arity CHA heuristic only where Roslyn
/// couldn't bind (net48 error-typed `!:` interfaces / unmined members), marking those "heuristic".
/// </summary>
public sealed record DispatchFact(string SourceMember, string TargetMember, string Kind);

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
    string? ReceiverType = null,
    // The id of the handoffDispatchers rule that classified this edge as an async HANDOFF — a
    // delegate (method-group) handed to a dispatcher (a background/timer/actor/event scheduler) to
    // run LATER / on another thread, not invoked synchronously here. Set ONLY when Kind=="handoff"
    // (HandoffClassifier rewrote a dispatcher-consumed methodGroup edge); null for every ordinary
    // edge. Sync-cut traversal skips Kind=="handoff" edges; --async walks them carrying this as the
    // HandoffVia provenance. The callback target is a first-class execution origin (a root).
    string? HandoffDispatcher = null,
    // Call-site generic type arguments of THIS edge (comma-joined display FQNs, mined into
    // ReferenceFactEntity.TypeArguments). Concrete at a direct call like `Entity.New<Account,int,
    // AccountRecord>` (-> "…Account,int,…AccountRecord"); a forwarded type-PARAMETER token (e.g.
    // "TConstruct") inside a generic body. Carried forward by the traversal as a path-scoped binding of
    // concrete types in scope, so a downstream GENERIC dispatch hub (e.g. `Construct`2.New`, CHA-fanned
    // to all entity constructors) is narrowed to the candidate whose declaring type is one of those
    // concretes — `Account.New` — instead of the full fan-out. Null for synthesized dispatch edges and
    // non-generic calls.
    string? TypeArguments = null,
    // For a Kind=="methodGroup" edge, the DocID of the invocation/constructor this delegate is handed to
    // as an argument (ReferenceFact.DelegateConsumer, mined by ancestor walk — line-placement-agnostic).
    // The async-handoff classifier matches this against handoffDispatchers ConsumerPatterns to reclassify
    // the edge as Kind=="handoff"; it then becomes irrelevant. Null for non-methodGroup edges, for
    // method-groups that are not a call argument, and on stores indexed before this fact existed (the
    // classifier falls back to same-line co-location there).
    string? DelegateConsumer = null,
    // Generic monomorphization bindings (RENDERING only) — the callee's declaring-type and own-method
    // type-arg tokens at this call site (ReferenceFact.DeclaringTypeArgBinding / MethodTypeArgBinding,
    // JSON string[] of C:/T:/M:/? tokens). Carried onto the reached node so the renderer can resolve the
    // forwarded T:/M: positions against the parent node's instantiation and substitute the label's
    // declaring + method arity placeholders. Do NOT affect dispatch (that uses the open `ReceiverType`).
    string? DeclaringTypeArgBinding = null,
    string? MethodTypeArgBinding = null
);

// An "implType implements ifaceType" edge (from a type-relation fact).
public sealed record ImplementsEdge(string ImplType, string InterfaceType);

// A "subType derives baseType" edge (from a "base" type-relation fact). Drives base-virtual/
// abstract -> override dispatch in the call graph (G6/G3).
public sealed record BaseEdge(string SubType, string BaseType);

// Minimal method descriptor for interface->concrete and base->override resolution.
// IsOverride gates override-dispatch so base.M reaches only subtypes that actually override M.
// FilePath/Line are the method's DEFINITION location (from symbol_facts), surfaced by `rig tree --files`
// so each node links to its source. Default null/0 keeps synthetic/test constructions source-compatible.
public sealed record MethodRef(
    string SymbolId,
    string Name,
    string? ContainingTypeId,
    bool IsOverride = false,
    string? FilePath = null,
    int Line = 0
);

// A reference to a target symbol from within an enclosing method, at a source location. Covers ctor
// refs (RefKind="ctor": constructor calls + attribute applications) and throw refs (RefKind="throw") —
// both feed the effect/entry-point derivers keyed by this identical (Target, Enclosing, FilePath, Line)
// shape, and were previously two structurally-identical 4-tuples. Enclosing is null when the ref has no
// resolved enclosing method (most callers filter those out at the query).
public sealed record SymbolRef(string Target, string? Enclosing, string FilePath, int Line);

// A declared method symbol (symbol_facts kind="method") with the metadata the entry-point deriver needs:
// page EPs use the .ctor rows; class-inheritance EPs use the named-handler rows (IsOverride gates
// RequireOverride rules; Signature feeds parameter-type matching). Distinct from MethodRef (the call-graph
// descriptor) — this carries Signature and is keyed for EP derivation, not dispatch resolution.
public sealed record MethodSymbol(
    string SymbolId,
    string Name,
    string? ContainingSymbolId,
    string Signature,
    string FilePath,
    int Line,
    bool IsOverride
);

// A declared type symbol (symbol_facts kind="type") for page EPs where the class has no explicit ctor.
// IsAbstract gates out base/abstract pages, which are never navigable entry points.
public sealed record TypeSymbol(string SymbolId, string Namespace, string FilePath, int Line, bool IsAbstract);

// A call SITE (Caller, FilePath, Line) that contains an event read — a `someEvent += Handler`. Mined by
// Reads.EventSubscriptionSitesAsync and intersected with method-group edges by
// FactPathFinder.MarkEventSubscriptionHandoffs so the handler subtree is treated as a deferred handoff
// rather than a synchronous call. Lives in Domain because the shaping consumer is a Domain function.
public sealed record EventSubscriptionSite(string Caller, string FilePath, int Line);

// The fact-derived call graph loaded for cross-project path finding (stage 2 over facts).
public sealed record FactGraphData(
    IReadOnlyList<CallEdge> CallEdges,
    IReadOnlyList<ImplementsEdge> ImplementsEdges,
    IReadOnlyList<MethodRef> Methods,
    // subType -> baseType edges; enables base-virtual/abstract -> override dispatch. Defaults to
    // empty so existing constructions stay source-compatible.
    IReadOnlyList<BaseEdge>? BaseEdges = null,
    // EXACT Roslyn-mined dispatch edges (dispatch_facts). When present, DispatchTargets resolves
    // virtual/interface dispatch from these FIRST (Basis="roslyn") and uses the name/arity CHA scan
    // only as a flagged fallback for members with no mined edge (Basis="heuristic"). Null/empty =>
    // behaves like before this fact existed (pure CHA, all heuristic) — old stores and synthetic
    // test graphs degrade gracefully.
    IReadOnlyList<DispatchFact>? MinedDispatch = null,
    // Graph SHAPING carried ON the graph so EVERY traversal — forward (reaches/tree/path) or reverse
    // (callers) — honours the identical shaping, instead of each command deciding it independently (the
    // old split where `callers` walked the raw graph and saw a different reach than `path`). Set by
    // FactPathFinder.ShapeGraph at load. CutRules: nodes whose successors are not walked (reflection /
    // service-locator seams) — applied symmetrically (forward: a leaf; reverse: never a predecessor).
    // ContextRules: context-bound interface-dispatch narrowing (state-family). The generic-FACTORY
    // rewrite needs no field — it is baked into CallEdges by ShapeGraph. Null => unshaped (the `--raw`
    // path, and the sound CHA superset `dead` requires). Default null keeps synthetic test graphs
    // source-compatible.
    IReadOnlyList<FactTraversalCutRule>? CutRules = null,
    IReadOnlyList<FactContextDispatchRule>? ContextRules = null
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
    int Fanout = 0,
    // The dispatcher id of the async HANDOFF edge that reached this step (Kind=="handoff"), or null
    // for a synchronous hop. Only populated under --async traversal — sync-cut never crosses a
    // handoff edge. Lets `rig path --async` render the cross-thread hop (⤳ via <dispatcher>).
    string? HandoffVia = null,
    // Provenance of the dispatch edge that reached this step: "roslyn" (exact, mined at extraction)
    // or "heuristic" (name/arity CHA fallback — Roslyn couldn't bind the interface/base; ~99%
    // correct, verify). Null for non-dispatch hops. Lets `rig path` flag inferred hops.
    string? DispatchBasis = null
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
    int Fanout = 0,
    // The dispatcher id when the edge that reached this node is an async HANDOFF (EdgeKind=="handoff"):
    // the callback was scheduled, not called. Only present under --async (sync-cut prunes the edge),
    // so the tree renderer can show "⤳ via <dispatcher>" at the cross-thread boundary. Null otherwise.
    string? HandoffVia = null,
    // Provenance of the dispatch edge that reached this node from its parent: "roslyn" (exact mined
    // fact) or "heuristic" (name/arity CHA fallback). Null for non-dispatch edges. The tree renderer
    // marks heuristic hops («impl-dispatch ~heuristic») so the user knows the hop was inferred.
    string? DispatchBasis = null,
    // Number of distinct call sites under the SAME parent that resolve to this identical edge (same
    // callee + edge kind + loop + handoff + fan-out + basis). A generic method or bodied accessor
    // invoked N times from one parent collapses to a single child carrying N, instead of 1 expansion
    // + N-1 "↺seen" duplicate leaves. 1 for an ordinary single-call edge.
    int CallSites = 1,
    // Set by the render-time single-impl fold: when an interface/base method dispatched to EXACTLY one
    // target, that lone interface hop is collapsed into its impl, and this carries the folded-away
    // interface's short name for a "«via IFoo»" marker. Null when the node was not folded.
    string? FoldedVia = null,
    // Generic monomorphization bindings (from CallEdge): the callee's declaring-type and own-method
    // type-arg tokens (JSON string[] of C:/T:/M:/?) at the call site that reached this node. RENDERING
    // ONLY: the renderer resolves T:/M: tokens against the PARENT node's resolved instantiation and
    // substitutes both arity groups of this node's label. Null for dispatch hops / non-generic callees.
    string? DeclaringTypeArgBinding = null,
    string? MethodTypeArgBinding = null
);

// A method handed off as a delegate (method-group) — a deferred/background entry point the
// structural entry-point rules don't catch (e.g. RepeatingBackgroundProcessSchedule(.., Process)).
// Dispatcher/Kind are set when the handoff was CLASSIFIED against the handoffDispatchers rule set
// (Dispatcher = the matching rule id; Kind = background|timer|actor|event); both null for the
// unclassified-methodGroup residual (a delegate handed to something outside the curated set).
public sealed record HandoffEntryPoint(
    string Target,
    string RegisteredIn,
    string FilePath,
    int Line,
    string? Dispatcher = null,
    string? Kind = null,
    // Capability tokens (from the producing dispatcher rule) a deployment must `provides` for this
    // handoff to ACTIVATE there — active-in vs merely loaded-in. Null/empty = ungated. See DeploymentMap.
    IReadOnlyList<string>? Requires = null
);

// A handoff-dispatcher rule (the fact-matchable projection of a `handoffDispatchers` JSON entry):
// a curated dispatcher whose CONSUMING ctor/method, when it is handed a method-group, makes that
// method-group an async handoff rather than a synchronous call. ConsumerPatterns are matched as
// substrings against the (generic-arity-stripped) DocID of the consuming invocation/ctor target
// (e.g. "RepeatingBackgroundProcessSchedule.#ctor", "Echo.Process.spawn", "IAsyncEvent.Add"). Kind
// is the execution-origin kind the promoted callback gets (background|timer|actor|event); Repeating
// flags a re-firing schedule (vs one-shot). Rule data, not code — see the "detectors are data"
// agreement; the generic matcher lives in HandoffClassifier.
public sealed record FactHandoffRule(
    string Id,
    string Kind,
    IReadOnlyList<string> ConsumerPatterns,
    bool Repeating = false,
    // Capability tokens a deployment must `provides` (ANY-intersection) for the handoffs this dispatcher
    // produces to be active-in that deployment. Null/empty = ungated (active wherever loaded). The
    // tokens are opaque strings to rig — a generic per-deployment gate, not a coderig concept.
    IReadOnlyList<string>? Requires = null
);

// Codebase-specific RENDER knowledge for `rig tree` — presentation rules, NOT analysis facts. They
// only change what the tree DRAWS; the underlying reach is untouched and stays exact. Loaded from the
// `render` rule section (cascaded via --rules) and projected from AnalysisRuleSet, exactly like
// FactHandoffRule. Ships EMPTY, so a codebase with no curated render rules always sees the raw exact
// tree — the abstraction is the codebase author's data, never a hardcoded heuristic. `rig tree --raw`
// bypasses these. Patterns are case-insensitive substrings of a node's DocID (rig's pattern convention).
//   - CollapseSeams match a fan-out HUB (e.g. a reflection service-locator's interface method, or an
//     ORM entity-constructor factory): the hub's candidate children are folded into ONE summary leaf
//     carrying the union of their effects + a hidden-line count, instead of N polymorphic subtrees.
//   - OpaqueTypes match a type/namespace whose internals aren't worth expanding (e.g. an ORM query
//     builder): a matching node is drawn as a leaf — its own effects still print, its subtree does not.
public sealed record FactRenderRules(IReadOnlyList<FactRenderRule> CollapseSeams, IReadOnlyList<FactRenderRule> OpaqueTypes)
{
    public static readonly FactRenderRules Empty = new([], []);

    public bool IsEmpty => CollapseSeams.Count == 0 && OpaqueTypes.Count == 0;

    public FactRenderRule? MatchCollapseSeam(string symbolId) => FirstMatch(CollapseSeams, symbolId);

    public FactRenderRule? MatchOpaque(string symbolId) => FirstMatch(OpaqueTypes, symbolId);

    private static FactRenderRule? FirstMatch(IReadOnlyList<FactRenderRule> rules, string symbolId)
    {
        if (rules.Count == 0)
            return null;
        // Match against the DocID with the parameter list stripped, so a namespace/type pattern (e.g.
        // "Echo.") hits the DECLARING type only — never a parameter type in the signature (an app method
        // `M:App.Foo.Bar(Echo.ProcessId)` must NOT match "Echo.").
        var paren = symbolId.IndexOf('(');
        var head = paren >= 0 ? symbolId.Substring(0, paren) : symbolId;
        foreach (var rule in rules)
            if (head.IndexOf(rule.Pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                return rule;
        return null;
    }
}

// One render rule: a DocID substring `Pattern` + a short `Label` shown in the rendered marker
// (e.g. «opaque: ORM» / [seam: reflection service-locator]).
public sealed record FactRenderRule(string Pattern, string Label);

// A traversal-cut rule: a node whose DocID matches `Pattern` is emitted as-is but its successors
// are NOT walked — it becomes a traversal leaf. This stops reflection service-locator seams (and
// similar infra) from exploding the tree AND prevents their deep expansion from stealing shallow
// direct calls (problem 1). Unlike render rules (presentation-only), this affects the TRAVERSAL
// itself. `--raw` bypasses cuts so the exact plumbing is inspectable.
public sealed record FactTraversalCutRule(string Pattern, string Label)
{
    // True when `symbolId` matches this cut rule. Matches against the DocID head (parameter list
    // stripped before the first '('), so a namespace/type pattern never accidentally matches a
    // parameter type in the signature. Case-insensitive, same convention as FactRenderRules.
    public bool IsMatch(string symbolId)
    {
        var paren = symbolId.IndexOf('(');
        var head = paren >= 0 ? symbolId.Substring(0, paren) : symbolId;
        return head.IndexOf(Pattern, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

// A generic-FACTORY resolution rule (codebase-specific, data-driven). A call to `Method` with a
// CONCRETE type argument is monomorphized at the call site: the edge is rewritten to point straight at
// the constructed type's `TargetMethod`, bypassing the generic plumbing the factory forwards through.
// E.g. `Entity.New<Account,int,AccountRecord>(pk)` (Method = "MedDBase.DataAccessTier.Entity.New",
// ConstructArgIndex = 0, TargetMethod = "New") rewrites the caller's edge to `Account.New`, so the
// reader never walks Entity.New``3 -> EntityCache`3.New -> ItemCache`3.Get -> Construct`2.New (-> ×N
// entity ctors). Where the type arg ISN'T concrete (a forwarded type parameter inside another generic
// helper) there is nothing to resolve, so the edge is left intact and the in-memory generic-dispatch
// narrowing (carried type-arg binding) remains the fallback. `Method` is matched as the callee's
// "<declaringType>.<name>" (arity-agnostic); the rewrite picks the construct type's `TargetMethod`
// overloads whose arity matches the factory call's, falling back to keeping the edge when none resolve.
public sealed record FactGenericFactoryRule(string Method, int ConstructArgIndex, string TargetMethod);

// A context-bound interface-dispatch rule (codebase-specific, data-driven). Some interfaces are
// implemented only by types each bound to a "context" type via a generic base `BindingBase<C>`, and a
// field of the interface type on a context object can only ever hold an impl bound to THAT context.
// E.g. IWorkflowState is implemented by state classes `AgedState : WorkflowStateBase<InvoiceDebtChase
// .Controller>`, and an InvoiceDebtChase.Controller's `State` field only ever holds InvoiceDebtChase
// states. A naive interface dispatch of `this.State.RegisterEvents()` fans to ALL ~14 state impls
// across unrelated workflows; this rule narrows it to the states bound to the ENCLOSING controller.
// `Interface` and `BindingBase` are matched as DocID substrings (e.g. "IWorkflowState",
// "WorkflowStateBase"); the controller type is recovered from the `BindingBase{C}` base edge's type
// argument. Recall-safe: narrowing applies only when the carried controller is a known context type
// with a non-empty family, else the full CHA fan-out stands (so an impl reached without a matching
// context is never wrongly dropped).
public sealed record FactContextDispatchRule(string Interface, string BindingBase);

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
    string? CatchTypes = null,
    // Call-site generic type arguments (comma-joined) and the first-argument member/identifier path.
    // Feed the `type_argument` / `argument_name` effect-resource strategies (P2a). See ReferenceFact.
    string? TypeArguments = null,
    string? FirstArgName = null,
    // Enclosing held-resource scope chain (using/lock), innermost-first. Feeds the resource_span
    // observation. Decode with FactStructuralContext.DecodeScopes. See ReferenceFact.EnclosingScopes.
    string? EnclosingScopes = null,
    // ALL arguments' string templates / member-identifier names as JSON string?[] (see
    // ReferenceFact.ArgumentTemplates/ArgumentNames). Feed nth-argument resource resolution.
    string? ArgumentTemplates = null,
    string? ArgumentNames = null
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

// A resource-span observation rule (P2b, ordering/nesting): an effect that occurs LEXICALLY INSIDE a
// held-resource scope yields an observation proving the resource is held across that effect. The
// scope is a `using`/`lock` whose KIND equals ScopeKind and whose resource type matches one of
// ScopeTypePatterns (substring; empty = any type, used for `lock`). Filtering is by DENY-LIST, not
// allow-list: every effect provider is flagged EXCEPT those in ExcludeProviders — the scope's own
// expected family (DB ops inside a transaction, in-memory ops a lock protects, the lock/tx effects
// themselves). Flag-by-default is the safe direction: a newly-added external provider is flagged
// without a rule edit (an allow-list would silently miss it). Pure syntactic nesting from the
// captured EnclosingScopes facts — manual `begin`…`commit` with NO `using` block is a separate
// intra-method-sequence case, NOT covered here.
public sealed record FactResourceSpanRule(
    string ScopeKind, // "using" | "lock"
    IReadOnlyList<string> ScopeTypePatterns, // substrings the scope resource type must match; empty = any
    IReadOnlyList<string> ExcludeProviders, // providers NOT flagged (the scope's expected family); all others flagged
    string ObservationType, // emitted observation type, e.g. "transaction_spans_effect"
    string Context // observation context label, e.g. "transaction" / "lock"
);

public sealed record FactObservationRules(
    IReadOnlyList<FactResilienceRetryRule> ResilienceRetry,
    IReadOnlyList<FactConcurrencyHandledRule> ConcurrencyHandled,
    IReadOnlyList<FactParallelFanoutRule> ParallelFanout,
    IReadOnlyList<FactResourceSpanRule> ResourceSpan
);

// An entry point re-derived from facts (type_relation_facts BFS + symbol_facts + reference_facts).
// Covers the two type-entry-point cases: constructor-per-overload (page kind) and
// attribute-decorated methods (action kind).
public sealed record DerivedEntryPoint(
    string Kind, // e.g. "page" or "action"
    string Method, // e.g. "PAGE" or "ACTION"
    string Route, // e.g. "Accounts/MakePaymentComponents/Create2"
    string DisplayName, // e.g. "page PAGE Accounts/MakePaymentComponents/Create2(pkInvoice)"
    string FilePath,
    int Line,
    // Capability tokens inherited from the producing rule; a deployment activates this EP only if it
    // `provides` one of them (active-in). Null/empty = ungated. See DeploymentMap.ActiveServices.
    IReadOnlyList<string>? Requires = null
);

// Fact-matchable projection of a typeEntryPoints rule (from AnalysisRuleSet.TypeEntryPoints).
// The generic BFS deriver (FactEntryPointDeriver) consumes these — no hardcoded type lists.
public sealed record FactEntryPointRule(
    string Id,
    string Kind, // "page" or "action"
    string DefaultMethod, // "PAGE" or "ACTION"
    IReadOnlyList<string> BaseTypes, // BFS roots (e.g. "MMS.Web.UI.ClientPage")
    string NamespacePrefix, // strip prefix from namespace to build route (e.g. "MedDBase.Pages.")
    // When set: methods decorated with any of these attribute DocID prefixes are action entry points.
    // When null/empty: the rule emits constructor-overload page entry points instead.
    IReadOnlyList<string> HandlerMethodAttributePrefixes,
    // Capability tokens a deployment must `provides` for EPs from this rule to be active-in it (active-in
    // vs loaded-in). Null/empty = ungated. Opaque to rig; see DeploymentMap.
    IReadOnlyList<string>? Requires = null
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
    IReadOnlyList<string> HandlerParameterTypeSimpleNames,
    // Capability tokens a deployment must `provides` for EPs from this rule to be active-in it (active-in
    // vs loaded-in). Null/empty = ungated. Opaque to rig; see DeploymentMap.
    IReadOnlyList<string>? Requires = null
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
    bool TreatAsDispatch = false,
    // WRAPPER gate (data-driven, no per-type curation): match an invocation whose TARGET method is
    // itself a method that calls one of these patterns (substring over the called DocID, e.g.
    // "Echo.Process.ask"). Identifies request/response WRAPPERS — a generic helper like
    // `AccountsService<TReply,TMsg>(TMsg msg) => ask<…<TReply>>(pid, msg)` is recognized because it
    // calls ask, and the effect is emitted at the wrapper's CALL SITES, where `resource:type_argument`
    // resolves to the caller's CONCRETE type-arg combo (TReply,TMsg) — the message+reply contract the
    // raw `ask<R>(pid, object)` discards. Method-name / declaring-type gates are ignored when set.
    IReadOnlyList<string>? TargetCallsMethods = null,
    // Selects ONE top-level position of the comma-joined `type_argument` resource (0-based) instead of
    // the whole combo. Null = the whole combo (echo wrappers, where <TReply,TMsg> together is the
    // contract). 0 = the leading type arg — e.g. `Entity.New<TConstruct,TPk,TRecord>` whose signature
    // pins the constructed entity to position 0, so the effect resolves to that one type at the
    // concrete call site (entity_cache:read Account) rather than the CHA-fanned per-entity aggregate.
    // Only consulted when Resource == "type_argument".
    int? TypeArgumentIndex = null,
    // Selects ONE positional argument (0-based) for the `string_argument` / `argument_name` resource
    // instead of the first. Null = argument 0 (the existing first-argument fast path). Lets a rule pull
    // a resource that lives past position 0 — e.g. CertificateEntity.HasRight(cert, Rights.X.Y, txn)
    // exposes the permission right at arg 1 via `resource:argument_name, argumentIndex:1`. Resolved
    // from the JSON ArgumentNames/ArgumentTemplates lists. Only consulted for those two strategies.
    int? ArgumentIndex = null
);

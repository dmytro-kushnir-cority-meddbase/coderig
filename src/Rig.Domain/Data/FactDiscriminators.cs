namespace Rig.Domain.Data;

// Canonical values for the fact discriminators — the SINGLE source of truth shared by the extractor
// (which PRODUCES them), storage (the TEXT columns + EF `Where` filters), and the in-memory traversal
// (which switches on them). These were previously magic-string literals scattered across ~40 sites; a
// typo (`"methodGroup"` vs `"methodgroup"`) silently broke dispatch/handoff classification with no
// compiler error — the review's top correctness concern.
//
// `const string` rather than `enum` deliberately: most comparison sites are EF LINQ `Where(r =>
// r.RefKind == X)` translated to SQL, where a compile-time constant folds into the query unchanged but
// an enum would force an entity/domain split + value converters. The values ARE the on-disk contract
// (reference_facts.RefKind / call_edges.Kind / dispatch_facts.Kind / type_relation_facts.RelationKind /
// symbol_facts.Kind) — do NOT change a value without re-mining every store.

// reference_facts.RefKind — how a reference uses its target.
public static class RefKinds
{
    public const string Invocation = "invocation";
    public const string MethodGroup = "methodGroup";
    public const string Ctor = "ctor";
    public const string TypeUse = "typeUse";
    public const string Read = "read";
    public const string Write = "write";
    public const string AttributeUse = "attributeUse";
    public const string Throw = "throw";

    // A method/type name appearing as the argument of `nameof(...)`. `nameof` only extracts a
    // compile-time STRING — the symbol is never converted to a delegate nor invoked through that
    // reference — so this is recorded (the name WAS referenced) but DELIBERATELY EXCLUDED from the
    // call/dispatch graph (it is not in the call-edge RefKind set in Reads.LoadFactGraphAsync). Keeps
    // `path`/`callers`/`reaches` from treating e.g. a `nameof(Handler)` in a static menu map as a real
    // call edge into Handler (the 2026-06-16 path/callers over-reach bug).
    public const string NameOf = "nameof";
}

// call_edges.Kind — a call edge inherits its originating ref's kind (invocation/methodGroup/ctor); the
// HandoffClassifier rewrites dispatcher-consumed method-group edges to Handoff.
public static class EdgeKinds
{
    public const string Invocation = RefKinds.Invocation;
    public const string MethodGroup = RefKinds.MethodGroup;
    public const string Ctor = RefKinds.Ctor;
    public const string Handoff = "handoff";

    // Synthesized by FactDelegateFieldJoin: caller->callable across a delegate field. A plain edge (not
    // a handoff), so the sync traversal walks it like an ordinary call.
    public const string DelegateField = "delegate-field";
}

// CallEdge.DeliveryPrecision — set ONLY on publish→consumer delivery handoff edges emitted by
// FactPathFinder.AddDeliveryEdges (event_raise / actor_tell). Records whether the producer→handler
// binding is unambiguous or a symbol-blind fan-out, so the traversal can trust the exact ones and
// quarantine the imprecise ones from the default --async walk. Null on every non-delivery edge.
public static class DeliveryPrecisions
{
    // The channel resolved to exactly ONE handler — the producer→handler edge is unambiguous.
    public const string Exact = "exact";

    // The channel resolved to MANY handlers and the join is by symbol/name only (no instance/call-site
    // identity), so the producer fans out to every subscriber of that symbol regardless of which caller
    // wired which handler. Empirically the source of false reach (see docs/FIX-event-raise-overapproximation.md).
    public const string Fanout = "fanout";
}

// dispatch_facts.Kind — the member-level correspondence direction.
public static class DispatchKinds
{
    public const string Impl = "impl"; // interface method -> implementing member
    public const string Override = "override"; // base/virtual method -> overriding member
    public const string DelegateBind = "delegate_bind"; // delegate field/property/event slot -> bound target (18c)

    // Delegate-FIELD join inputs, keyed at the FIELD slot (`F:`), for FactDelegateFieldJoin. Fields only —
    // events stay on the event_raise path. Bind = field -> assigned callable (in-type assignment). Escape =
    // field -> field, emitted for an out-of-type assignment, poisoning the slot so the join is suppressed.
    // Invoke = field -> invoking method (in-type invocation).
    public const string DelegateFieldBind = "delegate_field_bind";
    public const string DelegateFieldEscape = "delegate_field_escape";
    public const string DelegateFieldInvoke = "delegate_field_invoke";
}

// type_relation_facts.RelationKind.
public static class RelationKinds
{
    public const string Base = "base";
    public const string Interface = "interface";
}

// symbol_facts.Kind.
public static class SymbolKinds
{
    public const string Method = "method";
    public const string Type = "type";

    // A field symbol (IFieldSymbol => "field" in FactExtractor). The `F:` DocID prefix's expanded kind; used
    // by the static_init_capture detector to load the static-field universe.
    public const string Field = "field";
}

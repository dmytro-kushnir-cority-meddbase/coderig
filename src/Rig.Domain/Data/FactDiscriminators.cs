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
}

// call_edges.Kind — a call edge inherits its originating ref's kind (invocation/methodGroup/ctor); the
// HandoffClassifier rewrites dispatcher-consumed method-group edges to Handoff.
public static class EdgeKinds
{
    public const string Invocation = RefKinds.Invocation;
    public const string MethodGroup = RefKinds.MethodGroup;
    public const string Ctor = RefKinds.Ctor;
    public const string Handoff = "handoff";
}

// dispatch_facts.Kind — the member-level correspondence direction.
public static class DispatchKinds
{
    public const string Impl = "impl"; // interface method -> implementing member
    public const string Override = "override"; // base/virtual method -> overriding member
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
}

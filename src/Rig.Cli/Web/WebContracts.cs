namespace Rig.Cli.Web;

// JSON contracts for the /api surface. Deliberately flat, camelCased by the ASP.NET default serializer, and
// decoupled from the internal Rig.Domain records (TraceNode/DerivedEffect) so the wire shape can evolve
// independently of the engine — and so this whole folder lifts cleanly to a standalone Rig.Web later.

// One aggregated effect on a method: distinct provider:operation with the glyph and the number of static
// call-SITES (matching `rig` semantics — sites in code, not runtime executions).
internal sealed record EffectDto(string Provider, string Operation, string Glyph, int Sites);

internal sealed record TreeNodeDto(
    string Id, // SymbolId (DocID) — the stable identity for client-side collapse/re-root state.
    string Name, // ShortName — display label.
    string Signature, // compact param signature "(SiteId, ITransaction)" — shown in the "signatures" render mode.
    string Guards, // control-dependence predicate gating this call (⎇), empty if must-run — the "predicates" mode.
    string EdgeKind, // how this node was reached from its parent ("entry"/"invocation"/"impl-dispatch"/…).
    int Fanout, // dispatch fan-out degree of the reaching edge (>1 = "could be any of these N", not a real call).
    int CallSites, // distinct call sites under the same parent that collapsed into this child.
    bool Truncated, // subtree not expanded — an "⋯elided" leaf. The cause is in TruncationCause.
    // WHY the subtree was cut: "AlreadyExpanded" (cycle / shared callee — shown elsewhere), "BudgetCapped"
    // (50k node safety cap), "DepthCapped" (depth limit — the web never sends one, so it fetches full). Null
    // when not truncated. Lets the client label elisions honestly instead of a generic "⋯elided".
    string? TruncationCause,
    string? DispatchBasis, // "heuristic" = inferred dispatch (verify); null/"roslyn" = exact mined fact.
    string? File,
    int Line,
    IReadOnlyList<EffectDto> Effects,
    IReadOnlyList<TreeNodeDto> Children,
    // Render-rule fold: set when an opaque/collapse rule matched this node, so the SPA draws it as a labelled
    // seam leaf instead of the raw subtree (parity with the pretty/llm renderers). Null Children on a folded
    // node — the subtree is intentionally hidden; a folded COLLAPSE node's Effects carry the union of what it
    // hides (with FoldHidden = the hidden node count). Absent (null Fold*) when no rule matched or ?raw=true.
    string? FoldKind = null, // "opaque" | "collapse"
    string? FoldLabel = null, // the rule's human label, e.g. "charge-band pricing engine"
    int FoldHidden = 0 // number of descendant nodes folded away (collapse only; 0 for opaque)
);

internal sealed record TreeResponseDto(string From, bool Matched, IReadOnlyList<TreeNodeDto> Roots);

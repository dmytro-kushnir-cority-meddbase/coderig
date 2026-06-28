using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Builds the FactGraphData directly from a freshly-extracted AnalysisResult — the SAME graph
// Reads.LoadFactGraphAsync reconstructs from a saved .rig store, but without the round-trip through
// SQLite. `rig index` uses this so the graph phase materializes call_edges from facts already in memory
// instead of re-reading the whole fact store off disk (the second 3.8GB cold read).
//
// This projection MUST stay field-for-field identical to Reads.LoadFactGraphAsync — same first-party
// call filter, same edge fields (incl. ReceiverType/TypeArguments), same method dedup, same handoff
// classification — or the persisted call_edges would diverge from what the in-memory oracle computes
// over a re-read store (the effect-path divergence). FactGraphProjectionParityTests asserts the two
// agree on a real solution; keep them in lockstep.
public static class FactGraphProjection
{
    public static FactGraphData FromAnalysis(
        AnalysisResult result,
        IReadOnlyList<FactHandoffRule>? handoffRules = null,
        IReadOnlyList<FactRedirectRule>? redirectRules = null
    )
    {
        // First-party callees only (TargetInSource): BCL/runtime targets are leaves that add width, not
        // reach, and have no source symbol. Mirrors LoadFactGraphAsync's WHERE exactly. EXCEPTION: a call
        // matched by a redirect rule (external convenience overload → virtual hatch) is KEPT despite being
        // out-of-source and its callee rewritten to the hatch — the external-virtual-override-orphan fix
        // (docs/backlog.md); receiver-narrowed dispatch then resolves the kept hatch to the first-party override.
        var callEdges = (result.References ?? [])
            .Where(r =>
                r.EnclosingSymbolId != null
                && (r.RefKind == RefKinds.Invocation || r.RefKind == RefKinds.MethodGroup || r.RefKind == RefKinds.Ctor)
            )
            .Select(r => (r, redirect: RedirectClassifier.Redirect(r.TargetSymbolId, redirectRules)))
            .Where(x => x.r.TargetInSource || x.redirect != null)
            .Select(x => new CallEdge(
                Caller: x.r.EnclosingSymbolId!,
                Callee: x.redirect ?? x.r.TargetSymbolId,
                Kind: x.r.RefKind,
                FilePath: x.r.FilePath,
                Line: x.r.Line,
                LoopKind: x.r.EnclosingLoopKind,
                LoopDetail: x.r.EnclosingLoopDetail,
                ReceiverType: x.r.ReceiverType,
                HandoffDispatcher: null,
                TypeArguments: x.r.TypeArguments,
                DelegateConsumer: x.r.DelegateConsumer,
                DeclaringTypeArgBinding: x.r.DeclaringTypeArgBinding,
                MethodTypeArgBinding: x.r.MethodTypeArgBinding,
                NonVirtual: x.r.NonVirtual,
                EnclosingGuards: x.r.EnclosingGuards
            ))
            .Distinct()
            .ToList();
        var classifiedEdges = HandoffClassifier.Classify(callEdges, handoffRules);

        var implEdges = (result.TypeRelations ?? [])
            .Where(t => t.RelationKind == RelationKinds.Interface)
            .Select(t => new ImplementsEdge(ImplType: t.TypeSymbolId, InterfaceType: t.RelatedSymbolId))
            .Distinct()
            .ToList();

        var baseEdges = (result.TypeRelations ?? [])
            .Where(t => t.RelationKind == RelationKinds.Base)
            .Select(t => new BaseEdge(SubType: t.TypeSymbolId, BaseType: t.RelatedSymbolId))
            .Distinct()
            .ToList();

        var methods = (result.Symbols ?? [])
            .Where(s => s.Kind == SymbolKinds.Method)
            .Select(s => new MethodRef(
                SymbolId: s.SymbolId,
                Name: s.Name,
                ContainingTypeId: s.ContainingSymbolId,
                IsOverride: s.IsOverride,
                FilePath: s.FilePath,
                Line: s.Line
            ))
            .GroupBy(m => m.SymbolId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var minedDispatch = (result.DispatchFacts ?? []).Distinct().ToList();

        return new FactGraphData(classifiedEdges, implEdges, methods, baseEdges, minedDispatch);
    }
}

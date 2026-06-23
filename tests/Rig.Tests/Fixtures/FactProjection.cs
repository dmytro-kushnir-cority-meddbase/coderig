using Rig.Domain.Data;
using Rig.Domain.Functions;

namespace Rig.Tests.Fixtures;

public static class FactProjection
{
    public static FactEntryPointDeriver.FactEntryPointData EntryPointData(AnalysisResult result)
    {
        var baseEdges = result
            .TypeRelations!.Where(t => t.RelationKind == "base")
            .Select(t => (t.TypeSymbolId, t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var interfaceEdges = result
            .TypeRelations!.Where(t => t.RelationKind == "interface")
            .Select(t => (t.TypeSymbolId, t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var methods = result
            .Symbols!.Where(s => s.Kind == "method")
            .GroupBy(m => (m.FilePath, m.Line))
            .Select(g => g.First())
            .Select(m => new MethodSymbol(
                SymbolId: m.SymbolId,
                Name: m.Name,
                ContainingSymbolId: m.ContainingSymbolId,
                Signature: m.Signature,
                FilePath: m.FilePath,
                Line: m.Line,
                IsOverride: m.IsOverride
            ))
            .ToArray();

        var types = result
            .Symbols!.Where(s => s.Kind == "type")
            .GroupBy(t => t.SymbolId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(t => new TypeSymbol(
                SymbolId: t.SymbolId,
                Namespace: t.Namespace,
                FilePath: t.FilePath,
                Line: t.Line,
                IsAbstract: t.Modifiers.Split(separator: ' ').Contains(value: "abstract")
            ))
            .ToArray();

        var ctorRefs = result
            .References!.Where(r => r.RefKind == "ctor" && r.EnclosingSymbolId != null)
            .GroupBy(r => (r.FilePath, r.Line))
            .Select(g => g.First())
            .Select(r => new SymbolRef(Target: r.TargetSymbolId, Enclosing: r.EnclosingSymbolId, FilePath: r.FilePath, Line: r.Line))
            .ToArray();

        return new FactEntryPointDeriver.FactEntryPointData(
            BaseEdges: baseEdges,
            Methods: methods,
            Types: types,
            CtorRefs: ctorRefs,
            InterfaceEdges: interfaceEdges
        );
    }

    public static FactGraphData GraphData(
        AnalysisResult result,
        IReadOnlyList<FactHandoffRule>? handoffRules = null,
        IReadOnlyList<FactRedirectRule>? redirectRules = null
    )
    {
        var callEdges = result
            .References!.Where(r =>
                r.EnclosingSymbolId != null && (r.RefKind == "invocation" || r.RefKind == "methodGroup" || r.RefKind == "ctor")
            )
            .Select(r => (r, redirect: RedirectClassifier.Redirect(r.TargetSymbolId, redirectRules)))
            // Keep an edge if its target is first-party OR a redirect rule rewrites it to the virtual hatch
            // (the redirect target is itself external, so it would otherwise be dropped — that is the fix).
            .Where(x => x.r.TargetInSource || x.redirect != null)
            .Select(x => new CallEdge(
                Caller: x.r.EnclosingSymbolId!,
                Callee: x.redirect ?? x.r.TargetSymbolId,
                Kind: x.r.RefKind,
                FilePath: x.r.FilePath,
                Line: x.r.Line,
                LoopKind: x.r.EnclosingLoopKind,
                LoopDetail: x.r.EnclosingLoopDetail,
                // Carry the receiver ONLY on redirected edges so dispatch narrows the kept virtual node to the
                // receiver's first-party override (not the full CHA fan). Non-redirected edges keep the prior
                // behaviour (no ReceiverType — see the generic-monomorphization note below).
                ReceiverType: x.redirect != null ? x.r.ReceiverType : null,
                DelegateConsumer: x.r.DelegateConsumer,
                // Render-only generic monomorphization bindings (do NOT set TypeArguments here — it switches on
                // dispatch narrowing and would perturb other tests using this degraded graph).
                DeclaringTypeArgBinding: x.r.DeclaringTypeArgBinding,
                MethodTypeArgBinding: x.r.MethodTypeArgBinding
            ))
            .Distinct()
            .ToArray();
        var classifiedEdges = HandoffClassifier.Classify(edges: callEdges, rules: handoffRules);

        var implEdges = result
            .TypeRelations!.Where(t => t.RelationKind == "interface")
            .Select(t => new ImplementsEdge(ImplType: t.TypeSymbolId, InterfaceType: t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var baseEdges = result
            .TypeRelations!.Where(t => t.RelationKind == "base")
            .Select(t => new BaseEdge(SubType: t.TypeSymbolId, BaseType: t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var methods = result
            .Symbols!.Where(s => s.Kind == "method")
            .GroupBy(m => m.SymbolId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(m => new MethodRef(
                SymbolId: m.SymbolId,
                Name: m.Name,
                ContainingTypeId: m.ContainingSymbolId,
                IsOverride: m.IsOverride
            ))
            .ToArray();

        var minedDispatch = result.DispatchFacts?.Distinct().ToArray();

        return new FactGraphData(
            CallEdges: classifiedEdges,
            ImplementsEdges: implEdges,
            Methods: methods,
            BaseEdges: baseEdges,
            MinedDispatch: minedDispatch
        );
    }

    public static IReadOnlyList<FactInvocation> Invocations(AnalysisResult result) =>
        result
            .References!.Where(r => r.RefKind == "invocation")
            .Select(r => new FactInvocation(
                Target: r.TargetSymbolId,
                Enclosing: r.EnclosingSymbolId,
                FilePath: r.FilePath,
                Line: r.Line,
                Receiver: r.ReceiverType,
                FirstArgTemplate: r.FirstArgumentTemplate,
                FirstArgType: r.FirstArgumentType,
                LoopKind: r.EnclosingLoopKind,
                LoopDetail: r.EnclosingLoopDetail,
                EnclosingInvocations: r.EnclosingInvocations,
                CatchTypes: r.EnclosingCatchTypes,
                TypeArguments: r.TypeArguments,
                FirstArgName: r.FirstArgumentName,
                EnclosingScopes: r.EnclosingScopes,
                ArgumentTemplates: r.ArgumentTemplates,
                ArgumentNames: r.ArgumentNames
            ))
            .ToArray();

    public static IReadOnlyList<SymbolRef> ThrowRefs(AnalysisResult result) =>
        result
            .References!.Where(r => r.RefKind == "throw" && r.EnclosingSymbolId != null)
            .GroupBy(r => (r.FilePath, r.Line, r.TargetSymbolId))
            .Select(g => g.First())
            .Select(r => new SymbolRef(Target: r.TargetSymbolId, Enclosing: r.EnclosingSymbolId, FilePath: r.FilePath, Line: r.Line))
            .ToArray();
}

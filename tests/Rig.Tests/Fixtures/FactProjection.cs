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
            .GroupBy(t => t.SymbolId)
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

    public static FactGraphData GraphData(AnalysisResult result, IReadOnlyList<FactHandoffRule>? handoffRules = null)
    {
        var callEdges = result
            .References!.Where(r =>
                r.EnclosingSymbolId != null
                && r.TargetInSource
                && (r.RefKind == "invocation" || r.RefKind == "methodGroup" || r.RefKind == "ctor")
            )
            .Select(r => new CallEdge(
                Caller: r.EnclosingSymbolId!,
                Callee: r.TargetSymbolId,
                Kind: r.RefKind,
                FilePath: r.FilePath,
                Line: r.Line,
                LoopKind: r.EnclosingLoopKind,
                LoopDetail: r.EnclosingLoopDetail,
                DelegateConsumer: r.DelegateConsumer
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
            .GroupBy(m => m.SymbolId)
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
                EnclosingScopes: r.EnclosingScopes
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

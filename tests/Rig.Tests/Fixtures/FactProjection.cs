using Rig.Domain.Data;
using Rig.Domain.Functions;

namespace Rig.Tests.Fixtures;

// Projects an in-memory AnalysisResult's facts into the deriver inputs, mirroring
// Reads.LoadFactEntryPointDataAsync / Reads.LoadInvocationRefsAsync (the SQLite path).
// This lets the fact-layer derivers be tested end-to-end against a fixture solution with
// NO database — analyze -> project -> derive -> assert. If the Reads projections change,
// keep these in sync (follow-up: extract a single shared pure projection).
public static class FactProjection
{
    public static FactEntryPointDeriver.FactEntryPointData EntryPointData(AnalysisResult result)
    {
        var baseEdges = result
            .TypeRelations.Where(t => t.RelationKind == "base")
            .Select(t => (t.TypeSymbolId, t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var interfaceEdges = result
            .TypeRelations.Where(t => t.RelationKind == "interface")
            .Select(t => (t.TypeSymbolId, t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var methods = result
            .Symbols.Where(s => s.Kind == "method")
            .GroupBy(m => (m.FilePath, m.Line))
            .Select(g => g.First())
            .Select(m => (m.SymbolId, m.Name, m.ContainingSymbolId, m.Signature, m.FilePath, m.Line, m.IsOverride))
            .ToArray();

        var types = result
            .Symbols.Where(s => s.Kind == "type")
            .GroupBy(t => t.SymbolId)
            .Select(g => g.First())
            .Select(t => (t.SymbolId, t.Namespace, t.FilePath, t.Line,
                IsAbstract: t.Modifiers.Split(' ').Contains("abstract")))
            .ToArray();

        var ctorRefs = result
            .References.Where(r => r.RefKind == "ctor" && r.EnclosingSymbolId != null)
            .GroupBy(r => (r.FilePath, r.Line))
            .Select(g => g.First())
            .Select(r => (r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line))
            .ToArray();

        return new FactEntryPointDeriver.FactEntryPointData(baseEdges, methods, types, ctorRefs!, interfaceEdges);
    }

    // Mirrors Reads.LoadFactGraphAsync: the fact-derived call graph (call edges + interface and base
    // edges + method descriptors with IsOverride) for FactPathFinder.
    public static FactGraphData GraphData(AnalysisResult result)
    {
        var callEdges = result
            .References.Where(r => r.EnclosingSymbolId != null
                && (r.RefKind == "invocation" || r.RefKind == "methodGroup" || r.RefKind == "ctor"))
            .Select(r => new CallEdge(r.EnclosingSymbolId!, r.TargetSymbolId, r.RefKind, r.FilePath, r.Line))
            .Distinct()
            .ToArray();

        var implEdges = result
            .TypeRelations.Where(t => t.RelationKind == "interface")
            .Select(t => new ImplementsEdge(t.TypeSymbolId, t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var baseEdges = result
            .TypeRelations.Where(t => t.RelationKind == "base")
            .Select(t => new BaseEdge(t.TypeSymbolId, t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var methods = result
            .Symbols.Where(s => s.Kind == "method")
            .GroupBy(m => m.SymbolId)
            .Select(g => g.First())
            .Select(m => new MethodRef(m.SymbolId, m.Name, m.ContainingSymbolId, m.IsOverride))
            .ToArray();

        return new FactGraphData(callEdges, implEdges, methods, baseEdges);
    }

    public static IReadOnlyList<(string Target, string? Enclosing, string FilePath, int Line, string? Receiver, string? FirstArgTemplate, string? FirstArgType)> Invocations(
        AnalysisResult result) =>
        result
            .References.Where(r => r.RefKind == "invocation")
            .Select(r => (r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line, r.ReceiverType, r.FirstArgumentTemplate, r.FirstArgumentType))
            .ToArray();
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;

namespace Rig.Analysis.CallGraph;

internal static class CallGraphBuilder
{
    public static IReadOnlyList<CallGraphInfo> Build(
        IReadOnlyList<EntryPointInfo> entryPoints,
        IReadOnlyList<SourceModel> sources,
        IReadOnlyList<EffectInfo> effects,
        IReadOnlyList<EffectRule> dispatchRules,
        IReadOnlyList<DiRegistrationInfo> diRegistrations
    )
    {
        var methods = sources
            .SelectMany(source => FindApplicationMethods(source, effects))
            .GroupBy(method => method.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var indexes = CallGraphIndexes.Build(sources, diRegistrations);
        var context = new CallGraphContext(methods, dispatchRules, indexes.DispatchIndex, indexes.SingleImplIndex, effects);

        return entryPoints.Select(entryPoint => BuildCallGraph(entryPoint, sources, context)).ToArray();
    }

    private static IEnumerable<MethodModel> FindApplicationMethods(SourceModel source, IReadOnlyList<EffectInfo> effects)
    {
        foreach (var method in source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (source.SemanticModel.GetDeclaredSymbol(method) is not IMethodSymbol methodSymbol)
            {
                continue;
            }

            var key = RoslynSymbolHelpers.GetMethodKey(methodSymbol);
            var displayName = RoslynSymbolHelpers.GetMethodDisplayName(methodSymbol);
            var line = RoslynSymbolHelpers.GetLine(source.Tree, method);
            var methodEffects = effects
                .Where(effect => string.Equals(effect.FilePath, source.FilePath, StringComparison.OrdinalIgnoreCase))
                .Where(effect => RoslynSymbolHelpers.IsLineInside(source.Tree, method, effect.Line))
                .ToArray();

            yield return new MethodModel(key, displayName, source.FilePath, line, method, source.SemanticModel, methodEffects);
        }
    }

    private static CallGraphInfo BuildCallGraph(EntryPointInfo entryPoint, IReadOnlyList<SourceModel> sources, CallGraphContext context)
    {
        var nodes = new List<CallGraphNodeInfo>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var entryNode = EntryNodeResolver.Resolve(entryPoint, sources, context);
        nodes.Add(entryNode.Node);

        foreach (var call in entryNode.Calls)
        {
            VisitMethod(call.Key, context, nodes, visited);
        }

        return new CallGraphInfo(entryPoint.DisplayName, nodes, CallGraphCycleDetector.Detect(nodes));
    }

    private static void VisitMethod(string key, CallGraphContext context, List<CallGraphNodeInfo> nodes, HashSet<string> visited)
    {
        if (!visited.Add(key) || !context.Methods.TryGetValue(key, out var method))
        {
            return;
        }

        var calls = CallResolver.ResolveCalls(method.Body, method.SemanticModel, context);
        nodes.Add(
            CallGraphNodeFactory.Create(
                method.Key,
                method.FilePath,
                method.Line,
                calls,
                method.Effects,
                "high",
                "compilation",
                "direct_symbol_match"
            )
        );

        foreach (var call in calls.Application)
        {
            VisitMethod(call.Key, context, nodes, visited);
        }
    }
}

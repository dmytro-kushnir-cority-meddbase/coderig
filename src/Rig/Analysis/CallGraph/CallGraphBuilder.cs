using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal static class CallGraphBuilder
{
    public static IReadOnlyList<CallGraphInfo> Build(
        IReadOnlyList<EntryPointInfo> entryPoints,
        IReadOnlyList<SourceModel> sources,
        IReadOnlyList<EffectInfo> effects)
    {
        var methods = sources
            .SelectMany(source => FindApplicationMethods(source, effects))
            .GroupBy(method => method.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return entryPoints
            .Select(entryPoint => BuildCallGraph(entryPoint, sources, methods))
            .ToArray();
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

            yield return new MethodModel(
                key,
                displayName,
                source.FilePath,
                line,
                method,
                source.SemanticModel,
                methodEffects);
        }
    }

    private static CallGraphInfo BuildCallGraph(
        EntryPointInfo entryPoint,
        IReadOnlyList<SourceModel> sources,
        IReadOnlyDictionary<string, MethodModel> methods)
    {
        var nodes = new List<CallGraphNodeInfo>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var entryNode = CreateEntryNode(entryPoint, sources, methods);
        nodes.Add(entryNode.Node);

        foreach (var call in entryNode.Calls)
        {
            VisitMethod(call.Key, methods, nodes, visited);
        }

        return new CallGraphInfo(entryPoint.DisplayName, nodes);
    }

    private static (CallGraphNodeInfo Node, IReadOnlyList<ResolvedCall> Calls) CreateEntryNode(
        EntryPointInfo entryPoint,
        IReadOnlyList<SourceModel> sources,
        IReadOnlyDictionary<string, MethodModel> methods)
    {
        if (entryPoint.Kind == "mvc")
        {
            var method = methods.Values.FirstOrDefault(method =>
                string.Equals(method.FilePath, entryPoint.FilePath, StringComparison.OrdinalIgnoreCase) &&
                method.Line == entryPoint.Line);

            if (method is not null)
            {
                var calls = ResolveCalls(method.Body, method.SemanticModel, methods);
                return (CreateNode(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, calls, [], "high", "compilation", "mvc_action_symbol"), calls.Application);
            }
        }

        var source = sources.First(source => string.Equals(source.FilePath, entryPoint.FilePath, StringComparison.OrdinalIgnoreCase));
        var invocation = source.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(invocation => RoslynSymbolHelpers.GetLine(source.Tree, invocation) == entryPoint.Line);

        if (invocation is not null)
        {
            var calls = ResolveMinimalApiHandlerCalls(invocation, source.SemanticModel, methods);
            return (CreateNode(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, calls, [], "high", "compilation", "minimal_api_handler_symbol"), calls.Application);
        }

        return (CreateNode(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, new ResolvedCallSet([], []), [], "low", "compilation", "entrypoint_handler_unresolved"), []);
    }

    private static ResolvedCallSet ResolveMinimalApiHandlerCalls(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, MethodModel> methods)
    {
        var handler = invocation.ArgumentList.Arguments.Skip(1).FirstOrDefault()?.Expression;
        if (handler is null)
        {
            return new ResolvedCallSet([], []);
        }

        if (handler is ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
        {
            return ResolveCalls(handler, semanticModel, methods);
        }

        var methodSymbol = RoslynSymbolHelpers.ResolveMethodSymbol(handler, semanticModel);
        if (methodSymbol is null)
        {
            return new ResolvedCallSet([], [CreateUnresolvedBoundaryCall(handler, semanticModel)]);
        }

        var key = RoslynSymbolHelpers.GetMethodKey(methodSymbol);
        return methods.TryGetValue(key, out var method)
            ? new ResolvedCallSet([new ResolvedCall(key, method.Symbol)], [])
            : new ResolvedCallSet([], [CreateExternalBoundaryCall(handler, semanticModel, methodSymbol)]);
    }

    private static void VisitMethod(
        string key,
        IReadOnlyDictionary<string, MethodModel> methods,
        List<CallGraphNodeInfo> nodes,
        HashSet<string> visited)
    {
        if (!visited.Add(key) || !methods.TryGetValue(key, out var method))
        {
            return;
        }

        var calls = ResolveCalls(method.Body, method.SemanticModel, methods);
        nodes.Add(CreateNode(method.Symbol, method.FilePath, method.Line, calls, method.Effects, "high", "compilation", "direct_symbol_match"));

        foreach (var call in calls.Application)
        {
            VisitMethod(call.Key, methods, nodes, visited);
        }
    }

    private static CallGraphNodeInfo CreateNode(
        string symbol,
        string filePath,
        int line,
        ResolvedCallSet calls,
        IReadOnlyList<EffectInfo> effects,
        string confidence,
        string basis,
        string reason)
    {
        return new CallGraphNodeInfo(
            symbol,
            filePath,
            line,
            confidence,
            basis,
            reason,
            calls.Application.Select(call => call.DisplayName).Distinct(StringComparer.Ordinal).ToArray(),
            calls.Boundary.DistinctBy(call => $"{call.Kind}|{call.Target}|{call.Line}").ToArray(),
            effects);
    }

    private static ResolvedCallSet ResolveCalls(
        SyntaxNode root,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, MethodModel> methods)
    {
        var calls = new List<ResolvedCall>();
        var boundaryCalls = new List<BoundaryCallInfo>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodSymbol = RoslynSymbolHelpers.ResolveMethodSymbol(invocation, semanticModel);
            if (methodSymbol is null)
            {
                boundaryCalls.Add(CreateUnresolvedBoundaryCall(invocation, semanticModel));
                continue;
            }

            var key = RoslynSymbolHelpers.GetMethodKey(methodSymbol);
            if (!methods.TryGetValue(key, out var method))
            {
                boundaryCalls.Add(CreateExternalBoundaryCall(invocation, semanticModel, methodSymbol));
                continue;
            }

            if (!calls.Any(call => string.Equals(call.Key, key, StringComparison.Ordinal)))
            {
                calls.Add(new ResolvedCall(key, method.Symbol));
            }
        }

        return new ResolvedCallSet(calls, boundaryCalls);
    }

    private static BoundaryCallInfo CreateExternalBoundaryCall(
        SyntaxNode node,
        SemanticModel semanticModel,
        IMethodSymbol methodSymbol)
    {
        return new BoundaryCallInfo(
            "external",
            RoslynSymbolHelpers.GetMethodKey(methodSymbol),
            RoslynSymbolHelpers.GetMethodDisplayName(methodSymbol),
            semanticModel.SyntaxTree.FilePath,
            RoslynSymbolHelpers.GetLine(semanticModel.SyntaxTree, node),
            "high",
            "compilation",
            "external_symbol");
    }

    private static BoundaryCallInfo CreateUnresolvedBoundaryCall(
        SyntaxNode node,
        SemanticModel semanticModel)
    {
        return new BoundaryCallInfo(
            "unresolved",
            node.ToString(),
            TryGetUnresolvedMethodName(node) ?? "?",
            semanticModel.SyntaxTree.FilePath,
            RoslynSymbolHelpers.GetLine(semanticModel.SyntaxTree, node),
            "low",
            "compilation",
            "unresolved_call_target");
    }

    private static string? TryGetUnresolvedMethodName(SyntaxNode node)
    {
        return node switch
        {
            InvocationExpressionSyntax invocation => RoslynSymbolHelpers.TryGetMemberName(invocation),
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null
        };
    }
}

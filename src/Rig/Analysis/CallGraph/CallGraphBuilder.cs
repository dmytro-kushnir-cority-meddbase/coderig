using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal static class CallGraphBuilder
{
    private sealed record CallGraphContext(
        IReadOnlyDictionary<string, MethodModel> Methods,
        IReadOnlyList<EffectRule> DispatchRules,
        IReadOnlyDictionary<string, IReadOnlyList<string>> DispatchIndex,
        IReadOnlyDictionary<string, string> SingleImplIndex);

    public static IReadOnlyList<CallGraphInfo> Build(
        IReadOnlyList<EntryPointInfo> entryPoints,
        IReadOnlyList<SourceModel> sources,
        IReadOnlyList<EffectInfo> effects,
        IReadOnlyList<EffectRule> dispatchRules,
        IReadOnlyList<DiRegistrationInfo> diRegistrations)
    {
        var methods = sources
            .SelectMany(source => FindApplicationMethods(source, effects))
            .GroupBy(method => method.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var dispatchIndex = BuildDispatchIndex(sources);
        var singleImplIndex = BuildSingleImplIndex(diRegistrations);
        var context = new CallGraphContext(methods, dispatchRules, dispatchIndex, singleImplIndex);

        return entryPoints
            .Select(entryPoint => BuildCallGraph(entryPoint, sources, context))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildDispatchIndex(
        IReadOnlyList<SourceModel> sources)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            foreach (var classDecl in source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (source.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol classSymbol)
                {
                    continue;
                }

                foreach (var iface in classSymbol.AllInterfaces)
                {
                    if (!IsMessageHandlerInterface(iface.OriginalDefinition) || iface.TypeArguments.Length == 0)
                    {
                        continue;
                    }

                    var messageKey = iface.TypeArguments[0].OriginalDefinition.ToDisplayString();

                    var handleMethod = classDecl.Members
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(m => m.Identifier.ValueText is "Handle" or "HandleAsync");

                    if (handleMethod is null ||
                        source.SemanticModel.GetDeclaredSymbol(handleMethod) is not IMethodSymbol handleSymbol)
                    {
                        continue;
                    }

                    var methodKey = RoslynSymbolHelpers.GetMethodKey(handleSymbol);

                    if (!index.TryGetValue(messageKey, out var handlers))
                    {
                        index[messageKey] = handlers = [];
                    }

                    if (!handlers.Contains(methodKey, StringComparer.Ordinal))
                    {
                        handlers.Add(methodKey);
                    }
                }
            }
        }

        return index.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value.AsReadOnly(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> BuildSingleImplIndex(
        IReadOnlyList<DiRegistrationInfo> registrations)
    {
        return registrations
            .Where(r => r.ImplementationType is not null)
            .GroupBy(r => r.ServiceType, StringComparer.Ordinal)
            .Where(g => g.Select(r => r.ImplementationType).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(
                g => g.Key,
                g => g.First().ImplementationType!,
                StringComparer.Ordinal);
    }

    private static bool IsMessageHandlerInterface(INamedTypeSymbol iface)
    {
        var ns = iface.ContainingNamespace?.ToDisplayString();
        return ns is "MediatR" or "Mediator" &&
               iface.Name is "IRequestHandler" or "ICommandHandler" or "IQueryHandler" or "INotificationHandler";
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
        CallGraphContext context)
    {
        var nodes = new List<CallGraphNodeInfo>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var entryNode = CreateEntryNode(entryPoint, sources, context);
        nodes.Add(entryNode.Node);

        foreach (var call in entryNode.Calls)
        {
            VisitMethod(call.Key, context, nodes, visited);
        }

        return new CallGraphInfo(entryPoint.DisplayName, nodes);
    }

    private static (CallGraphNodeInfo Node, IReadOnlyList<ResolvedCall> Calls) CreateEntryNode(
        EntryPointInfo entryPoint,
        IReadOnlyList<SourceModel> sources,
        CallGraphContext context)
    {
        var method = context.Methods.Values.FirstOrDefault(method =>
            string.Equals(method.FilePath, entryPoint.FilePath, StringComparison.OrdinalIgnoreCase) &&
            method.Line == entryPoint.Line);

        if (method is not null)
        {
            var calls = ResolveCalls(method.Body, method.SemanticModel, context);
            var reason = entryPoint.Kind == "mvc" ? "mvc_action_symbol" : "handler_method_symbol";
            return (CreateNode(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, calls, method.Effects, "high", "compilation", reason), calls.Application);
        }

        var source = sources.First(source => string.Equals(source.FilePath, entryPoint.FilePath, StringComparison.OrdinalIgnoreCase));
        var invocation = source.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(invocation => RoslynSymbolHelpers.GetLine(source.Tree, invocation) == entryPoint.Line);

        if (invocation is not null)
        {
            var calls = ResolveMinimalApiHandlerCalls(invocation, source.SemanticModel, context);
            return (CreateNode(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, calls, [], "high", "compilation", "minimal_api_handler_symbol"), calls.Application);
        }

        return (CreateNode(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, new ResolvedCallSet([], []), [], "low", "compilation", "entrypoint_handler_unresolved"), []);
    }

    private static ResolvedCallSet ResolveMinimalApiHandlerCalls(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CallGraphContext context)
    {
        var handler = invocation.ArgumentList.Arguments.Skip(1).FirstOrDefault()?.Expression;
        if (handler is null)
        {
            return new ResolvedCallSet([], []);
        }

        if (handler is ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
        {
            return ResolveCalls(handler, semanticModel, context);
        }

        var methodSymbol = RoslynSymbolHelpers.ResolveMethodSymbol(handler, semanticModel);
        if (methodSymbol is null)
        {
            return new ResolvedCallSet([], [CreateUnresolvedBoundaryCall(handler, semanticModel)]);
        }

        var key = RoslynSymbolHelpers.GetMethodKey(methodSymbol);
        return context.Methods.TryGetValue(key, out var method)
            ? new ResolvedCallSet([new ResolvedCall(key, method.Symbol)], [])
            : new ResolvedCallSet([], [CreateExternalBoundaryCall(handler, semanticModel, methodSymbol)]);
    }

    private static void VisitMethod(
        string key,
        CallGraphContext context,
        List<CallGraphNodeInfo> nodes,
        HashSet<string> visited)
    {
        if (!visited.Add(key) || !context.Methods.TryGetValue(key, out var method))
        {
            return;
        }

        var calls = ResolveCalls(method.Body, method.SemanticModel, context);
        nodes.Add(CreateNode(method.Symbol, method.FilePath, method.Line, calls, method.Effects, "high", "compilation", "direct_symbol_match"));

        foreach (var call in calls.Application)
        {
            VisitMethod(call.Key, context, nodes, visited);
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
        CallGraphContext context)
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

            // For interface methods, prefer the concrete single-impl before traversing
            // the interface declaration itself (which has no body or effects).
            var singleImpl = TryResolveSingleImplDispatch(methodSymbol, context);
            if (singleImpl is not null)
            {
                if (!calls.Any(call => string.Equals(call.Key, singleImpl.Key, StringComparison.Ordinal)))
                {
                    calls.Add(singleImpl);
                }
                continue;
            }

            if (context.Methods.TryGetValue(key, out var method))
            {
                if (!calls.Any(call => string.Equals(call.Key, key, StringComparison.Ordinal)))
                {
                    calls.Add(new ResolvedCall(key, method.Symbol));
                }
                continue;
            }

            var dispatched = TryResolveDispatch(invocation, semanticModel, methodSymbol, context);
            if (dispatched is not null)
            {
                foreach (var dispatch in dispatched)
                {
                    if (!calls.Any(call => string.Equals(call.Key, dispatch.Key, StringComparison.Ordinal)))
                    {
                        calls.Add(dispatch);
                    }
                }
                continue;
            }

            boundaryCalls.Add(CreateExternalBoundaryCall(invocation, semanticModel, methodSymbol));
        }

        return new ResolvedCallSet(calls, boundaryCalls);
    }

    private static IReadOnlyList<ResolvedCall>? TryResolveDispatch(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        IMethodSymbol methodSymbol,
        CallGraphContext context)
    {
        var matchingRule = context.DispatchRules.FirstOrDefault(rule => IsDispatchCall(methodSymbol, rule));
        if (matchingRule is null)
        {
            return null;
        }

        var argumentType = GetInvocationArgumentType(invocation, semanticModel);
        if (argumentType is null || !context.DispatchIndex.TryGetValue(argumentType, out var handlerKeys))
        {
            return null;
        }

        var resolved = handlerKeys
            .Where(hk => context.Methods.ContainsKey(hk))
            .Select(hk => new ResolvedCall(hk, context.Methods[hk].Symbol))
            .ToArray();

        return resolved.Length > 0 ? resolved : null;
    }

    private static ResolvedCall? TryResolveSingleImplDispatch(
        IMethodSymbol methodSymbol,
        CallGraphContext context)
    {
        if (methodSymbol.ContainingType.TypeKind != Microsoft.CodeAnalysis.TypeKind.Interface)
        {
            return null;
        }

        var interfaceTypeKey = RoslynSymbolHelpers.GetTypeKey(methodSymbol.ContainingType.OriginalDefinition);
        if (!context.SingleImplIndex.TryGetValue(interfaceTypeKey, out var concreteTypeKey))
        {
            return null;
        }

        var interfaceMethodKey = RoslynSymbolHelpers.GetMethodKey(methodSymbol);
        var concreteMethodKey = interfaceMethodKey.Replace(interfaceTypeKey, concreteTypeKey, StringComparison.Ordinal);

        return context.Methods.TryGetValue(concreteMethodKey, out var method)
            ? new ResolvedCall(concreteMethodKey, method.Symbol)
            : null;
    }

    private static bool IsDispatchCall(IMethodSymbol methodSymbol, EffectRule rule)
    {
        if (!rule.Methods.Contains(methodSymbol.Name, StringComparer.Ordinal))
        {
            return false;
        }

        if (rule.DeclaringTypes is null || rule.DeclaringTypes.Count == 0)
        {
            return false;
        }

        var containingType = methodSymbol.ContainingType.OriginalDefinition.ToDisplayString();
        return rule.DeclaringTypes.Any(dt =>
            string.Equals(dt, containingType, StringComparison.Ordinal) ||
            containingType.Contains(dt, StringComparison.Ordinal));
    }

    private static string? GetInvocationArgumentType(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        return argument is null
            ? null
            : semanticModel.GetTypeInfo(argument).Type?.OriginalDefinition.ToDisplayString();
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

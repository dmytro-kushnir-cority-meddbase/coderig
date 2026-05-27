using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Analysis.Analysis.Rules;
using Rig.Domain.Data;

namespace Rig.Analysis.Analysis.CallGraph;

internal static class CallResolver
{
    public static ResolvedCallSetInfo ResolveCalls(
        SyntaxNode root,
        SemanticModel semanticModel,
        CallGraphContext context
    )
    {
        var calls = new List<ResolvedCallInfo>();
        var boundaryCalls = new List<BoundaryCallInfo>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // nameof(...) is a compile-time operator, not a runtime call.
            if (invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })
            {
                continue;
            }

            var methodSymbol = RoslynSymbolHelpers.ResolveMethodSymbol(invocation, semanticModel);
            if (methodSymbol is null)
            {
                boundaryCalls.Add(CreateUnresolvedBoundaryCall(invocation, semanticModel));
                continue;
            }

            var key = RoslynSymbolHelpers.GetMethodKey(methodSymbol);
            var line = RoslynSymbolHelpers.GetCallNameLine(semanticModel.SyntaxTree, invocation);

            var singleImpl = TryResolveSingleImplDispatch(methodSymbol, context);
            if (singleImpl is not null)
            {
                AddUnique(calls, singleImpl with { Line = line });
                continue;
            }

            if (context.Methods.ContainsKey(key))
            {
                AddUnique(calls, new ResolvedCallInfo(key, line));
                continue;
            }

            var dispatched = TryResolveDispatch(invocation, semanticModel, methodSymbol, context);
            if (dispatched is not null)
            {
                foreach (var dispatch in dispatched)
                {
                    AddUnique(calls, dispatch with { Line = line });
                }
                continue;
            }

            boundaryCalls.Add(CreateExternalBoundaryCall(invocation, semanticModel, methodSymbol));
        }

        AddMethodGroupCalls(root, semanticModel, context, calls);

        return new ResolvedCallSetInfo(calls, boundaryCalls);
    }

    public static BoundaryCallInfo CreateExternalBoundaryCall(
        SyntaxNode node,
        SemanticModel semanticModel,
        IMethodSymbol methodSymbol
    )
    {
        return new BoundaryCallInfo(
            "external",
            RoslynSymbolHelpers.GetMethodKey(methodSymbol),
            RoslynSymbolHelpers.GetMethodDisplayName(methodSymbol),
            semanticModel.SyntaxTree.FilePath,
            RoslynSymbolHelpers.GetCallNameLine(semanticModel.SyntaxTree, node),
            "high",
            "compilation",
            "external_symbol"
        );
    }

    public static BoundaryCallInfo CreateUnresolvedBoundaryCall(
        SyntaxNode node,
        SemanticModel semanticModel
    )
    {
        return new BoundaryCallInfo(
            "unresolved",
            node.ToString(),
            TryGetUnresolvedMethodName(node) ?? "?",
            semanticModel.SyntaxTree.FilePath,
            RoslynSymbolHelpers.GetCallNameLine(semanticModel.SyntaxTree, node),
            "low",
            "compilation",
            "unresolved_call_target"
        );
    }

    private static void AddMethodGroupCalls(
        SyntaxNode root,
        SemanticModel semanticModel,
        CallGraphContext context,
        List<ResolvedCallInfo> calls
    )
    {
        // Scan for method group references used as delegates; they are not InvocationExpressionSyntax.
        foreach (var node in root.DescendantNodes())
        {
            if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            {
                continue;
            }

            if (
                node is IdentifierNameSyntax
                && node.Parent is MemberAccessExpressionSyntax parentMember
                && parentMember.Name == node
            )
            {
                continue;
            }

            if (
                node.Parent is InvocationExpressionSyntax parentCall
                && parentCall.Expression == node
            )
            {
                continue;
            }

            var groupSymbolInfo = semanticModel.GetSymbolInfo(node);
            if (groupSymbolInfo.Symbol is not IMethodSymbol groupSymbol)
            {
                continue;
            }

            var groupLine = RoslynSymbolHelpers.GetCallNameLine(semanticModel.SyntaxTree, node);

            var singleImplGroup = TryResolveSingleImplDispatch(groupSymbol, context);
            if (singleImplGroup is not null)
            {
                AddUnique(calls, singleImplGroup with { Line = groupLine });
                continue;
            }

            var groupKey = RoslynSymbolHelpers.GetMethodKey(groupSymbol);
            if (context.Methods.ContainsKey(groupKey))
            {
                AddUnique(calls, new ResolvedCallInfo(groupKey, groupLine));
            }
        }
    }

    private static IReadOnlyList<ResolvedCallInfo>? TryResolveDispatch(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        IMethodSymbol methodSymbol,
        CallGraphContext context
    )
    {
        var matchingRule = context.DispatchRules.FirstOrDefault(rule =>
            IsDispatchCall(methodSymbol, rule)
        );
        if (matchingRule is null)
        {
            return null;
        }

        var argumentType = GetInvocationArgumentType(invocation, semanticModel);
        if (
            argumentType is null
            || !context.DispatchIndex.TryGetValue(argumentType, out var handlerKeys)
        )
        {
            return null;
        }

        var resolved = handlerKeys
            .Where(context.Methods.ContainsKey)
            .Select(hk => new ResolvedCallInfo(hk))
            .ToArray();

        return resolved.Length > 0 ? resolved : null;
    }

    private static ResolvedCallInfo? TryResolveSingleImplDispatch(
        IMethodSymbol methodSymbol,
        CallGraphContext context
    )
    {
        if (methodSymbol.ContainingType.TypeKind != Microsoft.CodeAnalysis.TypeKind.Interface)
        {
            return null;
        }

        var interfaceTypeKey = RoslynSymbolHelpers.GetTypeKey(
            methodSymbol.ContainingType.OriginalDefinition
        );
        if (!context.SingleImplIndex.TryGetValue(interfaceTypeKey, out var concreteTypeKey))
        {
            return null;
        }

        var interfaceMethodKey = RoslynSymbolHelpers.GetMethodKey(methodSymbol);
        var concreteMethodKey = interfaceMethodKey.Replace(
            interfaceTypeKey,
            concreteTypeKey,
            StringComparison.Ordinal
        );

        return context.Methods.ContainsKey(concreteMethodKey)
            ? new ResolvedCallInfo(concreteMethodKey)
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
            RuleTypeMatcher.MatchesDisplayName(containingType, dt, allowSubstring: true)
        );
    }

    private static string? GetInvocationArgumentType(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel
    )
    {
        var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        return argument is null
            ? null
            : semanticModel.GetTypeInfo(argument).Type?.OriginalDefinition.ToDisplayString();
    }

    private static void AddUnique(List<ResolvedCallInfo> calls, ResolvedCallInfo call)
    {
        if (!calls.Any(existing => string.Equals(existing.Key, call.Key, StringComparison.Ordinal)))
        {
            calls.Add(call);
        }
    }

    private static string? TryGetUnresolvedMethodName(SyntaxNode node)
    {
        return node switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax m } => m.Name
                .Identifier
                .ValueText,
            InvocationExpressionSyntax { Expression: IdentifierNameSyntax id } =>
                id.Identifier.ValueText,
            InvocationExpressionSyntax { Expression: GenericNameSyntax g } =>
                g.Identifier.ValueText,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };
    }
}

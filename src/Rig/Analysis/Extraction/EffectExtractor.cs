using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal static class EffectExtractor
{
    public static IEnumerable<EffectInfo> FindEffects(SourceModel source, AnalysisRuleSet rules)
    {
        foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodSymbol = RoslynSymbolHelpers.ResolveMethodSymbol(invocation, source.SemanticModel);
            var methodName = methodSymbol?.Name ?? memberAccess.Name.Identifier.ValueText;
            var line = RoslynSymbolHelpers.GetLine(source.Tree, invocation);
            var candidate = new InvocationEffectCandidate(
                invocation,
                memberAccess,
                methodSymbol,
                methodName,
                source.SemanticModel.GetEnclosingSymbol(invocation.SpanStart) as IMethodSymbol,
                source.SemanticModel);

            foreach (var rule in rules.Effects.Where(rule => !rule.TreatAsDispatch && Matches(rule, candidate)))
            {
                var effect = TryCreateEffect(rule, methodName, invocation, memberAccess, source.FilePath, line, source.SemanticModel);
                if (effect is not null)
                {
                    yield return EffectObservationExtractor.AttachObservations(invocation, effect, source.SemanticModel);
                }
            }
        }
    }

    private static bool Matches(EffectRule rule, InvocationEffectCandidate candidate)
    {
        return rule.Matches(candidate.MethodName)
            && MatchesDeclaringType(rule, candidate.MethodSymbol)
            && MatchesReceiverType(rule, candidate)
            && MatchesContainingNamespace(rule, candidate.ContainingMethodSymbol)
            && MatchesContainingType(rule, candidate.ContainingMethodSymbol)
            && MatchesContainingMethod(rule, candidate.ContainingMethodSymbol);
    }

    private static bool MatchesDeclaringType(EffectRule rule, IMethodSymbol? methodSymbol)
    {
        return MatchesOptionalTypes(rule.DeclaringTypes, methodSymbol?.ContainingType);
    }

    private static bool MatchesReceiverType(EffectRule rule, InvocationEffectCandidate candidate)
    {
        if (rule.ReceiverTypes is null || rule.ReceiverTypes.Count == 0)
        {
            return true;
        }

        var receiverType = candidate.SemanticModel.GetTypeInfo(candidate.MemberAccess.Expression).Type;
        return MatchesOptionalTypes(rule.ReceiverTypes, receiverType);
    }

    private static bool MatchesContainingNamespace(EffectRule rule, IMethodSymbol? methodSymbol)
    {
        if (rule.ContainingNamespaces is null || rule.ContainingNamespaces.Count == 0)
        {
            return true;
        }

        var containingNamespace = methodSymbol?.ContainingType.ContainingNamespace?.ToDisplayString();
        return containingNamespace is not null && rule.ContainingNamespaces.Any(ruleNamespace =>
            string.Equals(ruleNamespace, containingNamespace, StringComparison.Ordinal) ||
            containingNamespace.StartsWith($"{ruleNamespace}.", StringComparison.Ordinal));
    }

    private static bool MatchesContainingType(EffectRule rule, IMethodSymbol? methodSymbol)
    {
        return MatchesOptionalTypes(rule.ContainingTypes, methodSymbol?.ContainingType);
    }

    private static bool MatchesContainingMethod(EffectRule rule, IMethodSymbol? methodSymbol)
    {
        if (rule.ContainingMethods is null || rule.ContainingMethods.Count == 0)
        {
            return true;
        }

        return methodSymbol is not null && rule.ContainingMethods.Contains(methodSymbol.Name, StringComparer.Ordinal);
    }

    private static bool MatchesOptionalTypes(IReadOnlyList<string>? ruleTypes, ITypeSymbol? actualType)
    {
        if (ruleTypes is null || ruleTypes.Count == 0)
        {
            return true;
        }

        if (actualType is null)
        {
            return false;
        }

        return ruleTypes.Any(ruleType => RuleTypeMatcher.MatchesTypeOrInterfaces(actualType, ruleType));
    }

    private static EffectInfo? TryCreateEffect(
        EffectRule rule,
        string methodName,
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        string sourceFile,
        int line,
        SemanticModel semanticModel)
    {
        var resource = rule.Resource switch
        {
            "http_argument" => TryGetStringArgument(invocation) is { } url ? NormalizeHttpResource(url) : null,
            "string_argument" => TryGetStringArgument(invocation),
            "ef_dbset_receiver" => TryGetDbSetResource(memberAccess.Expression, semanticModel),
            "ef_query_root" => TryGetDbSetResource(FindRootReceiver(memberAccess.Expression), semanticModel),
            "ef_context_receiver" => TryGetContextResource(memberAccess.Expression, semanticModel),
            "ef_database_facade" => TryGetDatabaseFacadeResource(memberAccess.Expression, semanticModel),
            "receiver_type" => TryGetReceiverTypeResource(memberAccess.Expression, semanticModel),
            "argument_type" => TryGetArgumentTypeResource(invocation, semanticModel),
            _ => null
        };

        return string.IsNullOrWhiteSpace(resource)
            ? null
            : new EffectInfo(
                rule.Provider,
                rule.Operation,
                resource,
                methodName,
                sourceFile,
                line,
                rule.Confidence,
                rule.Basis,
                rule.Reason,
                []);
    }

    private static string? TryGetStringArgument(InvocationExpressionSyntax invocation)
    {
        return invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.GetStringTemplate();
    }

    private static string NormalizeHttpResource(string url)
    {
        var schemeSeparator = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            var withoutScheme = url[(schemeSeparator + 3)..];
            return withoutScheme.TrimEnd('/');
        }

        return url.TrimStart('/');
    }

    private static string? TryGetDbSetResource(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is MemberAccessExpressionSyntax dbSetAccess)
        {
            var symbol = semanticModel.GetSymbolInfo(dbSetAccess).Symbol;
            return symbol switch
            {
                IPropertySymbol property => $"{property.ContainingType.Name}.{property.Name}",
                IFieldSymbol field => $"{field.ContainingType.Name}.{field.Name}",
                _ => null
            };
        }

        // Follow local variable back to its initializer (e.g. var q = _ctx.Items.AsQueryable())
        if (expression is IdentifierNameSyntax identifier)
        {
            var symbol = semanticModel.GetSymbolInfo(identifier).Symbol;

            if (symbol is ILocalSymbol localSymbol)
            {
                var declarator = localSymbol.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax())
                    .OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault();

                if (declarator?.Initializer?.Value is ExpressionSyntax init)
                {
                    var root = FindRootReceiver(init);
                    if (root != expression)
                    {
                        return TryGetDbSetResource(root, semanticModel);
                    }
                }
            }

            // Method parameter typed as IQueryable<T> — extract the entity type name as resource
            if (symbol is IParameterSymbol paramSymbol)
            {
                return TryGetQueryableEntityType(paramSymbol.Type);
            }
        }

        return null;
    }

    private static string? TryGetQueryableEntityType(ITypeSymbol type)
    {
        var candidates = new[] { type }.Concat(type.AllInterfaces);
        foreach (var t in candidates)
        {
            if (t is INamedTypeSymbol named &&
                named.IsGenericType &&
                named.TypeArguments.Length == 1 &&
                named.OriginalDefinition.ToDisplayString().Contains("IQueryable"))
            {
                return named.TypeArguments[0].Name;
            }
        }

        return null;
    }

    private static string? TryGetContextResource(ExpressionSyntax expression, SemanticModel semanticModel)    {
        return semanticModel.GetTypeInfo(expression).Type?.Name;
    }

    private static ExpressionSyntax FindRootReceiver(ExpressionSyntax expression)
    {
        var current = expression;
        while (true)
        {
            if (current is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax invocationAccess)
            {
                current = invocationAccess.Expression;
            }
            else if (current is CastExpressionSyntax cast)
            {
                current = cast.Expression;
            }
            else if (current is ParenthesizedExpressionSyntax paren)
            {
                current = paren.Expression;
            }
            else
            {
                break;
            }
        }

        return current;
    }

    private static string? TryGetDatabaseFacadeResource(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is not MemberAccessExpressionSyntax databaseAccess)
        {
            return TryGetReceiverTypeResource(expression, semanticModel);
        }

        var contextType = semanticModel.GetTypeInfo(databaseAccess.Expression).Type;
        return contextType is null ? null : $"{contextType.Name}.{databaseAccess.Name.Identifier.ValueText}";
    }

    private static string? TryGetReceiverTypeResource(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        return semanticModel.GetTypeInfo(expression).Type?.OriginalDefinition.ToDisplayString();
    }

    private static string? TryGetArgumentTypeResource(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        return argument is null
            ? null
            : semanticModel.GetTypeInfo(argument).Type?.OriginalDefinition.ToDisplayString() ?? argument.ToString();
    }

    private sealed record InvocationEffectCandidate(
        InvocationExpressionSyntax Invocation,
        MemberAccessExpressionSyntax MemberAccess,
        IMethodSymbol? MethodSymbol,
        string MethodName,
        IMethodSymbol? ContainingMethodSymbol,
        SemanticModel SemanticModel);
}

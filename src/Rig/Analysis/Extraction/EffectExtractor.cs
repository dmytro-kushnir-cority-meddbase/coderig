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
                    yield return AttachObservations(invocation, effect);
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

        return ruleTypes.Any(ruleType => TypeMatches(actualType, ruleType));
    }

    private static bool TypeMatches(ITypeSymbol actualType, string ruleType)
    {
        var actualTypes = EnumerateTypeAndInterfaces(actualType)
            .Select(type => type.OriginalDefinition.ToDisplayString())
            .Distinct(StringComparer.Ordinal);

        return actualTypes.Any(actual =>
            string.Equals(ruleType, actual, StringComparison.Ordinal) ||
            actual.StartsWith($"{ruleType}<", StringComparison.Ordinal) ||
            actual.EndsWith($".{ruleType}", StringComparison.Ordinal) ||
            actual.Contains($".{ruleType}<", StringComparison.Ordinal) ||
            actual.Contains(ruleType, StringComparison.Ordinal));
    }

    private static IEnumerable<ITypeSymbol> EnumerateTypeAndInterfaces(ITypeSymbol type)
    {
        yield return type;

        foreach (var interfaceType in type.AllInterfaces)
        {
            yield return interfaceType;
        }

        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            yield return baseType;
        }
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

    private static EffectInfo AttachObservations(InvocationExpressionSyntax invocation, EffectInfo effect)
    {
        var observations = new List<EffectObservationInfo>();

        var loop = FindLoopContext(invocation);
        if (loop is not null)
        {
            observations.Add(new EffectObservationInfo(
                "looped_effect",
                loop.Value.Context,
                loop.Value.Detail,
                "high",
                "compilation",
                "effect_inside_loop"));
        }

        var fanout = FindParallelFanoutContext(invocation);
        if (fanout is not null)
        {
            observations.Add(new EffectObservationInfo(
                "parallel_fanout",
                fanout.Value.Context,
                fanout.Value.Detail,
                "high",
                "compilation",
                "effect_inside_parallel_fanout"));
        }

        return effect with { Observations = observations };
    }

    private static (string Context, string Detail)? FindLoopContext(InvocationExpressionSyntax invocation)
    {
        foreach (var ancestor in invocation.Ancestors())
        {
            switch (ancestor)
            {
                case ForEachStatementSyntax forEach:
                    return ("foreach", $"{forEach.Identifier.ValueText} in {forEach.Expression}");
                case ForStatementSyntax:
                    return ("for", "for");
                case WhileStatementSyntax:
                    return ("while", "while");
            }
        }

        return null;
    }

    private static (string Context, string Detail)? FindParallelFanoutContext(InvocationExpressionSyntax invocation)
    {
        foreach (var ancestor in invocation.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            if (ancestor.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.ValueText;
            var receiver = memberAccess.Expression.ToString();

            if (string.Equals(receiver, "Task", StringComparison.Ordinal) &&
                string.Equals(methodName, "WhenAll", StringComparison.Ordinal))
            {
                return ("Task.WhenAll", "Task.WhenAll");
            }

            if (string.Equals(receiver, "Parallel", StringComparison.Ordinal) &&
                (string.Equals(methodName, "ForEach", StringComparison.Ordinal) ||
                 string.Equals(methodName, "ForEachAsync", StringComparison.Ordinal)))
            {
                return ($"Parallel.{methodName}", $"Parallel.{methodName}");
            }
        }

        return null;
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
        if (expression is not MemberAccessExpressionSyntax dbSetAccess)
        {
            return null;
        }

        var symbol = semanticModel.GetSymbolInfo(dbSetAccess).Symbol;
        return symbol switch
        {
            IPropertySymbol property => $"{property.ContainingType.Name}.{property.Name}",
            IFieldSymbol field => $"{field.ContainingType.Name}.{field.Name}",
            _ => null
        };
    }

    private static string? TryGetContextResource(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        return semanticModel.GetTypeInfo(expression).Type?.Name;
    }

    private static ExpressionSyntax FindRootReceiver(ExpressionSyntax expression)
    {
        var current = expression;
        while (current is InvocationExpressionSyntax invocation &&
               invocation.Expression is MemberAccessExpressionSyntax invocationAccess)
        {
            current = invocationAccess.Expression;
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

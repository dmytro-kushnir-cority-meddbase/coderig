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

            foreach (var rule in rules.Effects.Where(rule => rule.Matches(methodName)))
            {
                var effect = TryCreateEffect(rule, methodName, invocation, memberAccess, source.FilePath, line, source.SemanticModel);
                if (effect is not null)
                {
                    yield return AttachObservations(invocation, effect);
                }
            }
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
            "ef_context_receiver" => TryGetContextResource(memberAccess.Expression, semanticModel),
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
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal static class EntryPointExtractor
{
    public static IEnumerable<EntryPointInfo> FindMinimalApiEntryPoints(
        SourceModel source,
        AnalysisRuleSet rules)
    {
        foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.ValueText;
            var rule = rules.MinimalApiEntryPoints.FirstOrDefault(rule =>
                string.Equals(rule.Method, methodName, StringComparison.Ordinal));
            if (rule is null)
            {
                continue;
            }

            var route = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.GetLiteralString();
            if (string.IsNullOrWhiteSpace(route))
            {
                continue;
            }

            yield return new EntryPointInfo(
                "minapi",
                rule.HttpMethod,
                route,
                $"minapi {rule.HttpMethod} {route}",
                source.FilePath,
                RoslynSymbolHelpers.GetLine(source.Tree, invocation));
        }
    }

    public static IEnumerable<EntryPointInfo> FindMvcEntryPoints(
        SourceModel source,
        AnalysisRuleSet rules)
    {
        foreach (var controller in source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!controller.Identifier.ValueText.EndsWith("Controller", StringComparison.Ordinal))
            {
                continue;
            }

            var controllerToken = controller.Identifier.ValueText[..^"Controller".Length].ToLowerInvariant();
            var controllerRoute = GetAttributeStringArgument(controller.AttributeLists, "Route") ?? "[controller]";
            controllerRoute = controllerRoute.Replace("[controller]", controllerToken, StringComparison.OrdinalIgnoreCase);

            foreach (var method in controller.Members.OfType<MethodDeclarationSyntax>())
            {
                var httpAttribute = FindHttpAttribute(method.AttributeLists, rules);
                if (httpAttribute is null)
                {
                    continue;
                }

                var route = CombineRoutes(controllerRoute, httpAttribute.Value.Route);

                yield return new EntryPointInfo(
                    "mvc",
                    httpAttribute.Value.Method,
                    route,
                    $"mvc {httpAttribute.Value.Method} {route}",
                    source.FilePath,
                    RoslynSymbolHelpers.GetLine(source.Tree, method));
            }
        }
    }

    public static IEnumerable<EntryPointInfo> FindClassInheritanceEntryPoints(
        SourceModel source,
        AnalysisRuleSet rules)
    {
        foreach (var typeDeclaration in source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var typeSymbol = source.SemanticModel.GetDeclaredSymbol(typeDeclaration);
            if (typeSymbol is null)
            {
                continue;
            }

            foreach (var rule in rules.ClassInheritanceEntryPoints.Where(rule => HasBaseType(typeSymbol, rule.BaseTypes)))
            {
                var route = FindRoute(typeDeclaration, rule, source.SemanticModel);

                foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    var methodName = method.Identifier.ValueText;
                    if (!rule.HandlerMethods.Contains(methodName, StringComparer.Ordinal) ||
                        (rule.RequireOverride && !IsOverride(method, source.SemanticModel)))
                    {
                        continue;
                    }

                    yield return new EntryPointInfo(
                        rule.Kind,
                        route?.HttpMethod ?? "UNKNOWN",
                        route?.Route ?? typeSymbol.ToDisplayString(),
                        $"{rule.Kind} {route?.HttpMethod ?? "UNKNOWN"} {route?.Route ?? typeSymbol.ToDisplayString()}",
                        source.FilePath,
                        RoslynSymbolHelpers.GetLine(source.Tree, method));
                }
            }
        }
    }

    private static (string Method, string? Route)? FindHttpAttribute(
        SyntaxList<AttributeListSyntax> attributes,
        AnalysisRuleSet rules)
    {
        foreach (var attribute in attributes.SelectMany(list => list.Attributes))
        {
            var attributeName = attribute.Name.ToString();
            var rule = rules.MvcHttpAttributes.FirstOrDefault(rule =>
                string.Equals(rule.Attribute, attributeName, StringComparison.Ordinal));
            if (rule is null)
            {
                continue;
            }

            return (rule.HttpMethod, attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.GetLiteralString());
        }

        return null;
    }

    private static string? GetAttributeStringArgument(SyntaxList<AttributeListSyntax> attributes, string attributeName)
    {
        return attributes
            .SelectMany(list => list.Attributes)
            .Where(attribute => string.Equals(attribute.Name.ToString(), attributeName, StringComparison.Ordinal)
                || string.Equals(attribute.Name.ToString(), $"{attributeName}Attribute", StringComparison.Ordinal))
            .Select(attribute => attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.GetLiteralString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string CombineRoutes(string prefix, string? suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return prefix.Trim('/');
        }

        return $"{prefix.TrimEnd('/')}/{suffix.TrimStart('/')}".Trim('/');
    }

    private static bool HasBaseType(INamedTypeSymbol typeSymbol, IReadOnlyList<string> baseTypes)
    {
        for (var current = typeSymbol.BaseType; current is not null; current = current.BaseType)
        {
            var name = current.OriginalDefinition.ToDisplayString();
            if (baseTypes.Any(baseType => RuleTypeMatcher.MatchesDisplayName(name, baseType)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOverride(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        if (method.Modifiers.Any(SyntaxKind.OverrideKeyword))
        {
            return true;
        }

        return semanticModel.GetDeclaredSymbol(method)?.IsOverride == true;
    }

    private static (string HttpMethod, string Route)? FindRoute(
        ClassDeclarationSyntax typeDeclaration,
        ClassInheritanceEntryPointRule rule,
        SemanticModel semanticModel)
    {
        foreach (var invocation in typeDeclaration.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(method => rule.RouteProviderMethods.Contains(method.Identifier.ValueText, StringComparer.Ordinal))
            .SelectMany(method => method.DescendantNodes().OfType<InvocationExpressionSyntax>()))
        {
            var methodName = GetInvocationMethodName(invocation);
            if (methodName is null)
            {
                continue;
            }

            var routeMethod = rule.RouteMethods.FirstOrDefault(routeMethod =>
                string.Equals(routeMethod.Method, methodName, StringComparison.Ordinal));
            if (routeMethod is null)
            {
                continue;
            }

            var route = ResolveString(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression, semanticModel);
            if (string.IsNullOrWhiteSpace(route))
            {
                continue;
            }

            return (routeMethod.HttpMethod, route);
        }

        return null;
    }

    private static string? GetInvocationMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null
        };
    }

    private static string? ResolveString(ExpressionSyntax? expression, SemanticModel semanticModel)
    {
        if (expression is null)
        {
            return null;
        }

        var literal = expression.GetLiteralString();
        if (!string.IsNullOrWhiteSpace(literal))
        {
            return literal;
        }

        var constant = semanticModel.GetConstantValue(expression);
        return constant is { HasValue: true, Value: string value } ? value : null;
    }
}

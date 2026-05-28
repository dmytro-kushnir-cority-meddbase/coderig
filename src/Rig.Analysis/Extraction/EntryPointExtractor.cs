using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Analysis.Rules;
using Rig.Domain.Data;

namespace Rig.Analysis.Extraction;

internal static class EntryPointExtractor
{
    public static IEnumerable<EntryPointInfo> FindMinimalApiEntryPoints(SourceModel source, AnalysisRuleSet rules)
    {
        foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.ValueText;
            var rule = rules.MinimalApiEntryPoints.FirstOrDefault(rule => string.Equals(rule.Method, methodName, StringComparison.Ordinal));
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
                RoslynSymbolHelpers.GetLine(source.Tree, invocation)
            );
        }
    }

    public static IEnumerable<EntryPointInfo> FindMvcEntryPoints(SourceModel source, AnalysisRuleSet rules)
    {
        foreach (var controller in source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!controller.Identifier.ValueText.EndsWith("Controller", StringComparison.Ordinal))
            {
                continue;
            }

            var controllerName = controller.Identifier.ValueText;
            var controllerToken = controllerName.Substring(0, controllerName.Length - "Controller".Length).ToLowerInvariant();
            var controllerRoute =
                GetAttributeStringArgument(controller.AttributeLists, "RoutePrefix")
                ?? GetAttributeStringArgument(controller.AttributeLists, "Route")
                ?? "[controller]";
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
                    RoslynSymbolHelpers.GetLine(source.Tree, method)
                );
            }
        }
    }

    public static IEnumerable<EntryPointInfo> FindPageModelEntryPoints(SourceModel source, AnalysisRuleSet rules)
    {
        if (rules.PageModelEntryPoints.Count == 0)
        {
            yield break;
        }

        foreach (var typeDeclaration in source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
            {
                continue;
            }

            var typeSymbol = source.SemanticModel.GetDeclaredSymbol(typeDeclaration);
            if (typeSymbol is null)
            {
                continue;
            }

            foreach (var rule in rules.PageModelEntryPoints.Where(rule => HasBaseType(typeSymbol, rule.BaseTypes)))
            {
                var ns = typeSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                var route = DerivePageRoute(ns, typeSymbol.Name, rule.NamespacePrefix);
                var httpMethod = rule.DefaultMethod ?? "PAGE";

                var publicCtors = typeDeclaration.Members
                    .OfType<ConstructorDeclarationSyntax>()
                    .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    .ToArray();

                if (publicCtors.Length == 0)
                {
                    // Implicit parameterless constructor
                    yield return new EntryPointInfo(
                        rule.Kind,
                        httpMethod,
                        route,
                        $"{rule.Kind} {httpMethod} {route}",
                        source.FilePath,
                        RoslynSymbolHelpers.GetLine(source.Tree, typeDeclaration)
                    );
                }
                else
                {
                    foreach (var ctor in publicCtors)
                    {
                        var paramSummary = string.Join(", ", ctor.ParameterList.Parameters.Select(p => p.Identifier.ValueText));
                        yield return new EntryPointInfo(
                            rule.Kind,
                            httpMethod,
                            route,
                            $"{rule.Kind} {httpMethod} {route}({paramSummary})",
                            source.FilePath,
                            RoslynSymbolHelpers.GetLine(source.Tree, ctor)
                        );
                    }
                }
            }
        }
    }

    private static string DerivePageRoute(string namespaceName, string className, string prefix)
    {
        var withClass = namespaceName.Length > 0 ? $"{namespaceName}.{className}" : className;
        if (withClass.StartsWith(prefix, StringComparison.Ordinal))
        {
            withClass = withClass[prefix.Length..];
        }
        return withClass.Replace('.', '/');
    }

    public static IEnumerable<EntryPointInfo> FindClassInheritanceEntryPoints(SourceModel source, AnalysisRuleSet rules)
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
                    if (
                        !MatchesHandlerMethod(rule, methodName)
                        || !MatchesHandlerParameterTypes(rule, method, source.SemanticModel)
                        || (rule.RequireOverride && !IsOverride(method, source.SemanticModel))
                    )
                    {
                        continue;
                    }

                    var httpMethod = route?.HttpMethod ?? rule.DefaultMethod ?? "UNKNOWN";
                    var routeText = route?.Route ?? $"{typeSymbol.ToDisplayString()}.{methodName}";

                    yield return new EntryPointInfo(
                        rule.Kind,
                        httpMethod,
                        routeText,
                        $"{rule.Kind} {httpMethod} {routeText}",
                        source.FilePath,
                        RoslynSymbolHelpers.GetLine(source.Tree, method)
                    );
                }
            }
        }
    }

    private static (string Method, string? Route)? FindHttpAttribute(SyntaxList<AttributeListSyntax> attributes, AnalysisRuleSet rules)
    {
        string? httpMethod = null;
        string? verbRoute = null;

        foreach (var attribute in attributes.SelectMany(list => list.Attributes))
        {
            var attributeName = attribute.Name.ToString();
            var rule = rules.MvcHttpAttributes.FirstOrDefault(rule =>
                string.Equals(rule.Attribute, attributeName, StringComparison.Ordinal)
            );
            if (rule is null)
            {
                continue;
            }

            httpMethod = rule.HttpMethod;
            verbRoute = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.GetLiteralString();
            break;
        }

        if (httpMethod is null)
        {
            return null;
        }

        // If the HTTP verb attribute has no route, check for a separate [Route] attribute on the method.
        var route = verbRoute ?? GetAttributeStringArgument(attributes, "Route");
        return (httpMethod, route);
    }

    private static string? GetAttributeStringArgument(SyntaxList<AttributeListSyntax> attributes, string attributeName)
    {
        return attributes
            .SelectMany(list => list.Attributes)
            .Where(attribute =>
                string.Equals(attribute.Name.ToString(), attributeName, StringComparison.Ordinal)
                || string.Equals(attribute.Name.ToString(), $"{attributeName}Attribute", StringComparison.Ordinal)
            )
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
        if (baseTypes.Contains("*", StringComparer.Ordinal))
        {
            return true;
        }

        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var name = iface.OriginalDefinition.ToDisplayString();
            if (baseTypes.Any(baseType => RuleTypeMatcher.MatchesDisplayName(name, baseType)))
            {
                return true;
            }
        }

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

    private static bool MatchesHandlerMethod(ClassInheritanceEntryPointRule rule, string methodName)
    {
        return (rule.HandlerMethods ?? []).Contains("*", StringComparer.Ordinal)
            || (rule.HandlerMethods ?? []).Contains(methodName, StringComparer.Ordinal);
    }

    private static bool MatchesHandlerParameterTypes(
        ClassInheritanceEntryPointRule rule,
        MethodDeclarationSyntax method,
        SemanticModel semanticModel
    )
    {
        if (rule.HandlerParameterTypes is null || rule.HandlerParameterTypes.Count == 0)
        {
            return true;
        }

        if (semanticModel.GetDeclaredSymbol(method) is not IMethodSymbol methodSymbol)
        {
            return false;
        }

        return rule.HandlerParameterTypes.All(expected =>
            methodSymbol.Parameters.Any(parameter =>
                RuleTypeMatcher.MatchesDisplayName(parameter.Type.OriginalDefinition.ToDisplayString(), expected)
            )
        );
    }

    private static (string HttpMethod, string Route)? FindRoute(
        ClassDeclarationSyntax typeDeclaration,
        ClassInheritanceEntryPointRule rule,
        SemanticModel semanticModel
    )
    {
        foreach (
            var invocation in typeDeclaration
                .Members.OfType<MethodDeclarationSyntax>()
                .Where(method => (rule.RouteProviderMethods ?? []).Contains(method.Identifier.ValueText, StringComparer.Ordinal))
                .SelectMany(method => method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        )
        {
            var methodName = GetInvocationMethodName(invocation);
            if (methodName is null)
            {
                continue;
            }

            var routeMethod = rule.RouteMethods.FirstOrDefault(routeMethod =>
                string.Equals(routeMethod.Method, methodName, StringComparison.Ordinal)
            );
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
            _ => null,
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

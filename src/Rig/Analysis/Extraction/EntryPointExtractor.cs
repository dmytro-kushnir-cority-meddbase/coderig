using Microsoft.CodeAnalysis;
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
}

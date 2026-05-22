using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal static class DiRegistrationExtractor
{
    public static IEnumerable<DiRegistrationInfo> FindDiRegistrations(
        SourceModel source,
        AnalysisRuleSet rules)
    {
        foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodSymbol = RoslynSymbolHelpers.ResolveMethodSymbol(invocation, source.SemanticModel);
            var methodName = methodSymbol?.Name ?? RoslynSymbolHelpers.TryGetMemberName(invocation);
            if (methodName is null)
            {
                continue;
            }

            var rule = rules.DiRegistrations.FirstOrDefault(rule => rule.Matches(methodName));
            if (rule is null)
            {
                continue;
            }

            var genericTypes = methodSymbol?.TypeArguments ?? [];
            var serviceType = genericTypes.Length switch
            {
                >= 2 => RoslynSymbolHelpers.GetTypeKey(genericTypes[0]),
                1 => RoslynSymbolHelpers.GetTypeKey(genericTypes[0]),
                _ => "unknown"
            };
            var implementationType = genericTypes.Length >= 2
                ? RoslynSymbolHelpers.GetTypeKey(genericTypes[1])
                : genericTypes.Length == 1 && rule.RegistrationKind is "hosted_service" or "http_client" or "dbcontext"
                    ? RoslynSymbolHelpers.GetTypeKey(genericTypes[0])
                    : null;

            yield return new DiRegistrationInfo(
                serviceType,
                implementationType,
                rule.Lifetime,
                rule.RegistrationKind,
                source.FilePath,
                RoslynSymbolHelpers.GetLine(source.Tree, invocation),
                "high",
                "compilation+profile",
                rule.Reason,
                methodName);
        }
    }
}

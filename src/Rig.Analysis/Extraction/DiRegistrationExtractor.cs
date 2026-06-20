using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Domain.Data;
using RuleSet = Rig.Domain.Data.RuleSet;

namespace Rig.Analysis.Extraction;

internal static class DiRegistrationExtractor
{
    public static IEnumerable<DiRegistrationInfo> FindDiRegistrations(SourceModel source, RuleSet rules)
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
                _ => "unknown",
            };
            if (serviceType == "unknown")
            {
                continue;
            }

            var implementationType =
                genericTypes.Length >= 2
                    ? RoslynSymbolHelpers.GetTypeKey(genericTypes[1])
                    : TryGetFactoryForwardedImplementation(invocation, source.SemanticModel)
                        ?? (
                            genericTypes.Length == 1 && rule.RegistrationKind is "hosted_service" or "http_client" or "dbcontext"
                                ? RoslynSymbolHelpers.GetTypeKey(genericTypes[0])
                                : null
                        );

            var registrationKind =
                implementationType is not null
                && genericTypes.Length == 1
                && rule.RegistrationKind is "service_registration" or "tryadd_service_registration"
                    ? "factory_forward"
                    : rule.RegistrationKind;

            yield return new DiRegistrationInfo(
                ServiceType: serviceType,
                ImplementationType: implementationType,
                Lifetime: rule.Lifetime,
                RegistrationKind: registrationKind,
                FilePath: source.FilePath,
                Line: RoslynSymbolHelpers.GetLine(source.Tree, invocation),
                Confidence: "high",
                Basis: "compilation+profile",
                Reason: rule.Reason,
                Evidence: BuildEvidence(source, invocation, methodName)
            );
        }
    }

    private static string? TryGetFactoryForwardedImplementation(InvocationExpressionSyntax registration, SemanticModel semanticModel)
    {
        foreach (var argument in registration.ArgumentList.Arguments)
        {
            var forwardedInvocation = argument.Expression switch
            {
                SimpleLambdaExpressionSyntax { ExpressionBody: InvocationExpressionSyntax invocation } => invocation,
                ParenthesizedLambdaExpressionSyntax { ExpressionBody: InvocationExpressionSyntax invocation } => invocation,
                SimpleLambdaExpressionSyntax
                {
                    Block.Statements: [ReturnStatementSyntax { Expression: InvocationExpressionSyntax invocation }]
                } => invocation,
                ParenthesizedLambdaExpressionSyntax
                {
                    Block.Statements: [ReturnStatementSyntax { Expression: InvocationExpressionSyntax invocation }]
                } => invocation,
                _ => null,
            };

            if (forwardedInvocation is null)
            {
                continue;
            }

            var methodSymbol = RoslynSymbolHelpers.ResolveMethodSymbol(forwardedInvocation, semanticModel);
            if (methodSymbol?.Name == "GetRequiredService" && methodSymbol.TypeArguments.Length == 1)
            {
                return RoslynSymbolHelpers.GetTypeKey(methodSymbol.TypeArguments[0]);
            }
        }

        return null;
    }

    private static string BuildEvidence(SourceModel source, InvocationExpressionSyntax invocation, string methodName)
    {
        var containingMethod = source.SemanticModel.GetEnclosingSymbol(invocation.SpanStart) as IMethodSymbol;
        var containingType = containingMethod?.ContainingType is null
            ? ""
            : RoslynSymbolHelpers.GetTypeKey(containingMethod.ContainingType);
        var containingMethodName = containingMethod is null ? "" : RoslynSymbolHelpers.GetMethodKey(containingMethod);

        return string.Join(
            " ",
            $"method={methodName}",
            $"project={source.ProjectName}",
            $"containing_type={containingType}",
            $"containing_method={containingMethodName}"
        );
    }
}

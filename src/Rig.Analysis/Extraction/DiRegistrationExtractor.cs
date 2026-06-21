using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Domain.Data;
using RuleSet = Rig.Domain.Data.RuleSet;

namespace Rig.Analysis.Extraction;

internal static class DiRegistrationExtractor
{
    // The exact set of method names any DI rule can match (DiRegistrationRule.Matches is an Ordinal
    // Contains over its Methods list). Built once per run so the per-file pass can reject a non-DI
    // invocation by its SYNTACTIC name — before the expensive ResolveMethodSymbol bind — and only
    // resolve the handful that could actually be registrations.
    public static HashSet<string> BuildMethodNameSet(RuleSet rules) =>
        rules.DiRegistrations.SelectMany(rule => rule.Methods).ToHashSet(StringComparer.Ordinal);

    public static IEnumerable<DiRegistrationInfo> FindDiRegistrations(SourceModel source, RuleSet rules, IReadOnlySet<string> diMethodNames)
    {
        if (diMethodNames.Count == 0)
        {
            yield break; // no DI rules — nothing this pass can ever emit; skip the whole tree walk
        }

        foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // Cheap syntactic reject FIRST: the invoked method's source name equals its bound name, and
            // a rule can only match a name in diMethodNames — so an invocation whose call-site name isn't
            // in the set can never be a registration. This skips the per-invocation GetSymbolInfo bind
            // (ResolveMethodSymbol) for the overwhelming majority of calls, which are not DI methods.
            var syntacticName = SyntacticName(invocation);
            if (syntacticName is null || !diMethodNames.Contains(syntacticName))
            {
                continue;
            }

            var methodSymbol = RoslynSymbolHelpers.ResolveMethodSymbol(invocation, source.SemanticModel);
            var methodName = methodSymbol?.Name ?? syntacticName;

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

    // The call-site method name across every invocation shape — `x.Foo()`, `x?.Foo()`, `Foo()`,
    // `Foo<T>()` (SimpleNameSyntax covers IdentifierName + GenericName). Equals the bound method's name
    // for any direct call, so it is a sound pre-bind filter key. Null for shapes with no name token.
    private static string? SyntacticName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            SimpleNameSyntax name => name.Identifier.ValueText,
            _ => null,
        };

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

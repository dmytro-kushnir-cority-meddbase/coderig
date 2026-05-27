using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Domain.Data;

namespace Rig.Analysis.Analysis.Extraction;

internal static class RoslynObservationExtractor
{
    public static IEnumerable<MethodObservationInfo> FindMethodObservations(SourceModel source)
    {
        foreach (var method in source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (source.SemanticModel.GetDeclaredSymbol(method) is not IMethodSymbol methodSymbol)
            {
                continue;
            }

            yield return new MethodObservationInfo(
                RoslynSymbolHelpers.GetMethodKey(methodSymbol),
                RoslynSymbolHelpers.GetMethodDisplayName(methodSymbol),
                source.FilePath,
                RoslynSymbolHelpers.GetLine(source.Tree, method),
                source.ProjectName
            );
        }
    }

    public static IEnumerable<InvocationObservationInfo> FindInvocationObservations(
        SourceModel source
    )
    {
        foreach (
            var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
        )
        {
            var target = RoslynSymbolHelpers.ResolveMethodSymbol(invocation, source.SemanticModel);
            var containingMethod = invocation
                .Ancestors()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault();

            if (
                target is null
                || containingMethod is null
                || source.SemanticModel.GetDeclaredSymbol(containingMethod)
                    is not IMethodSymbol containingSymbol
            )
            {
                continue;
            }

            yield return new InvocationObservationInfo(
                RoslynSymbolHelpers.GetMethodKey(containingSymbol),
                RoslynSymbolHelpers.GetMethodKey(target),
                RoslynSymbolHelpers.GetMethodDisplayName(target),
                source.FilePath,
                RoslynSymbolHelpers.GetLine(source.Tree, invocation),
                "high",
                "compilation",
                "semantic_model_symbol_info"
            );
        }
    }
}

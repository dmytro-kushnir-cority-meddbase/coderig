using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal static class RoslynSymbolHelpers
{
    private static readonly SymbolDisplayFormat MethodKeyFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
    );

    public static IMethodSymbol? ResolveMethodSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(node);
        return symbolInfo.Symbol as IMethodSymbol ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    public static string GetMethodKey(IMethodSymbol symbol)
    {
        var method = symbol.ReducedFrom ?? symbol;
        return method.OriginalDefinition.ToDisplayString(MethodKeyFormat);
    }

    public static string GetTypeKey(ITypeSymbol symbol)
    {
        return symbol.ToDisplayString(MethodKeyFormat);
    }

    public static int GetLine(SyntaxTree tree, SyntaxNode node)
    {
        return tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
    }

    public static string? TryGetMemberName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax memberAccess ? memberAccess.Name.Identifier.ValueText : null;
    }
}

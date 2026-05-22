using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal static class RoslynSymbolHelpers
{
    private static readonly SymbolDisplayFormat MethodKeyFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static IMethodSymbol? ResolveMethodSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(node);
        return symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    public static string GetMethodKey(IMethodSymbol symbol)
    {
        var method = symbol.ReducedFrom ?? symbol;
        return method.OriginalDefinition.ToDisplayString(MethodKeyFormat);
    }

    public static string GetMethodDisplayName(IMethodSymbol symbol)
    {
        var method = symbol.ReducedFrom ?? symbol;
        return $"{method.ContainingType.Name}.{method.Name}";
    }

    public static string GetTypeKey(ITypeSymbol symbol)
    {
        return symbol.ToDisplayString(MethodKeyFormat);
    }

    public static int GetLine(SyntaxTree tree, SyntaxNode node)
    {
        return tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
    }

    public static bool IsLineInside(SyntaxTree tree, SyntaxNode node, int line)
    {
        var span = tree.GetLineSpan(node.Span);
        var start = span.StartLinePosition.Line + 1;
        var end = span.EndLinePosition.Line + 1;
        return line >= start && line <= end;
    }

    public static string? TryGetMemberName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax memberAccess
            ? memberAccess.Name.Identifier.ValueText
            : null;
    }
}

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis.Analysis;

internal static class StringTemplateExtensions
{
    public static string? GetLiteralString(this ExpressionSyntax expression)
    {
        return expression switch
        {
            LiteralExpressionSyntax literal when literal.Token.ValueText is { Length: > 0 } value =>
                value,
            _ => null,
        };
    }

    public static string? GetStringTemplate(this ExpressionSyntax expression)
    {
        return expression switch
        {
            LiteralExpressionSyntax literal => literal.Token.ValueText,
            InterpolatedStringExpressionSyntax interpolated => BuildInterpolatedTemplate(
                interpolated
            ),
            _ => null,
        };
    }

    private static string BuildInterpolatedTemplate(InterpolatedStringExpressionSyntax interpolated)
    {
        var parts = new List<string>();

        foreach (var content in interpolated.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    parts.Add(text.TextToken.ValueText);
                    break;
                case InterpolationSyntax interpolation:
                    parts.Add($"{{{GetPlaceholder(interpolation.Expression)}}}");
                    break;
            }
        }

        return string.Concat(parts);
    }

    private static string GetPlaceholder(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            _ => "expr",
        };
    }
}

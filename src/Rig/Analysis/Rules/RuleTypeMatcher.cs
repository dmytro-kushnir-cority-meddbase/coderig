using Microsoft.CodeAnalysis;

namespace Rig.Analysis;

internal static class RuleTypeMatcher
{
    public static bool MatchesTypeOrInterfaces(ITypeSymbol actualType, string ruleType)
    {
        var actualTypes = EnumerateTypeAndInterfaces(actualType)
            .Select(type => type.OriginalDefinition.ToDisplayString())
            .Distinct(StringComparer.Ordinal);

        return actualTypes.Any(actual => MatchesDisplayName(actual, ruleType, allowSubstring: true));
    }

    public static bool MatchesDisplayName(string actualType, string ruleType, bool allowSubstring = false)
    {
        return string.Equals(actualType, ruleType, StringComparison.Ordinal) ||
            actualType.StartsWith($"{ruleType}<", StringComparison.Ordinal) ||
            actualType.EndsWith($".{ruleType}", StringComparison.Ordinal) ||
            actualType.Contains($".{ruleType}<", StringComparison.Ordinal) ||
            (allowSubstring && actualType.Contains(ruleType, StringComparison.Ordinal));
    }

    private static IEnumerable<ITypeSymbol> EnumerateTypeAndInterfaces(ITypeSymbol type)
    {
        yield return type;

        foreach (var interfaceType in type.AllInterfaces)
        {
            yield return interfaceType;
        }

        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            yield return baseType;
        }
    }
}

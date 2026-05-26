namespace Rig.Cli.Rendering;

internal static class SymbolNameFormatter
{
    public static string Shorten(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || !symbol.Contains("::", StringComparison.Ordinal))
        {
            return symbol;
        }

        var withoutGlobal = symbol.StartsWith("global::", StringComparison.Ordinal)
            ? symbol["global::".Length..]
            : symbol;

        var parameterStart = withoutGlobal.IndexOf('(');
        var memberPart = parameterStart >= 0
            ? withoutGlobal[..parameterStart]
            : withoutGlobal;

        var lastDot = memberPart.LastIndexOf('.');
        if (lastDot < 0)
        {
            return parameterStart >= 0 ? withoutGlobal[..parameterStart] : withoutGlobal;
        }

        var previousDot = memberPart.LastIndexOf('.', lastDot - 1);
        return previousDot >= 0
            ? memberPart[(previousDot + 1)..]
            : memberPart;
    }
}

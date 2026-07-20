namespace CoreAllocations;

// Compiler-owned source semantics that lower to managed allocations without an explicit `new`.
public static class CompilerLoweredScenarios
{
    public static Func<int> CreateCapturingLambda(int value) => () => value;

    public static IEnumerable<int> CreateIterator(int count)
    {
        for (var index = 0; index < count; index++)
        {
            yield return index;
        }
    }

    public static int CallWithImplicitParamsArray() => Sum(1, 2, 3);

    public static int CallWithExistingParamsArray(int[] values) => Sum(values);

    public static int CallWithNoParamsArguments() => Sum();

    public static Func<int, int> CreateCachedNonCapturingLambda() => static value => value + 1;

    public static string SliceRawEndTag(string rawEndTag) => rawEndTag[7..];

    public static ReadOnlySpan<char> SliceRawEndTagWithoutAllocation(string rawEndTag) => rawEndTag.AsSpan(7);

    public static string Concatenate(string prefix, int value) => prefix + ":" + value;

    public static string Interpolate(string prefix, int value) => $"{prefix}:{value}";

    public static string ConstantConcatenation() => "raw" + "tag";

    public static string ConstantInterpolation()
    {
        const string suffix = "tag";
        const string value = $"raw{suffix}";
        return value;
    }

    public static void LoweredRun(string rawEndTag, int value)
    {
        _ = CreateCapturingLambda(value);
        _ = CreateIterator(value);
        _ = CallWithImplicitParamsArray();
        _ = CreateCachedNonCapturingLambda();
        _ = SliceRawEndTag(rawEndTag);
        _ = SliceRawEndTagWithoutAllocation(rawEndTag);
        _ = Concatenate(rawEndTag, value);
        _ = Interpolate(rawEndTag, value);
        _ = ConstantConcatenation();
        _ = ConstantInterpolation();
    }

    private static int Sum(params int[] values) => values.Length;
}

namespace CoreAllocations;

// These methods have stable allocation behavior after compiler lowering, but contain no explicit
// source allocation syntax for the core detector to capture.
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

    private static int Sum(params int[] values) => values.Length;
}

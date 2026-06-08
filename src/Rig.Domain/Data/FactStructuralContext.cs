namespace Rig.Domain.Data;

// Shared encoding for the structural-context facts on ReferenceFact (P1c). The Roslyn
// EffectObservationExtractor reasons over an invocation's ancestor invocations and try/catch
// clauses; the fact layer captures those as flat strings so the stage-2 observation deriver (P2b)
// can reproduce the observations without Roslyn. Encoding is rule-agnostic raw structure — the
// fanout/resilience/concurrency rule data lives in the rules, not here.
public static class FactStructuralContext
{
    // Separates entries in a list (enclosing invocations, caught types). ASCII record separator.
    private const char ListSeparator = '';

    // Separates fields within one enclosing-invocation entry. ASCII unit separator.
    private const char FieldSeparator = '';

    // One enclosing (ancestor) invocation: the receiver's source text (e.g. "Task", "Parallel"),
    // the receiver's resolved static type FQN ("" when unresolved, e.g. a static type access), and
    // the invoked method name. ReceiverText feeds parallel_fanout; ReceiverType feeds
    // resilience_retry.
    public readonly record struct EnclosingInvocation(string ReceiverText, string ReceiverType, string MethodName);

    public static string? EncodeInvocations(IReadOnlyList<EnclosingInvocation> invocations) =>
        invocations.Count == 0
            ? null
            : string.Join(
                ListSeparator.ToString(),
                invocations.Select(i => $"{i.ReceiverText}{FieldSeparator}{i.ReceiverType}{FieldSeparator}{i.MethodName}")
            );

    public static IReadOnlyList<EnclosingInvocation> DecodeInvocations(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return [];

        var result = new List<EnclosingInvocation>();
        foreach (var entry in encoded.Split(ListSeparator))
        {
            var fields = entry.Split(FieldSeparator);
            if (fields.Length == 3)
                result.Add(new EnclosingInvocation(fields[0], fields[1], fields[2]));
        }

        return result;
    }

    public static string? EncodeList(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : string.Join(ListSeparator.ToString(), values);

    public static IReadOnlyList<string> DecodeList(string? encoded) =>
        string.IsNullOrEmpty(encoded) ? [] : encoded.Split(ListSeparator);
}

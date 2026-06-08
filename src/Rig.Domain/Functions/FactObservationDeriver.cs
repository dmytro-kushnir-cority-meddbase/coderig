using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Stage-2-over-facts observation derivation (P2b): reproduces the notes the Roslyn
// EffectObservationExtractor attaches to an effect — looped_effect, parallel_fanout,
// resilience_retry, concurrency_handled — from the structural-context facts captured in P1c
// (EnclosingLoopKind/Detail, EnclosingInvocations, EnclosingCatchTypes) plus the projected
// observation rules. No Roslyn. This is generic infra; the matching data lives in the rules.
//
// read_before_commit is intentionally NOT derived here: it needs cross-invocation ordering (an
// earlier read in the same method body), and it is EF-specific (SaveChanges + DbSet reads) — not
// the LLBLGen/MedDBase target. The facts to add it later already exist (ReceiverType + Line per
// invocation).
public static class FactObservationDeriver
{
    public static IReadOnlyList<EffectObservationInfo> Derive(
        string methodName,
        string? loopKind,
        string? loopDetail,
        IReadOnlyList<FactStructuralContext.EnclosingInvocation> enclosingInvocations,
        IReadOnlyList<string> catchTypes,
        FactObservationRules rules)
    {
        var observations = new List<EffectObservationInfo>();

        // looped_effect — the effect is lexically inside a loop (nearest enclosing loop).
        if (loopKind is not null)
        {
            observations.Add(new EffectObservationInfo(
                "looped_effect", loopKind, loopDetail ?? loopKind, "high", "compilation", "effect_inside_loop"));
        }

        // parallel_fanout — a fanout wrapper (Task.WhenAll / Parallel.ForEach…) lexically encloses
        // the effect. Innermost-first; first match wins (mirrors the Roslyn ancestor walk).
        foreach (var enclosing in enclosingInvocations)
        {
            var fanout = rules.ParallelFanout.FirstOrDefault(f =>
                string.Equals(enclosing.ReceiverText, f.Receiver, StringComparison.Ordinal)
                && f.Methods.Contains(enclosing.MethodName, StringComparer.Ordinal));
            if (fanout is not null)
            {
                var context = $"{fanout.Receiver}.{enclosing.MethodName}";
                observations.Add(new EffectObservationInfo(
                    "parallel_fanout", context, context, "high", "compilation", "effect_inside_parallel_fanout"));
                break;
            }
        }

        // resilience_retry — a wrapper invocation (Execute/ExecuteAsync on a ResiliencePipeline /
        // execution strategy) encloses the effect. Matches the wrapper method + a receiver-type
        // pattern (substring), per rule.
        foreach (var rule in rules.ResilienceRetry)
        {
            var match = enclosingInvocations
                .Where(e => rule.WrapperMethods.Contains(e.MethodName, StringComparer.Ordinal))
                .Select(e => (e.ReceiverType, Pattern: rule.ReceiverTypePatterns.FirstOrDefault(p =>
                    e.ReceiverType.IndexOf(p, StringComparison.Ordinal) >= 0)))
                .FirstOrDefault(m => m.Pattern is not null);
            if (match.Pattern is not null)
            {
                observations.Add(new EffectObservationInfo(
                    "resilience_retry", match.Pattern, match.ReceiverType, "high", "compilation", "effect_inside_resilience_retry"));
                break;
            }
        }

        // concurrency_handled — the effect is a commit (SaveChanges…) wrapped in a try/catch whose
        // caught type matches a concurrency-exception pattern.
        foreach (var rule in rules.ConcurrencyHandled)
        {
            if (!rule.CommitMethods.Contains(methodName, StringComparer.Ordinal))
                continue;

            foreach (var caught in catchTypes)
            {
                var matched = rule.CatchTypePatterns.FirstOrDefault(p => caught.IndexOf(p, StringComparison.Ordinal) >= 0);
                if (matched is not null)
                {
                    observations.Add(new EffectObservationInfo(
                        "concurrency_handled", matched, caught, "high", "compilation", "efcore_optimistic_concurrency_catch"));
                    break;
                }
            }

            if (observations.Any(o => o.Type == "concurrency_handled"))
                break;
        }

        return observations;
    }
}

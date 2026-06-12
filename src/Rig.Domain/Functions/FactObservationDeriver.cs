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
        FactObservationRules rules,
        string? provider = null,
        IReadOnlyList<FactStructuralContext.EnclosingScope>? enclosingScopes = null
    )
    {
        var observations = new List<EffectObservationInfo>();

        // looped_effect — the effect is lexically inside a loop (nearest enclosing loop).
        if (loopKind is not null)
        {
            observations.Add(
                new EffectObservationInfo("looped_effect", loopKind, loopDetail ?? loopKind, "high", "compilation", "effect_inside_loop")
            );
        }

        // parallel_fanout — a fanout wrapper (Task.WhenAll / Parallel.ForEach…) lexically encloses
        // the effect. Innermost-first; first match wins (mirrors the Roslyn ancestor walk).
        foreach (var enclosing in enclosingInvocations)
        {
            var fanout = rules.ParallelFanout.FirstOrDefault(f =>
                string.Equals(enclosing.ReceiverText, f.Receiver, StringComparison.Ordinal)
                && f.Methods.Contains(enclosing.MethodName, StringComparer.Ordinal)
            );
            if (fanout is not null)
            {
                var context = $"{fanout.Receiver}.{enclosing.MethodName}";
                observations.Add(
                    new EffectObservationInfo("parallel_fanout", context, context, "high", "compilation", "effect_inside_parallel_fanout")
                );
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
                .Select(e =>
                    (
                        e.ReceiverType,
                        Pattern: rule.ReceiverTypePatterns.FirstOrDefault(p => e.ReceiverType.IndexOf(p, StringComparison.Ordinal) >= 0)
                    )
                )
                .FirstOrDefault(m => m.Pattern is not null);
            if (match.Pattern is not null)
            {
                observations.Add(
                    new EffectObservationInfo(
                        "resilience_retry",
                        match.Pattern,
                        match.ReceiverType,
                        "high",
                        "compilation",
                        "effect_inside_resilience_retry"
                    )
                );
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
                    observations.Add(
                        new EffectObservationInfo(
                            "concurrency_handled",
                            matched,
                            caught,
                            "high",
                            "compilation",
                            "efcore_optimistic_concurrency_catch"
                        )
                    );
                    break;
                }
            }

            if (observations.Any(o => o.Type == "concurrency_handled"))
                break;
        }

        // resource_span (ordering/nesting) — a span-sensitive effect (this provider) is lexically
        // nested inside a held-resource scope: a transaction-`using` or a `lock`. Proves the resource
        // is held ACROSS the effect ("transaction spans a network call" / "lock held across IO").
        // Innermost-first scope chain; the first scope that satisfies a rule emits the observation.
        if (provider is not null && enclosingScopes is { Count: > 0 })
        {
            foreach (var rule in rules.ResourceSpan)
            {
                // Deny-list: flag every effect except the scope's own expected family.
                if (rule.ExcludeProviders.Contains(provider, StringComparer.Ordinal))
                    continue;

                var scope = enclosingScopes.FirstOrDefault(s =>
                    string.Equals(s.Kind, rule.ScopeKind, StringComparison.Ordinal)
                    && (
                        rule.ScopeTypePatterns.Count == 0
                        || rule.ScopeTypePatterns.Any(p => s.Type.IndexOf(p, StringComparison.Ordinal) >= 0)
                    )
                );
                // EnclosingScope is a struct; a no-match FirstOrDefault yields Kind == null.
                if (scope.Kind is null)
                    continue;

                observations.Add(
                    new EffectObservationInfo(
                        rule.ObservationType,
                        rule.Context,
                        scope.Type.Length == 0 ? rule.Context : scope.Type,
                        "high",
                        "compilation",
                        "effect_inside_held_resource_scope"
                    )
                );
            }
        }

        return observations;
    }
}

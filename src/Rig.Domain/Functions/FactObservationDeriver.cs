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
        IReadOnlyList<FactStructuralContext.EnclosingScope>? enclosingScopes = null,
        // Call-site generic type arguments (comma-joined display FQNs, FactInvocation.TypeArguments).
        // Feeds unserializable_payload — the payload type at a store/serialize boundary. Null for
        // non-generic calls / field writes (which carry no payload type argument).
        string? typeArguments = null,
        // The matched rule's effect operation (e.g. "GET"/"read"). Feeds n_plus_1, whose read-gate is
        // provider+operation. Null for callers that don't carry it (field writes); n_plus_1 then gates
        // on provider alone.
        string? operation = null,
        // The read's KEY ARGUMENT surface (FactInvocation.FirstArgName / FirstArgTemplate + the all-args
        // JSON ArgumentNames / ArgumentTemplates). Feeds n_plus_1 — the loop identifier "varies" when it
        // appears in any of these. Null when the call has no such argument.
        string? firstArgName = null,
        string? firstArgTemplate = null,
        string? argumentNames = null,
        string? argumentTemplates = null
    )
    {
        var observations = new List<EffectObservationInfo>();

        // looped_effect — the effect is lexically inside a loop (nearest enclosing loop).
        if (loopKind is not null)
        {
            observations.Add(
                new EffectObservationInfo(
                    Type: "looped_effect",
                    Context: loopKind,
                    Detail: loopDetail ?? loopKind,
                    Confidence: "high",
                    Basis: "compilation",
                    Reason: "effect_inside_loop"
                )
            );
        }

        // parallel_fanout — a fanout wrapper (Task.WhenAll / Parallel.ForEach…) lexically encloses
        // the effect. Innermost-first; first match wins (mirrors the Roslyn ancestor walk).
        foreach (var enclosing in enclosingInvocations)
        {
            // Match on the FQN of the resolved receiver TYPE (robust to how the call was qualified), not the
            // syntactic receiver text — a fully-qualified `System.Threading.Tasks.Parallel.ForEach` matches
            // exactly as the using-imported `Parallel.ForEach` does.
            var fanout = rules.ParallelFanout.FirstOrDefault(f =>
                string.Equals(enclosing.ReceiverType, f.ReceiverType, StringComparison.Ordinal)
                && f.Methods.Contains(enclosing.MethodName, StringComparer.Ordinal)
            );
            if (fanout is not null)
            {
                var context = $"{fanout.Receiver}.{enclosing.MethodName}";
                observations.Add(
                    new EffectObservationInfo(
                        Type: "parallel_fanout",
                        Context: context,
                        Detail: context,
                        Confidence: "high",
                        Basis: "compilation",
                        Reason: "effect_inside_parallel_fanout"
                    )
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
                        Type: "resilience_retry",
                        Context: match.Pattern,
                        Detail: match.ReceiverType,
                        Confidence: "high",
                        Basis: "compilation",
                        Reason: "effect_inside_resilience_retry"
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
            {
                continue;
            }

            foreach (var caught in catchTypes)
            {
                var matched = rule.CatchTypePatterns.FirstOrDefault(p => caught.IndexOf(p, StringComparison.Ordinal) >= 0);
                if (matched is not null)
                {
                    observations.Add(
                        new EffectObservationInfo(
                            Type: "concurrency_handled",
                            Context: matched,
                            Detail: caught,
                            Confidence: "high",
                            Basis: "compilation",
                            Reason: "efcore_optimistic_concurrency_catch"
                        )
                    );
                    break;
                }
            }

            if (observations.Any(o => o.Type == "concurrency_handled"))
            {
                break;
            }
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
                {
                    continue;
                }

                var scope = enclosingScopes.FirstOrDefault(s =>
                    string.Equals(s.Kind, rule.ScopeKind, StringComparison.Ordinal)
                    && (
                        rule.ScopeTypePatterns.Count == 0
                        || rule.ScopeTypePatterns.Any(p => s.Type.IndexOf(p, StringComparison.Ordinal) >= 0)
                    )
                );
                // EnclosingScope is a struct; a no-match FirstOrDefault yields Kind == null.
                if (scope.Kind is null)
                {
                    continue;
                }

                observations.Add(
                    new EffectObservationInfo(
                        Type: rule.ObservationType,
                        Context: rule.Context,
                        Detail: scope.Type.Length == 0 ? rule.Context : scope.Type,
                        Confidence: "high",
                        Basis: "compilation",
                        Reason: "effect_inside_held_resource_scope"
                    )
                );
            }
        }

        // unserializable_payload (FR-6, RCA #1646) — the effect stores/serializes a payload whose generic
        // TYPE ARGUMENT is a serializer-unsupported type (e.g. LanguageExt.Option / Either, which the store
        // CAN serialize but CANNOT deserialize). Unlike the structural observations above, this keys off the
        // effect's OWN payload type, not the surrounding code. ANNOTATE-only: it adds a note; the effect is
        // never removed. Matched against the call-site type arguments; first matching pattern wins per rule.
        if (provider is not null && !string.IsNullOrEmpty(typeArguments))
        {
            foreach (var rule in rules.SerializationHazard)
            {
                if (rule.Providers.Count > 0 && !rule.Providers.Contains(provider, StringComparer.Ordinal))
                {
                    continue;
                }

                var matched = rule.UnsupportedTypePatterns.FirstOrDefault(p => typeArguments!.IndexOf(p, StringComparison.Ordinal) >= 0);
                if (matched is not null)
                {
                    observations.Add(
                        new EffectObservationInfo(
                            Type: "unserializable_payload",
                            Context: matched,
                            Detail: typeArguments!,
                            Confidence: "high",
                            Basis: "compilation",
                            Reason: "serializer_unsupported_payload_type"
                        )
                    );
                    break;
                }
            }
        }

        // n_plus_1 (FR-3, RCA #2892) — a READ-category effect inside a loop whose KEY ARGUMENT VARIES per
        // iteration: the loop's iteration variable appears in the read's key argument. This refines the
        // structural looped_effect above (which fires for ANY effect in a loop): a read whose key is
        // CONSTANT is hoistable and is NOT an n+1, so it must not fire. The discriminator is the loop
        // identifier (the foreach iteration variable) appearing in any of the call's argument surfaces
        // (first-arg name/template + all positional arg names/templates — an interpolated `$"/var/{id}"`
        // reduces to "/var/{id}", preserving the {id} token). ANNOTATE-only. v1 fires only for foreach,
        // whose loopDetail carries the identifier ("{identifier} in {expression}"); for/while loops have
        // no identifier and are not matched here (looped_effect still covers them).
        if (provider is not null && loopKind is not null && rules.NPlusOne.Count > 0)
        {
            var loopIdentifier = ForeachIdentifier(loopKind, loopDetail);
            if (loopIdentifier is not null)
            {
                foreach (var rule in rules.NPlusOne)
                {
                    if (rule.Providers.Count > 0 && !rule.Providers.Contains(provider, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    if (rule.Operations.Count > 0 && (operation is null || !rule.Operations.Contains(operation, StringComparer.Ordinal)))
                    {
                        continue;
                    }

                    if (KeyVariesWith(loopIdentifier, firstArgName, firstArgTemplate, argumentNames, argumentTemplates))
                    {
                        observations.Add(
                            new EffectObservationInfo(
                                Type: "n_plus_1",
                                Context: loopIdentifier,
                                Detail: loopDetail ?? loopKind,
                                Confidence: "high",
                                Basis: "compilation",
                                Reason: "looped_read_with_varying_key"
                            )
                        );
                        break;
                    }
                }
            }
        }

        return observations;
    }

    // The foreach iteration variable from a loopDetail of the form "{identifier} in {expression}" (e.g.
    // "id in ids" -> "id"). Null for non-foreach loops (for/while carry no identifier) or a malformed
    // detail — the n_plus_1 v1 fires only when a loop identifier is present.
    private static string? ForeachIdentifier(string loopKind, string? loopDetail)
    {
        if (!string.Equals(loopKind, "foreach", StringComparison.Ordinal) || string.IsNullOrEmpty(loopDetail))
        {
            return null;
        }

        var inMarker = loopDetail!.IndexOf(" in ", StringComparison.Ordinal);
        if (inMarker <= 0)
        {
            return null;
        }

        var identifier = loopDetail.Substring(startIndex: 0, length: inMarker).Trim();
        return identifier.Length == 0 ? null : identifier;
    }

    // True when the loop identifier appears as a whole-word token in any of the read's key-argument
    // surfaces: the first-argument member/identifier path, the first-argument string/interp template, or
    // any element of the all-positional-args JSON name/template arrays. Whole-word so "id" matches in
    // "/var/{id}" and "id" but not as a substring of "invalid"/"width".
    private static bool KeyVariesWith(
        string loopIdentifier,
        string? firstArgName,
        string? firstArgTemplate,
        string? argumentNames,
        string? argumentTemplates
    )
    {
        return ContainsToken(haystack: firstArgName, token: loopIdentifier)
            || ContainsToken(haystack: firstArgTemplate, token: loopIdentifier)
            || ContainsToken(haystack: argumentNames, token: loopIdentifier)
            || ContainsToken(haystack: argumentTemplates, token: loopIdentifier);
    }

    // True when `token` occurs in `haystack` bounded by non-identifier characters on both sides (so it is
    // a distinct identifier reference, not a substring of a longer name). A C# identifier char is a
    // letter, digit, or underscore — any other char (`/`, `{`, `}`, `.`, `"`, `,`, quotes) is a boundary,
    // which is exactly what surrounds a varying key in a member path ("a.id"), an interp template
    // ("/var/{id}"), or a JSON arg array (["id"]).
    private static bool ContainsToken(string? haystack, string token)
    {
        if (string.IsNullOrEmpty(haystack))
        {
            return false;
        }

        var from = 0;
        while (true)
        {
            var at = haystack!.IndexOf(token, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            var before = at == 0 ? '\0' : haystack[at - 1];
            var afterIndex = at + token.Length;
            var after = afterIndex >= haystack.Length ? '\0' : haystack[afterIndex];
            if (!IsIdentifierChar(before) && !IsIdentifierChar(after))
            {
                return true;
            }

            from = at + 1;
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

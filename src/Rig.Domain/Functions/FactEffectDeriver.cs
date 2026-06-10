using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Stage-2-over-facts effect derivation: re-derives external effects from the reference index by
// matching invocation targets against effect rules — no Roslyn. The rules are NOT hardcoded here;
// they are the same AnalysisRuleSet.Effects JSON the Roslyn pass uses, projected to the
// fact-matchable subset (FactEffectRule) and passed in by the caller. This file is the generic
// matcher (infra); the detection lives in data — see the "detectors are data" agreement and
// docs/fact-layer-refactor.md.
//
// Fact limitation: stage-1 ReferenceFacts carry the invocation target's *declaring* type (parsed
// from the DocID) but not yet the receiver's static type. So receiverTypes gates are matched
// against the receiver type when a caller supplies it, otherwise approximated against the
// declaring type — sound for instance-method effect APIs where the receiver's type is (or derives
// from) the declaring type (clientpage, chamber_msg). llblgen entity ops, gated on the entity's
// namespace rather than the EntityBase* declaring type, only match once ReferenceFact carries a
// receiver type (slice 2 in the refactor doc).
public static class FactEffectDeriver
{
    private static readonly HashSet<string> EmptyClosure = new(StringComparer.Ordinal);

    public static IReadOnlyList<DerivedEffect> Derive(
        IReadOnlyList<FactInvocation> invocations,
        IReadOnlyList<FactEffectRule> rules,
        string? providerFilter = null,
        IReadOnlyList<(string TypeId, string BaseId)>? baseEdges = null,
        IReadOnlyList<(string Target, string? Enclosing, string FilePath, int Line)>? ctorRefs = null,
        FactObservationRules? observationRules = null,
        IReadOnlyList<(string Target, string? Enclosing, string FilePath, int Line)>? throwRefs = null
    )
    {
        // Precompute a base-type closure per distinct DeclaringTypeBaseTypes set (e.g. ProxyBase).
        // Without base edges, base-gated rules match nothing (the generated proxies aren't indexed).
        var baseEdgeLookup = baseEdges is null ? null : TypeClosure.BuildBaseEdgeLookup(baseEdges);
        var closureCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string>? ClosureFor(FactEffectRule rule)
        {
            if (rule.DeclaringTypeBaseTypes is not { Count: > 0 } roots)
                return null;
            if (baseEdgeLookup is null)
                return EmptyClosure;
            var key = string.Join("|", roots);
            if (!closureCache.TryGetValue(key, out var closure))
            {
                closure = TypeClosure.Compute(baseEdgeLookup, roots);
                closureCache[key] = closure;
            }
            return closure;
        }

        // Dispatch rules drive the call graph, not effects — the Roslyn FindEffects skips them, so
        // we do too (otherwise a dispatch rule with a resolvable resource would leak in as an effect).
        // Invocation rules are the default; constructor rules (MatchConstructor) match ctor refs.
        var invocationRules = rules.Where(r => !r.MatchConstructor && !r.MatchThrow && !r.TreatAsDispatch).ToArray();
        var constructorRules = rules.Where(r => r.MatchConstructor && !r.TreatAsDispatch).ToArray();
        var throwRules = rules.Where(r => r.MatchThrow && !r.TreatAsDispatch).ToArray();

        var results = new List<DerivedEffect>();
        foreach (var inv in invocations)
        {
            var parsed = ParseMethod(inv.Target);
            if (parsed is null)
                continue;
            var (declaringType, methodName) = parsed.Value;

            foreach (var rule in invocationRules)
            {
                if (providerFilter is not null && !string.Equals(rule.Provider, providerFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!rule.Methods.Contains(methodName, StringComparer.Ordinal))
                    continue;
                if (!TypeGateMatches(rule, declaringType, receiverType: inv.Receiver, ClosureFor(rule)))
                    continue;
                if (!ContainingGateMatches(rule, inv.Enclosing))
                    continue;

                // Resolve the resource the same way the Roslyn path does; when it can't be resolved
                // the effect is DROPPED (Roslyn returns null from TryCreateEffect), which is what
                // aligns the fact effects with the index effects.
                var resource = ResolveResource(rule.Resource, inv.Receiver, inv.FirstArgTemplate, inv.FirstArgType, declaringType);
                if (string.IsNullOrWhiteSpace(resource))
                    continue; // matched, but the resource is unresolvable — no effect; let a later rule try

                var observations = observationRules is null
                    ? null
                    : FactObservationDeriver.Derive(
                        methodName,
                        inv.LoopKind,
                        inv.LoopDetail,
                        FactStructuralContext.DecodeInvocations(inv.EnclosingInvocations),
                        FactStructuralContext.DecodeList(inv.CatchTypes),
                        observationRules
                    );

                results.Add(
                    new DerivedEffect(rule.Provider, rule.Operation, resource!, inv.Enclosing, inv.FilePath, inv.Line, observations)
                );
                break; // first matching rule wins
            }
        }

        // Constructor-matched effects (G5): `new XxxEntity(pk[, txn])` is an llblgen fetch. The
        // constructed type (parsed from the ctor DocID) is gated like a declaring type; the argument
        // count from the DocID signature separates the fetch ctor from the empty `new XxxEntity()`.
        if (constructorRules.Length > 0 && ctorRefs is not null)
        {
            foreach (var ctor in ctorRefs)
            {
                var parsed = ParseConstructor(ctor.Target);
                if (parsed is null)
                    continue;
                var (constructedType, argCount) = parsed.Value;

                foreach (var rule in constructorRules)
                {
                    if (providerFilter is not null && !string.Equals(rule.Provider, providerFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (argCount < rule.MinArguments)
                        continue;
                    if (!TypeGateMatches(rule, constructedType, receiverType: null, ClosureFor(rule)))
                        continue;

                    results.Add(
                        new DerivedEffect(rule.Provider, rule.Operation, constructedType, ctor.Enclosing, ctor.FilePath, ctor.Line)
                    );
                    break;
                }
            }
        }

        // Throw-matched effects: a `throw new XxxException(...)` site. The thrown exception TYPE
        // (parsed from the throw ref's target type DocID) is gated like a declaring type — so a rule
        // can scope to a namespace, a name suffix ("Exception"), or a base-exception closure — and
        // the resource is that exception type. A throw rule with no type gate matches every throw.
        if (throwRules.Length > 0 && throwRefs is not null)
        {
            foreach (var thrown in throwRefs)
            {
                var exceptionType = ParseType(thrown.Target);
                if (exceptionType is null)
                    continue;

                foreach (var rule in throwRules)
                {
                    if (providerFilter is not null && !string.Equals(rule.Provider, providerFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!TypeGateMatches(rule, exceptionType, receiverType: null, ClosureFor(rule)))
                        continue;

                    results.Add(
                        new DerivedEffect(rule.Provider, rule.Operation, exceptionType, thrown.Enclosing, thrown.FilePath, thrown.Line)
                    );
                    break;
                }
            }
        }

        return results;
    }

    // A rule with no type gate matches any receiver. Otherwise the declaring type must match a
    // declaringTypes entry, or the receiver type (or, lacking a receiver fact, the declaring type)
    // must match a receiverTypes entry. When declaringTypeNameEndsWith is also set, the declaring
    // type's simple name (last segment) must additionally end with one of the given suffixes —
    // this narrows a broad namespace-prefix gate without hardcoding a type list.
    private static bool TypeGateMatches(
        FactEffectRule rule,
        string declaringType,
        string? receiverType,
        HashSet<string>? declaringBaseClosure
    )
    {
        // Base-type gate (e.g. ProxyBase): when set it is authoritative — the declaring type must
        // be in the base-type closure, AND any simple-name suffix gate must also hold.
        if (rule.DeclaringTypeBaseTypes is { Count: > 0 })
        {
            if (declaringBaseClosure is null || !TypeClosure.Contains(declaringBaseClosure, "T:" + declaringType))
                return false;
            return DeclaringTypeNameSuffixMatches(rule, declaringType);
        }

        var hasDeclaring = rule.DeclaringTypes.Count > 0;
        var hasReceiver = rule.ReceiverTypes.Count > 0;
        if (!hasDeclaring && !hasReceiver)
        {
            // No namespace/type gate at all — but we may still need to apply the name-suffix gate.
            return DeclaringTypeNameSuffixMatches(rule, declaringType);
        }

        if (hasDeclaring && rule.DeclaringTypes.Any(gate => TypeNameMatches(declaringType, gate)))
        {
            // Namespace/prefix gate passed — apply the optional simple-name suffix gate.
            if (!DeclaringTypeNameSuffixMatches(rule, declaringType))
                return false;
            return true;
        }

        if (hasReceiver)
        {
            // Match the receiverTypes gate against BOTH the receiver's static type (P1a — precise
            // for interface-typed / covariant receivers and static-extension dispatch) AND the
            // method's declaring type. The declaring type is a faithful proxy for the receiver's
            // base chain: an instance method is only callable because the receiver derives the type
            // that declares it, so when the declaring type satisfies the gate Roslyn's receiver
            // base-walk would match too. Checking only the precise receiver (as P1a did) silently
            // dropped calls through a derived receiver whose own type isn't the gate — e.g.
            // `ActionsHelper.RedirectUrl(...)` where RedirectUrl is declared on the gated `Helper`
            // (the dominant clientpage_nav family). The fact layer has no base edges for framework
            // receiver types, so the declaring-type proxy is what recovers these.
            if (
                rule.ReceiverTypes.Any(gate =>
                    TypeNameMatches(declaringType, gate) || (receiverType is not null && TypeNameMatches(receiverType, gate))
                )
            )
                return true;
        }

        return false;
    }

    // Returns true when the rule has no declaringTypeNameEndsWith list (no suffix constraint),
    // or when the declaring type's simple name ends with at least one of the listed suffixes.
    private static bool DeclaringTypeNameSuffixMatches(FactEffectRule rule, string declaringType)
    {
        var suffixes = rule.DeclaringTypeNameEndsWith;
        if (suffixes is null || suffixes.Count == 0)
            return true;

        // Simple name = last dot-separated segment (e.g. "BillingItemListProxy" from
        // "MedDBase.Pages.Accounts.BillingItemComponents.BillingItemListProxy").
        var lastDot = declaringType.LastIndexOf('.');
        var simpleName = lastDot >= 0 ? declaringType.Substring(lastDot + 1) : declaringType;
        return suffixes.Any(suffix => simpleName.EndsWith(suffix, StringComparison.Ordinal));
    }

    // FQN equality, or the gate is a namespace/base prefix of the actual type (e.g. an entity
    // type under the "MedDBase.DataAccessTier.EntityClasses" namespace gate).
    private static bool TypeNameMatches(string actual, string gate)
    {
        return string.Equals(actual, gate, StringComparison.Ordinal) || actual.StartsWith(gate + ".", StringComparison.Ordinal);
    }

    // Enclosing-method gates (P2a) — mirror the Roslyn MatchesContainingNamespace/Type/Method,
    // parsed from the reference's EnclosingSymbolId DocID. No base-chain walk (the fact layer has no
    // base edges for the containing type), so containingTypes matches by equality/prefix only.
    private static bool ContainingGateMatches(FactEffectRule rule, string? enclosingDocId)
    {
        var hasNamespace = rule.ContainingNamespaces is { Count: > 0 };
        var hasType = rule.ContainingTypes is { Count: > 0 };
        var hasMethod = rule.ContainingMethods is { Count: > 0 };
        if (!hasNamespace && !hasType && !hasMethod)
            return true;

        var parsed = enclosingDocId is null ? null : ParseMethod(enclosingDocId);
        if (parsed is null)
            return false; // a containing gate is set but there is no enclosing method to match
        var (containingType, containingMethod) = parsed.Value;

        if (hasMethod && !rule.ContainingMethods!.Contains(containingMethod, StringComparer.Ordinal))
            return false;
        if (hasType && !rule.ContainingTypes!.Any(gate => TypeNameMatches(containingType, gate)))
            return false;
        if (hasNamespace)
        {
            var ns = NamespaceOf(containingType);
            if (
                !rule.ContainingNamespaces!.Any(gate =>
                    string.Equals(ns, gate, StringComparison.Ordinal) || ns.StartsWith(gate + ".", StringComparison.Ordinal)
                )
            )
                return false;
        }

        return true;
    }

    // Namespace = the containing type's FQN minus its simple name. (Nested types are
    // indistinguishable from namespaces in a DocID, so a nested-type enclosing over-reports its
    // namespace — a known fidelity gap, harmless for the prefix-style namespace gates in use.)
    private static string NamespaceOf(string typeFqn)
    {
        var lastDot = typeFqn.LastIndexOf('.');
        return lastDot >= 0 ? typeFqn.Substring(0, lastDot) : "";
    }

    // Resolve the effect resource from facts, mirroring EffectExtractor.TryCreateEffect. Returns
    // null when the strategy can't be resolved (Roslyn drops the effect in that case).
    private static string? ResolveResource(
        string strategy,
        string? receiver,
        string? firstArgTemplate,
        string? firstArgType,
        string declaringType
    )
    {
        return strategy switch
        {
            "receiver_type" => receiver,
            // The invocation target's declaring type — independent of how it's called. Needed for
            // statically-imported helpers that have no receiver (e.g. `using static LanguageExt.Prelude;`
            // then a bare `failwith(...)`), where receiver_type resolves to null and drops the effect.
            "declaring_type" => declaringType,
            "argument_type" => firstArgType,
            "string_argument" => firstArgTemplate,
            "http_argument" => firstArgTemplate is null ? null : NormalizeHttpResource(firstArgTemplate),
            // ef_dbset_receiver / ef_query_root / ef_context_receiver / ef_database_facade need EF
            // receiver/DbSet shape facts the stage-1 layer doesn't carry (deferred — not used by the
            // LLBLGen/MedDBase target). Unknown or empty strategy -> null (effect dropped).
            _ => null,
        };
    }

    // Strip the scheme and surrounding slashes from an HTTP resource (port of
    // EffectExtractor.NormalizeHttpResource): "https://h/p/" -> "h/p", "/p" -> "p".
    private static string NormalizeHttpResource(string url)
    {
        var schemeSeparator = url.IndexOf("://", StringComparison.Ordinal);
        return schemeSeparator >= 0 ? url.Substring(schemeSeparator + 3).TrimEnd('/') : url.TrimStart('/');
    }

    // "M:Ns.Type.Member(args)" -> ("Ns.Type", "Member").
    // Handles generic declaring types correctly: "M:Ns.Foo`1.Bar(`0)" -> ("Ns.Foo", "Bar").
    // The declaring type's backtick arity markers (e.g. `1, `2) are stripped so that
    // rules can match against the open-generic form (e.g. "MedDBase.Application.Core.Messages.EventSubject").
    // Method-level generic arity markers (``1, ``2) are stripped from the method name only.
    private static (string DeclaringType, string Name)? ParseMethod(string docId)
    {
        if (!docId.StartsWith("M:", StringComparison.Ordinal))
            return null;
        var body = docId.Substring(2);
        var paren = body.IndexOf('(');
        if (paren >= 0)
            body = body.Substring(0, paren);
        // Find last dot — this separates the declaring type from the member name.
        // We do NOT strip backticks before this search; `Ns.Foo`1.Bar` has lastDot at Bar.
        var lastDot = body.LastIndexOf('.');
        if (lastDot < 0)
            return null;
        var declaringRaw = body.Substring(0, lastDot);
        var methodRaw = body.Substring(lastDot + 1);
        // Strip generic arity markers from the declaring type (e.g. Foo`1 -> Foo, Bar`2 -> Bar).
        // These are backtick+digits sequences on type name segments.
        var declaring = StripTypeArityMarkers(declaringRaw);
        // Strip method-level generic arity markers (``1 style) from the method name.
        var backtick = methodRaw.IndexOf('`');
        var methodName = backtick >= 0 ? methodRaw.Substring(0, backtick) : methodRaw;
        if (string.IsNullOrEmpty(methodName))
            return null;
        return (declaring, methodName);
    }

    // "M:Ns.InvoiceEntity.#ctor(System.Int32,SD....ITransaction)" -> ("Ns.InvoiceEntity", 2).
    // "M:Ns.InvoiceEntity.#ctor" -> ("Ns.InvoiceEntity", 0). The constructed type is the segment
    // before ".#ctor"; the argument count is the number of top-level (brace-depth-0) parameters, so
    // generic args like List{System.Int32} don't inflate the count.
    private static (string ConstructedType, int ArgCount)? ParseConstructor(string docId)
    {
        if (!docId.StartsWith("M:", StringComparison.Ordinal))
            return null;
        var body = docId.Substring(2);
        var paren = body.IndexOf('(');
        var head = paren >= 0 ? body.Substring(0, paren) : body;
        // head ends with ".#ctor" (instance) or ".#cctor" (static) — strip the ctor segment.
        var ctorMarker = head.LastIndexOf(".#", StringComparison.Ordinal);
        if (ctorMarker < 0)
            return null;
        var constructedType = StripTypeArityMarkers(head.Substring(0, ctorMarker));

        var argCount = 0;
        if (paren >= 0)
        {
            var close = body.LastIndexOf(')');
            if (close > paren)
            {
                var inner = body.Substring(paren + 1, close - paren - 1);
                if (inner.Length > 0)
                {
                    argCount = 1;
                    var depth = 0;
                    foreach (var c in inner)
                    {
                        if (c == '{' || c == '<' || c == '(')
                            depth++;
                        else if (c == '}' || c == '>' || c == ')')
                            depth--;
                        else if (c == ',' && depth == 0)
                            argCount++;
                    }
                }
            }
        }
        return (constructedType, argCount);
    }

    // "T:Ns.Type" -> "Ns.Type" (generic arity markers stripped). Null when not a type DocID.
    private static string? ParseType(string docId)
    {
        if (!docId.StartsWith("T:", StringComparison.Ordinal))
            return null;
        return StripTypeArityMarkers(docId.Substring(2));
    }

    // Removes backtick-arity suffixes from each dot-separated segment of a type name.
    // "Ns.Foo`1" -> "Ns.Foo"; "A.B`2.C`1" -> "A.B.C".
    private static string StripTypeArityMarkers(string typeName)
    {
        // Fast path: no backtick at all.
        if (typeName.IndexOf('`') < 0)
            return typeName;
        // Split on dots, strip arity from each segment, rejoin.
        var segments = typeName.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            var bt = seg.IndexOf('`');
            if (bt >= 0)
                segments[i] = seg.Substring(0, bt);
        }
        return string.Join(".", segments);
    }
}

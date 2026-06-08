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
        IReadOnlyList<(string Target, string? Enclosing, string FilePath, int Line, string? Receiver)> invocations,
        IReadOnlyList<FactEffectRule> rules,
        string? providerFilter = null,
        IReadOnlyList<(string TypeId, string BaseId)>? baseEdges = null,
        IReadOnlyList<(string Target, string? Enclosing, string FilePath, int Line)>? ctorRefs = null)
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

        // Invocation rules are the default; constructor rules (MatchConstructor) match ctor refs.
        var invocationRules = rules.Where(r => !r.MatchConstructor).ToArray();
        var constructorRules = rules.Where(r => r.MatchConstructor).ToArray();

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

                results.Add(new DerivedEffect(rule.Provider, rule.Operation, declaringType, inv.Enclosing, inv.FilePath, inv.Line));
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

                    results.Add(new DerivedEffect(rule.Provider, rule.Operation, constructedType, ctor.Enclosing, ctor.FilePath, ctor.Line));
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
        FactEffectRule rule, string declaringType, string? receiverType, HashSet<string>? declaringBaseClosure)
    {
        // Base-type gate (e.g. ProxyBase): when set it is authoritative — the declaring type must
        // be in the base-type closure, AND any simple-name suffix gate must also hold.
        if (rule.DeclaringTypeBaseTypes is { Count: > 0 })
        {
            if (declaringBaseClosure is null
                || !TypeClosure.Contains(declaringBaseClosure, "T:" + declaringType))
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
            var probe = receiverType ?? declaringType;
            if (rule.ReceiverTypes.Any(gate => TypeNameMatches(probe, gate)))
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
        return string.Equals(actual, gate, StringComparison.Ordinal)
            || actual.StartsWith(gate + ".", StringComparison.Ordinal);
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
                        if (c == '{' || c == '<' || c == '(') depth++;
                        else if (c == '}' || c == '>' || c == ')') depth--;
                        else if (c == ',' && depth == 0) argCount++;
                    }
                }
            }
        }
        return (constructedType, argCount);
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

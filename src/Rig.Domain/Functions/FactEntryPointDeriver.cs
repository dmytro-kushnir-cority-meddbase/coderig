using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Stage-2-over-facts entry-point derivation: re-derives page and action entry points from
// the reference index without Roslyn.  No type/method lists are hardcoded here — they flow
// in from the JSON rules (FactEntryPointRule).  See the "detectors are data" agreement and
// docs/fact-layer-refactor.md.
//
// Two derivation patterns (both driven by data in the rules):
//
//   (A) PAGE entry points (rule.HandlerMethodAttributePrefixes is empty):
//       - BFS over type_relation_facts from each rule base type, collecting all descendent
//         type DocIDs (handles multi-hop and generic instantiation via prefix-strip).
//       - For each descendent type: emit one entry point per constructor (symbol_facts
//         method rows named ".ctor" with ContainingSymbolId == the type).
//         If no explicit constructors exist: emit one entry point at the type declaration line.
//       - Route = namespace stripped of rule.NamespacePrefix, '.' replaced by '/', + '/' + ClassName.
//
//   (B) ACTION entry points (rule.HandlerMethodAttributePrefixes is non-empty):
//       - For each reference_fact with RefKind="ctor" whose TargetSymbolId starts with one of
//         the attribute prefixes: the EnclosingSymbolId is the action method DocID.
//       - Emit one entry point per such method (deduped by FilePath+Line).
//       - Route = namespace stripped of rule.NamespacePrefix, last '.' replaced by '.', rest by '/'.
//
// Fact limitation: parameter *names* are not in symbol_facts (Signature carries types only).
// DisplayName therefore uses the type-only Signature for page constructors and the method's
// Signature for actions.  This matches the information available from stage-1 facts.
public static class FactEntryPointDeriver
{
    // Input facts loaded from the DB (see Reads.LoadFactEntryPointDataAsync):
    public sealed record FactEntryPointData(
        // (TypeSymbolId, RelatedSymbolId) base-type edges
        IReadOnlyList<(string TypeId, string BaseId)> BaseEdges,
        // method symbols (kind="method") — we use only .ctor rows for page EPs
        IReadOnlyList<(string SymbolId, string Name, string? ContainingSymbolId, string Signature, string FilePath, int Line)> Methods,
        // type symbols (kind="type") — for page EPs where the class has no explicit ctor
        IReadOnlyList<(string SymbolId, string Namespace, string FilePath, int Line)> Types,
        // ctor reference_facts: ctor calls to attribute constructors
        IReadOnlyList<(string TargetSymbolId, string? EnclosingSymbolId, string FilePath, int Line)> CtorRefs
    );

    public static IReadOnlyList<DerivedEntryPoint> Derive(
        FactEntryPointData data,
        IReadOnlyList<FactEntryPointRule> rules)
    {
        // Pre-index for performance
        var baseEdges = data.BaseEdges.ToLookup(e => e.BaseId, e => e.TypeId, StringComparer.Ordinal);
        var ctorsByContaining = data.Methods
            .Where(m => m.Name == ".ctor")
            .ToLookup(m => m.ContainingSymbolId ?? "", StringComparer.Ordinal);
        var typeById = data.Types.ToDictionary(t => t.SymbolId, StringComparer.Ordinal);

        var results = new List<DerivedEntryPoint>();
        var seen = new HashSet<(string FilePath, int Line)>();

        foreach (var rule in rules)
        {
            if (rule.HandlerMethodAttributePrefixes.Count > 0)
            {
                // Pattern B: action entry points via attribute ctor refs
                DeriveActions(data.CtorRefs, rule, results, seen);
            }
            else
            {
                // Pattern A: page entry points via BFS + ctors
                DerivePages(baseEdges, ctorsByContaining, typeById, rule, results, seen);
            }
        }

        return results;
    }

    // --- Pattern A: page entry points ---

    private static void DerivePages(
        ILookup<string, string> baseEdges,
        ILookup<string, (string SymbolId, string Name, string? ContainingSymbolId, string Signature, string FilePath, int Line)> ctorsByContaining,
        Dictionary<string, (string SymbolId, string Namespace, string FilePath, int Line)> typeById,
        FactEntryPointRule rule,
        List<DerivedEntryPoint> results,
        HashSet<(string, int)> seen)
    {
        var closure = BfsClosure(baseEdges, rule.BaseTypes);

        foreach (var typeId in closure)
        {
            // Route = strip the namespace prefix, convert remaining '.' to '/'
            // e.g. T:MedDBase.Pages.Accounts.MakePaymentComponents.Create2
            //   -> strip "T:" prefix -> strip "MedDBase.Pages." -> "Accounts.MakePaymentComponents.Create2"
            //   -> "Accounts/MakePaymentComponents/Create2"
            if (!typeId.StartsWith("T:", StringComparison.Ordinal))
                continue;
            var fqn = typeId.Substring(2); // strip "T:"

            var route = BuildTypeRoute(fqn, rule.NamespacePrefix);
            if (route is null)
                continue; // type is outside the namespace prefix

            var ctors = ctorsByContaining[typeId];
            var ctorList = ctors.OrderBy(c => c.Line).ToList();

            if (ctorList.Count > 0)
            {
                foreach (var ctor in ctorList)
                {
                    if (!seen.Add((ctor.FilePath, ctor.Line)))
                        continue;
                    var displayName = BuildPageDisplayName(rule, route, ctor.Signature);
                    results.Add(new DerivedEntryPoint(rule.Kind, rule.DefaultMethod, route, displayName, ctor.FilePath, ctor.Line));
                }
            }
            else if (typeById.TryGetValue(typeId, out var typeRow))
            {
                if (!seen.Add((typeRow.FilePath, typeRow.Line)))
                    continue;
                var displayName = $"{rule.Kind} {rule.DefaultMethod} {route}";
                results.Add(new DerivedEntryPoint(rule.Kind, rule.DefaultMethod, route, displayName, typeRow.FilePath, typeRow.Line));
            }
        }
    }

    // BFS over the base-type edges, collecting all descendant TypeSymbolIds.
    // Handles generic instantiations by also querying on the stripped (non-generic) prefix.
    // Rule base-type names like "MMS.Web.UI.ClientPage" are normalised to their DocID form
    // "T:MMS.Web.UI.ClientPage" so they match the keys in type_relation_facts.
    private static HashSet<string> BfsClosure(ILookup<string, string> baseEdges, IReadOnlyList<string> roots)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>();

        foreach (var root in roots)
        {
            // Normalise: add "T:" DocID prefix if not already present
            var normalised = root.StartsWith("T:", StringComparison.Ordinal) ? root : $"T:{root}";
            // Seed both the exact root and its stripped form (handles T:Foo`1 vs T:Foo{A})
            foreach (var seed in ExpandGeneric(normalised))
            {
                if (visited.Add(seed))
                    frontier.Enqueue(seed);
            }
        }

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var child in baseEdges[current])
            {
                foreach (var expanded in ExpandGeneric(child))
                {
                    if (visited.Add(expanded))
                        frontier.Enqueue(expanded);
                }
            }
        }

        return visited;
    }

    // Given a DocID like T:Foo.Bar{A,B} or T:Foo.Bar`2, returns both the original and
    // the bare prefix T:Foo.Bar (so that base-edge lookups can match both the instantiated
    // and the open-generic forms stored in type_relation_facts).
    private static IEnumerable<string> ExpandGeneric(string typeId)
    {
        yield return typeId;
        var brace = typeId.IndexOf('{');
        if (brace > 0)
        {
            yield return typeId.Substring(0, brace);
            yield break;
        }
        var backtick = typeId.IndexOf('`');
        if (backtick > 0)
            yield return typeId.Substring(0, backtick);
    }

    // "MedDBase.Pages.Accounts.MakePaymentComponents.Create2" + prefix "MedDBase.Pages."
    //   -> "Accounts/MakePaymentComponents/Create2"
    private static string? BuildTypeRoute(string fqn, string namespacePrefix)
    {
        if (!fqn.StartsWith(namespacePrefix, StringComparison.Ordinal))
            return null;
        return fqn.Substring(namespacePrefix.Length).Replace('.', '/');
    }

    // Build the page entry-point DisplayName from the ctor signature.
    // Signature example: "MedDBase.Pages.Accounts.MakePaymentComponents.Create2.Create2(int, int, int, bool)"
    // We want: "page PAGE Accounts/MakePaymentComponents/Create2(int, int, int, bool)"
    private static string BuildPageDisplayName(FactEntryPointRule rule, string route, string signature)
    {
        // Extract the parenthesised params portion, if any
        var paren = signature.IndexOf('(');
        var paramsPart = paren >= 0 ? signature.Substring(paren) : "";
        return $"{rule.Kind} {rule.DefaultMethod} {route}{paramsPart}";
    }

    // --- Pattern B: action entry points ---

    private static void DeriveActions(
        IReadOnlyList<(string TargetSymbolId, string? EnclosingSymbolId, string FilePath, int Line)> ctorRefs,
        FactEntryPointRule rule,
        List<DerivedEntryPoint> results,
        HashSet<(string, int)> seen)
    {
        foreach (var r in ctorRefs)
        {
            if (r.EnclosingSymbolId is null)
                continue;
            if (!rule.HandlerMethodAttributePrefixes.Any(prefix =>
                    r.TargetSymbolId.StartsWith(prefix, StringComparison.Ordinal)))
                continue;

            if (!seen.Add((r.FilePath, r.Line)))
                continue;

            var route = BuildActionRoute(r.EnclosingSymbolId, rule.NamespacePrefix);
            if (route is null)
                continue;

            var displayName = $"{rule.Kind} {rule.DefaultMethod} {route}";
            results.Add(new DerivedEntryPoint(rule.Kind, rule.DefaultMethod, route, displayName, r.FilePath, r.Line));
        }
    }

    // "M:MedDBase.Pages.TestBed.GCCollect"          -> "TestBed.GCCollect"
    // "M:MedDBase.Pages.Accounts.AdvancedPayerDialog.HandleEvent(int)" -> "Accounts/AdvancedPayerDialog.HandleEvent"
    // Strip M: prefix, strip params, strip the NamespacePrefix, then replace all '.' except the
    // last one (which separates class from method name) with '/'.
    private static string? BuildActionRoute(string enclosingSymbolId, string namespacePrefix)
    {
        if (!enclosingSymbolId.StartsWith("M:", StringComparison.Ordinal))
            return null;
        var body = enclosingSymbolId.Substring(2);
        var paren = body.IndexOf('(');
        if (paren >= 0)
            body = body.Substring(0, paren);
        // Strip generic arity markers
        var backtick = body.IndexOf('`');
        if (backtick >= 0)
            body = body.Substring(0, backtick);

        // body = "MedDBase.Pages.Accounts.AdvancedPayerDialog.HandleEvent"
        // Strip namespace prefix "MedDBase.Pages."
        if (!body.StartsWith(namespacePrefix, StringComparison.Ordinal))
            return null;
        body = body.Substring(namespacePrefix.Length);

        // "Accounts.AdvancedPayerDialog.HandleEvent"
        // Find the last '.' which separates ClassName.MethodName
        var lastDot = body.LastIndexOf('.');
        if (lastDot < 0)
            return body; // single-segment method (no class prefix)

        // Everything before the last dot: replace remaining '.' with '/'
        var classPath = body.Substring(0, lastDot).Replace('.', '/');
        var methodName = body.Substring(lastDot); // includes the '.'
        return classPath + methodName; // e.g. "Accounts/AdvancedPayerDialog.HandleEvent"
    }
}

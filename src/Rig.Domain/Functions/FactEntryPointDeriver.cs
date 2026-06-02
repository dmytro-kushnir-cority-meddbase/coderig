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
        // type symbols (kind="type") — for page EPs where the class has no explicit ctor.
        // IsAbstract gates out base/abstract pages, which are never navigable entry points.
        IReadOnlyList<(string SymbolId, string Namespace, string FilePath, int Line, bool IsAbstract)> Types,
        // ctor reference_facts: ctor calls to attribute constructors
        IReadOnlyList<(string TargetSymbolId, string? EnclosingSymbolId, string FilePath, int Line)> CtorRefs
    );

    public static IReadOnlyList<DerivedEntryPoint> Derive(
        FactEntryPointData data,
        IReadOnlyList<FactEntryPointRule> rules)
    {
        // Pre-index for performance. The base-edge lookup is keyed by the generic-stripped base so
        // the BFS can cross generic bases (see TypeClosure).
        var baseEdges = TypeClosure.BuildBaseEdgeLookup(data.BaseEdges);
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
                DeriveActions(data.CtorRefs, baseEdges, rule, results, seen);
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
        Dictionary<string, (string SymbolId, string Namespace, string FilePath, int Line, bool IsAbstract)> typeById,
        FactEntryPointRule rule,
        List<DerivedEntryPoint> results,
        HashSet<(string, int)> seen)
    {
        var closure = TypeClosure.Compute(baseEdges, rule.BaseTypes);

        foreach (var typeId in closure)
        {
            // Abstract/base pages are never navigable — skip them (they only exist to be subclassed).
            if (typeById.TryGetValue(typeId, out var typeInfo) && typeInfo.IsAbstract)
                continue;

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

    // "MedDBase.Pages.Accounts.MakePaymentComponents.Create2" + prefix "MedDBase.Pages."
    //   -> "Accounts/MakePaymentComponents/Create2"
    private static string? BuildTypeRoute(string fqn, string namespacePrefix)
    {
        if (!fqn.StartsWith(namespacePrefix, StringComparison.Ordinal))
            return null;
        return StripArityMarkers(fqn.Substring(namespacePrefix.Length)).Replace('.', '/');
    }

    // Removes generic-arity markers (`1, `2, ...) from anywhere in a DocID-derived name so display
    // routes read cleanly (WorkflowPaneBase`1.Save -> WorkflowPaneBase.Save). Unlike a single
    // IndexOf('`')+truncate, this preserves everything AFTER the arity (notably the method name).
    private static string StripArityMarkers(string text)
    {
        var backtick = text.IndexOf('`');
        if (backtick < 0)
            return text;
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`')
            {
                i++; // skip the backtick
                while (i < text.Length && char.IsDigit(text[i]))
                    i++; // skip the arity digits
                i--; // the for-loop will re-increment
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
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
        ILookup<string, string> baseEdges,
        FactEntryPointRule rule,
        List<DerivedEntryPoint> results,
        HashSet<(string, int)> seen)
    {
        // An attribute-decorated method is an action entry point ONLY when its declaring type is a
        // subtype of one of the rule's base types (e.g. ClientPage). Components/widgets that carry
        // the same attribute but inherit a different base (e.g. ClientControl) are NOT entry points —
        // this gate is what the Roslyn pass enforces and the fact deriver previously skipped.
        var closure = TypeClosure.Compute(baseEdges, rule.BaseTypes);

        foreach (var r in ctorRefs)
        {
            if (r.EnclosingSymbolId is null)
                continue;
            if (!rule.HandlerMethodAttributePrefixes.Any(prefix =>
                    r.TargetSymbolId.StartsWith(prefix, StringComparison.Ordinal)))
                continue;

            var declaringTypeId = DeclaringTypeId(r.EnclosingSymbolId);
            if (declaringTypeId is null || !TypeClosure.Contains(closure, declaringTypeId))
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
        // Strip generic arity markers in place (WorkflowPaneBase`1.Save -> WorkflowPaneBase.Save) —
        // truncating at the first '`' would wrongly drop the method name for generic declaring types.
        body = StripArityMarkers(body);

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

    // "M:MedDBase.Pages.Accounts.AdvancedPayerDialog.HandleEvent(int)" -> "T:MedDBase.Pages.Accounts.AdvancedPayerDialog"
    // Strips the M: prefix, the parameter list, the method's generic arity, and the trailing
    // ".Method" segment, then re-prefixes "T:" to form the declaring type's DocID.
    private static string? DeclaringTypeId(string enclosingSymbolId)
    {
        if (!enclosingSymbolId.StartsWith("M:", StringComparison.Ordinal))
            return null;
        var body = enclosingSymbolId.Substring(2);
        var paren = body.IndexOf('(');
        if (paren >= 0)
            body = body.Substring(0, paren);
        // Do NOT strip the generic arity globally: a generic *type* keeps its `n (e.g.
        // ConfigurationPaneBase`1.Save) and ClosureContains normalises it. Only the segment after
        // the last '.' is the method name, which we drop to leave the declaring type.
        var lastDot = body.LastIndexOf('.');
        if (lastDot <= 0)
            return null; // no declaring type segment
        return "T:" + body.Substring(0, lastDot);
    }
}

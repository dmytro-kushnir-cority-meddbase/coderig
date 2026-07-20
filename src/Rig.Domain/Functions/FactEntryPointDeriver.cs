using System.Text;
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
//   (C) CLASS-INHERITANCE entry points (FactClassInheritanceRule — background/service/WCF/HTTP/
//       actor/lifecycle handlers): a method is an entry point when its declaring type is a subtype
//       of one of the rule's base types (BFS over base AND interface edges — so DataSyncProcess :
//       IBackgroundProcess and Master : IHealthcodeService both qualify) and the method's name
//       matches rule.HandlerMethods (or "*"), optionally gated by RequireOverride and/or an
//       attribute (e.g. WCF [OperationContract]). Route = the declaring type's FQN + ".Method"
//       (the same fallback the Roslyn pass uses when no route provider matches). This is the case
//       that took backend projects from 0 entry points; see docs/effect-capture-validation.md (G1).
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
        // method symbols (kind="method") — ALL methods. Page EPs use only the .ctor rows;
        // class-inheritance EPs use the named handler rows (IsOverride gates RequireOverride rules).
        IReadOnlyList<MethodSymbol> Methods,
        // type symbols (kind="type") — for page EPs where the class has no explicit ctor.
        // IsAbstract gates out base/abstract pages, which are never navigable entry points.
        IReadOnlyList<TypeSymbol> Types,
        // ctor reference_facts: ctor calls to attribute constructors
        IReadOnlyList<SymbolRef> CtorRefs,
        // (TypeSymbolId, RelatedSymbolId) implemented-interface edges. Merged with BaseEdges for the
        // class-inheritance closure so interface-rooted rules (IBackgroundProcess, IHealthcodeService)
        // match. Defaults to empty so existing callers/tests stay source-compatible.
        IReadOnlyList<(string TypeId, string BaseId)>? InterfaceEdges = null
    );

    public static IReadOnlyList<DerivedEntryPoint> Derive(
        FactEntryPointData data,
        IReadOnlyList<FactEntryPointRule> rules,
        IReadOnlyList<FactClassInheritanceRule>? classInheritanceRules = null
    )
    {
        // Pre-index for performance. The base-edge lookup is keyed by the generic-stripped base so
        // the BFS can cross generic bases (see TypeClosure).
        var baseEdges = TypeClosure.BuildBaseEdgeLookup(data.BaseEdges);
        var ctorsByContaining = data
            .Methods.Where(m => m.Name == ".ctor")
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

        if (classInheritanceRules is { Count: > 0 })
        {
            // Pattern C closure spans base + interface edges (interface-rooted backend rules).
            var inheritanceEdges = TypeClosure.BuildBaseEdgeLookup(data.BaseEdges.Concat(data.InterfaceEdges ?? []));
            var attributeRefsByMethod = data
                .CtorRefs.Where(r => r.Enclosing is not null)
                .ToLookup(keySelector: r => r.Enclosing!, elementSelector: r => r.Target, comparer: StringComparer.Ordinal);
            var handlers = data.Methods.Where(m => m.Name != ".ctor").ToArray();

            foreach (var rule in classInheritanceRules)
            {
                DeriveClassInheritance(handlers, inheritanceEdges, attributeRefsByMethod, rule, results, seen);
            }
        }

        return results;
    }

    // --- Pattern A: page entry points ---

    private static void DerivePages(
        ILookup<string, string> baseEdges,
        ILookup<string, MethodSymbol> ctorsByContaining,
        Dictionary<string, TypeSymbol> typeById,
        FactEntryPointRule rule,
        List<DerivedEntryPoint> results,
        HashSet<(string, int)> seen
    )
    {
        var closure = TypeClosure.Compute(baseEdges, rule.BaseTypes);

        foreach (var typeId in closure)
        {
            // Abstract/base pages are never navigable — skip them (they only exist to be subclassed).
            if (typeById.TryGetValue(typeId, out var typeInfo) && typeInfo.IsAbstract)
            {
                continue;
            }

            // Route = strip the namespace prefix, convert remaining '.' to '/'
            // e.g. T:MedDBase.Pages.Accounts.MakePaymentComponents.Create2
            //   -> strip "T:" prefix -> strip "MedDBase.Pages." -> "Accounts.MakePaymentComponents.Create2"
            //   -> "Accounts/MakePaymentComponents/Create2"
            if (!typeId.StartsWith("T:", StringComparison.Ordinal))
            {
                continue;
            }

            var fqn = typeId.Substring(2); // strip "T:"

            var route = BuildTypeRoute(fqn: fqn, namespacePrefix: rule.NamespacePrefix);
            if (route is null)
            {
                continue; // type is outside the namespace prefix
            }

            var ctors = ctorsByContaining[typeId];
            var ctorList = ctors.OrderBy(c => c.Line).ToList();

            if (ctorList.Count > 0)
            {
                foreach (var ctor in ctorList)
                {
                    if (!seen.Add((ctor.FilePath, ctor.Line)))
                    {
                        continue;
                    }

                    var displayName = BuildPageDisplayName(rule, route: route, signature: ctor.Signature);
                    results.Add(
                        new DerivedEntryPoint(
                            Kind: rule.Kind,
                            Method: rule.DefaultMethod,
                            Route: route,
                            DisplayName: displayName,
                            FilePath: ctor.FilePath,
                            Line: ctor.Line,
                            Requires: rule.Requires
                        )
                    );
                }
            }
            else if (typeById.TryGetValue(typeId, out var typeRow))
            {
                if (!seen.Add((typeRow.FilePath, typeRow.Line)))
                {
                    continue;
                }

                var displayName = $"{rule.Kind} {rule.DefaultMethod} {route}";
                results.Add(
                    new DerivedEntryPoint(
                        Kind: rule.Kind,
                        Method: rule.DefaultMethod,
                        Route: route,
                        DisplayName: displayName,
                        FilePath: typeRow.FilePath,
                        Line: typeRow.Line,
                        Requires: rule.Requires
                    )
                );
            }
        }
    }

    // "MedDBase.Pages.Accounts.MakePaymentComponents.Create2" + prefix "MedDBase.Pages."
    //   -> "Accounts/MakePaymentComponents/Create2"
    private static string? BuildTypeRoute(string fqn, string namespacePrefix)
    {
        if (!fqn.StartsWith(namespacePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return StripArityMarkers(fqn.Substring(namespacePrefix.Length)).Replace(oldChar: '.', newChar: '/');
    }

    // Removes generic-arity markers (`1, `2, ...) from anywhere in a DocID-derived name so display
    // routes read cleanly (WorkflowPaneBase`1.Save -> WorkflowPaneBase.Save). Unlike a single
    // IndexOf('`')+truncate, this preserves everything AFTER the arity (notably the method name).
    private static string StripArityMarkers(string text)
    {
        var backtick = text.IndexOf('`');
        if (backtick < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`')
            {
                i++; // skip the backtick
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++; // skip the arity digits
                }

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
        var paren = signature.IndexOf('(');
        var paramsPart = paren >= 0 ? signature.Substring(paren) : "";
        return $"{rule.Kind} {rule.DefaultMethod} {route}{paramsPart}";
    }

    // --- Pattern B: action entry points ---

    private static void DeriveActions(
        IReadOnlyList<SymbolRef> ctorRefs,
        ILookup<string, string> baseEdges,
        FactEntryPointRule rule,
        List<DerivedEntryPoint> results,
        HashSet<(string, int)> seen
    )
    {
        // An attribute-decorated method is an action entry point ONLY when its declaring type is a
        // subtype of one of the rule's base types (e.g. ClientPage). Components/widgets that carry
        // the same attribute but inherit a different base (e.g. ClientControl) are NOT entry points —
        // this gate is what the Roslyn pass enforces and the fact deriver previously skipped.
        var closure = TypeClosure.Compute(strippedBaseEdges: baseEdges, roots: rule.BaseTypes);

        foreach (var r in ctorRefs)
        {
            if (r.Enclosing is null)
            {
                continue;
            }

            if (
                !rule.HandlerMethodAttributePrefixes.Any(predicate: prefix =>
                    r.Target.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)
                )
            )
            {
                continue;
            }

            var declaringTypeId = DeclaringTypeId(enclosingSymbolId: r.Enclosing);
            if (declaringTypeId is null || !TypeClosure.Contains(closure: closure, typeId: declaringTypeId))
            {
                continue;
            }

            if (!seen.Add(item: (r.FilePath, r.Line)))
            {
                continue;
            }

            var route = BuildActionRoute(enclosingSymbolId: r.Enclosing, namespacePrefix: rule.NamespacePrefix);
            if (route is null)
            {
                continue;
            }

            var displayName = $"{rule.Kind} {rule.DefaultMethod} {route}";
            results.Add(
                item: new DerivedEntryPoint(
                    Kind: rule.Kind,
                    Method: rule.DefaultMethod,
                    Route: route,
                    DisplayName: displayName,
                    FilePath: r.FilePath,
                    Line: r.Line,
                    Requires: rule.Requires
                )
            );
        }
    }

    // --- Pattern C: class-inheritance entry points (background / service / WCF / HTTP / actor) ---

    private static void DeriveClassInheritance(
        IReadOnlyList<MethodSymbol> handlers,
        ILookup<string, string> inheritanceEdges,
        ILookup<string, string> attributeRefsByMethod,
        FactClassInheritanceRule rule,
        List<DerivedEntryPoint> results,
        HashSet<(string, int)> seen
    )
    {
        // baseTypes:["*"] means "no base-type gate" (the WCF rule, narrowed instead by its attribute).
        // Otherwise gate on STRICT descendants — a handler declared on the root base itself (e.g. the
        // abstract ServiceBase.Startup) is not an entry point; only its concrete subtypes are.
        var anyBase = rule.BaseTypes.Contains("*", StringComparer.Ordinal);
        var closure = anyBase ? null : TypeClosure.ComputeStrictDescendants(inheritanceEdges, rule.BaseTypes);

        // handlerMethods:["*"] means "any method name" (again the WCF rule — gated by the attribute).
        var anyMethod = rule.HandlerMethods.Contains("*", StringComparer.Ordinal);
        // Hoist the handler-name set out of the per-method loop: rule.HandlerMethods.Contains(name, cmp)
        // is LINQ Enumerable.Contains, which allocates an enumerator on each of the ~50k calls per rule.
        var handlerNames = anyMethod ? null : new HashSet<string>(rule.HandlerMethods, StringComparer.Ordinal);

        foreach (var m in handlers)
        {
            if (m.ContainingSymbolId is null)
            {
                continue;
            }

            if (closure is not null && !TypeClosure.Contains(closure, m.ContainingSymbolId))
            {
                continue;
            }

            if (handlerNames is not null && !handlerNames.Contains(m.Name))
            {
                continue;
            }

            if (rule.RequireOverride && !m.IsOverride)
            {
                continue;
            }

            if (
                rule.HandlerMethodAttributePrefixes.Count > 0
                && !attributeRefsByMethod[m.SymbolId]
                    .Any(target => rule.HandlerMethodAttributePrefixes.Any(p => target.StartsWith(p, StringComparison.Ordinal)))
            )
            {
                continue;
            }

            if (rule.HandlerParameterTypeSimpleNames.Count > 0 && !HasAllParameterTypes(m.Signature, rule.HandlerParameterTypeSimpleNames))
            {
                continue;
            }

            if (!seen.Add((m.FilePath, m.Line)))
            {
                continue;
            }

            var route = BuildInheritanceRoute(containingTypeId: m.ContainingSymbolId, methodName: m.Name);
            if (route is null)
            {
                continue;
            }

            var displayName = $"{rule.Kind} {rule.DefaultMethod} {route}";
            results.Add(
                new DerivedEntryPoint(
                    Kind: rule.Kind,
                    Method: rule.DefaultMethod,
                    Route: route,
                    DisplayName: displayName,
                    FilePath: m.FilePath,
                    Line: m.Line,
                    Requires: rule.Requires
                )
            );
        }
    }

    // True when the method's signature carries a parameter of every required (simple-named) type.
    // Fact signatures show parameter TYPES only (no names), so each comma-separated token in the
    // parenthesised tail is a type; we reduce it to its simple name and match. Approximate vs. the
    // Roslyn semantic-model match, but the discriminating types (e.g. ServerCallContext) are distinct.
    private static bool HasAllParameterTypes(string signature, IReadOnlyList<string> requiredSimpleNames)
    {
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return false;
        }

        var paramSimpleNames = new HashSet<string>(
            signature
                .Substring(startIndex: open + 1, length: close - open - 1)
                .Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Select(SimpleTypeToken),
            StringComparer.Ordinal
        );
        return requiredSimpleNames.All(paramSimpleNames.Contains);
    }

    // "Grpc.Core.ServerCallContext" / "ref System.Int32" / "System.Threading.Tasks.Task<T>" -> simple name.
    private static string SimpleTypeToken(string token)
    {
        var space = token.LastIndexOf(' '); // drop ref/out/in/params modifiers
        if (space >= 0)
        {
            token = token.Substring(space + 1);
        }

        var generic = token.IndexOf('<');
        if (generic >= 0)
        {
            token = token.Substring(startIndex: 0, length: generic);
        }

        var lastDot = token.LastIndexOf('.');
        return lastDot >= 0 ? token.Substring(lastDot + 1) : token;
    }

    // "T:MedDBase.Application.Workflows.Master" + "ProcessHealthcodeQueue"
    //   -> "MedDBase.Application.Workflows.Master.ProcessHealthcodeQueue"
    // Mirrors the Roslyn pass's default route (typeSymbol.ToDisplayString() + "." + methodName) for
    // class-inheritance rules, which carry no namespace prefix. Generic arity markers are stripped.
    private static string? BuildInheritanceRoute(string containingTypeId, string methodName)
    {
        if (!containingTypeId.StartsWith("T:", StringComparison.Ordinal))
        {
            return null;
        }

        var type = StripArityMarkers(containingTypeId.Substring(2));
        return $"{type}.{methodName}";
    }

    // "M:MedDBase.Pages.TestBed.GCCollect"          -> "TestBed.GCCollect"
    // "M:MedDBase.Pages.Accounts.AdvancedPayerDialog.HandleEvent(int)" -> "Accounts/AdvancedPayerDialog.HandleEvent"
    // Strip M: prefix, strip params, strip the NamespacePrefix, then replace all '.' except the
    // last one (which separates class from method name) with '/'.
    private static string? BuildActionRoute(string enclosingSymbolId, string namespacePrefix)
    {
        if (!enclosingSymbolId.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        var body = enclosingSymbolId.Substring(2);
        var paren = body.IndexOf('(');
        if (paren >= 0)
        {
            body = body.Substring(startIndex: 0, length: paren);
        }

        // Strip generic arity markers in place (WorkflowPaneBase`1.Save -> WorkflowPaneBase.Save) —
        // truncating at the first '`' would wrongly drop the method name for generic declaring types.
        body = StripArityMarkers(body);

        if (!body.StartsWith(namespacePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        body = body.Substring(namespacePrefix.Length);

        var lastDot = body.LastIndexOf('.');
        if (lastDot < 0)
        {
            return body; // single-segment method (no class prefix)
        }

        var classPath = body.Substring(startIndex: 0, length: lastDot).Replace(oldChar: '.', newChar: '/');
        var methodName = body.Substring(lastDot); // includes the '.'
        return classPath + methodName; // e.g. "Accounts/AdvancedPayerDialog.HandleEvent"
    }

    // "M:MedDBase.Pages.Accounts.AdvancedPayerDialog.HandleEvent(int)" -> "T:MedDBase.Pages.Accounts.AdvancedPayerDialog"
    // Strips the M: prefix, the parameter list, the method's generic arity, and the trailing
    // ".Method" segment, then re-prefixes "T:" to form the declaring type's DocID.
    private static string? DeclaringTypeId(string enclosingSymbolId)
    {
        if (!enclosingSymbolId.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        var body = enclosingSymbolId.Substring(2);
        var paren = body.IndexOf('(');
        if (paren >= 0)
        {
            body = body.Substring(startIndex: 0, length: paren);
        }

        // Do NOT strip the generic arity globally: a generic *type* keeps its `n (e.g.
        // ConfigurationPaneBase`1.Save) and ClosureContains normalises it. Only the segment after
        // the last '.' is the method name, which we drop to leave the declaring type.
        var lastDot = body.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return null; // no declaring type segment
        }

        return "T:" + body.Substring(startIndex: 0, length: lastDot);
    }
}

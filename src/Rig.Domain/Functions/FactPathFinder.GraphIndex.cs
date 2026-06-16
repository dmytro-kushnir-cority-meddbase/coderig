using Rig.Domain.Data;

namespace Rig.Domain.Functions;

public static partial class FactPathFinder
{
    private sealed class ReverseMaps
    {
        public Dictionary<string, List<string>> Callers = new(StringComparer.Ordinal);

        // target method -> the dispatch SOURCE methods that resolve to it. Built as the exact REVERSE
        // of the forward DispatchTargets (CHA, receiver-blind) over every node, so reverse traversal
        // sees precisely the edges the materialised dispatch_edges table carries (SQL == oracle by
        // construction) — mined-first resolution, error-type recovery and the heuristic fallback all
        // included, instead of a parallel name-matching reimplementation that could drift.
        public Dictionary<string, List<string>> ReverseDispatch = new(StringComparer.Ordinal);

        // Per virtual/base/interface method node: the receiver-type "profile" of all direct call edges
        // that target it. Used to narrow reverse dispatch — a base method only reverse-reaches an
        // override whose declaring type is in scope of SOME caller's receiver. AnyUnreliable=true when
        // at least one caller had a null/unresolved/base-typed receiver (forces the CHA fallback so
        // recall is preserved). Strict-typed receiver DocIDs collected in StrippedReceivers.
        public Dictionary<string, ReceiverProfile> ReceiverProfileByCallee = new(StringComparer.Ordinal);
        public bool NarrowDispatch = true;
    }

    private sealed class ReceiverProfile
    {
        public bool AnyUnreliable;
        public HashSet<string> StrippedReceivers = new(StringComparer.Ordinal);
    }

    private static ReverseMaps BuildReverseMaps(FactGraphData graph, bool narrowDispatch = true, TraversalMode mode = TraversalMode.SyncCut)
    {
        var rev = new ReverseMaps { NarrowDispatch = narrowDispatch };
        foreach (var edge in graph.CallEdges)
        {
            // Sync-cut: an async handoff edge is NOT a synchronous caller->callee link, so it must not
            // make the registrar a predecessor of the callback (else `callers` would claim the
            // registrar reaches the callback synchronously, and the callback wouldn't surface as a
            // background origin via `--roots`). --async keeps the link.
            if (mode == TraversalMode.SyncCut && edge.Kind == EdgeKinds.Handoff)
            {
                continue;
            }

            if (!rev.Callers.TryGetValue(edge.Callee, out var list))
            {
                rev.Callers[edge.Callee] = list = new List<string>();
            }

            list.Add(edge.Caller);

            if (!rev.ReceiverProfileByCallee.TryGetValue(edge.Callee, out var profile))
            {
                rev.ReceiverProfileByCallee[edge.Callee] = profile = new ReceiverProfile();
            }

            var stripped = string.IsNullOrEmpty(edge.ReceiverType) ? null : ReceiverToStrippedTypeId(edge.ReceiverType!);
            if (stripped is null)
            {
                profile.AnyUnreliable = true; // null/unresolved receiver — can dispatch anywhere (CHA)
            }
            else
            {
                profile.StrippedReceivers.Add(stripped);
            }
        }

        // Reverse dispatch = the forward CHA dispatch edges, inverted. (The receiver-blind superset;
        // ReverseDispatchReaches narrows per hop when narrowing is on.)
        var index = BuildIndex(graph, narrowDispatch: false);
        foreach (var node in index.Nodes)
        foreach (var target in DispatchTargets(node, index, receiverType: null))
        {
            if (!rev.ReverseDispatch.TryGetValue(target.Node, out var sources))
            {
                rev.ReverseDispatch[target.Node] = sources = new List<string>();
            }

            sources.Add(node);
        }
        return rev;
    }

    private static IEnumerable<string> Predecessors(string current, GraphIndex index, ReverseMaps rev)
    {
        // Cut symmetry: a cut node yields NO successors forward (Successors `yield break`s on it), so it
        // can never be the runtime caller/dispatcher of `current` — it must not surface as a predecessor
        // in reverse either. Dropping it here is exactly the reverse of the forward leaf-stop, so
        // `callers` cuts the reflection/service-locator seams at the same boundary `reaches`/`tree` do
        // (e.g. a ProvideService<T> seam stops the reverse BFS instead of fanning to all its callers).
        var cutting = index.ApplyTraversalCuts;

        if (rev.Callers.TryGetValue(current, out var direct))
        {
            foreach (var c in direct)
            {
                if (!cutting || !index.IsTraversalCut(c))
                {
                    yield return c;
                }
            }
        }

        // Reverse dispatch: every source method whose (forward) dispatch resolves to `current` —
        // its interface declaration, base virtual, or transitive base of the override chain. Narrowed:
        // only yield a source when SOME caller of it could dispatch to current's declaring type (its
        // receiver is unreliable or in scope of it). Else current can't be the runtime target of any
        // of that source's call sites — drop it.
        if (rev.ReverseDispatch.TryGetValue(current, out var sources))
        {
            var parsed = ParseMethod(current);
            var typeId = parsed?.TypeId;
            foreach (var s in sources)
            {
                if (
                    (!cutting || !index.IsTraversalCut(s))
                    && (typeId is null || ReverseDispatchReaches(baseMethod: s, overrideTypeId: typeId, index: index, rev: rev))
                )
                {
                    yield return s;
                }
            }
        }
    }

    // True when the virtual/base/interface method `baseMethod` can dispatch to an override/impl whose
    // declaring type is `overrideTypeId` (a stripped or DocID type) — i.e. when SOME direct caller of
    // `baseMethod` has a receiver in scope of `overrideTypeId`. CHA fallback (always true) when
    // narrowing is off, when any caller's receiver is unreliable, or when `baseMethod` has no recorded
    // callers (e.g. an entry-point virtual reached only by the framework — keep recall).
    private static bool ReverseDispatchReaches(string baseMethod, string overrideTypeId, GraphIndex index, ReverseMaps rev)
    {
        if (!rev.NarrowDispatch)
        {
            return true;
        }

        if (!rev.ReceiverProfileByCallee.TryGetValue(baseMethod, out var profile))
        {
            return true; // no call edges target it (framework-invoked) — don't narrow it away
        }

        if (profile.AnyUnreliable || profile.StrippedReceivers.Count == 0)
        {
            return true;
        }

        var overrideStripped = TypeClosure.StripGeneric(overrideTypeId);
        foreach (var r in profile.StrippedReceivers)
        {
            // A receiver R reaches this override type when R == overrideType, or overrideType is a
            // descendant of R (R is a base typing of the runtime object), or R is a descendant of
            // overrideType (the override lives on a supertype of R's static type — the CLR walks up).
            if (string.Equals(r, overrideStripped, StringComparison.Ordinal))
            {
                return true;
            }

            if (
                Descendants(r, index).Contains(overrideTypeId)
                || DescendantsContainStripped(declaringTypeId: r, strippedReceiver: overrideStripped, index: index)
            )
            {
                return true;
            }

            if (
                Descendants(overrideTypeId, index).Contains(r)
                || DescendantsContainStripped(declaringTypeId: overrideTypeId, strippedReceiver: r, index: index)
            )
            {
                return true;
            }
        }
        return false;
    }

    private sealed class GraphIndex
    {
        public Dictionary<string, List<CallEdge>> Adjacency = new(StringComparer.Ordinal);
        public Dictionary<string, List<MethodRef>> MethodsByType = new(StringComparer.Ordinal);

        // Methods keyed by the GENERIC-STRIPPED containing type (Foo`2 / Foo{A,B} -> Foo), so
        // dispatch lookups land regardless of whether the base/impl/interface type DocID is the
        // open-generic or an instantiated form. Generic base classes (EditPaneBase`2, the EditPane
        // hierarchy, ...) otherwise break dispatch: the base EDGE stores Foo{A,B} while the METHODS
        // are declared on Foo`2, so an exact-DocID MethodsByType lookup misses them.
        public Dictionary<string, List<MethodRef>> MethodsByStrippedType = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> ImplsByInterface = new(StringComparer.Ordinal);

        // Implementers indexed by interface SIMPLE NAME, but ONLY for edges whose interface failed to
        // resolve to a real type (error-type "!:Name" DocID — pervasive under net48 partial binding
        // when a project doesn't resolve a referenced assembly). Lets impl-dispatch still fire when
        // the CALL's interface resolved (T:Ns.IFoo.M) but the IMPLEMENTER's edge didn't (!:IFoo) —
        // the failure mode that silently kills dispatch and under-reports downstream effects.
        public Dictionary<string, List<string>> ImplsByErrorInterfaceName = new(StringComparer.Ordinal);

        // EXACT Roslyn-mined dispatch edges (dispatch_facts), source member -> [(target, kind)] with
        // kind "override"|"impl". When a method has any of these, they are AUTHORITATIVE for its
        // member-level dispatch (Basis="roslyn") and the name/arity CHA scan is skipped; the CHA scan
        // remains only as a flagged "heuristic" fallback for members with no mined edge, plus the
        // always-on error-type (`!:`) simple-name recovery (Roslyn never bound those, so no mined
        // edge can exist). Empty when the graph carries no mined facts (old store / synthetic test
        // graph) — then everything falls back to CHA exactly as before, marked heuristic.
        public Dictionary<string, List<(string Target, string Kind)>> MinedDispatchBySource = new(StringComparer.Ordinal);

        // Generic-stripped base-edge lookup (stripped base id -> subtype ids), for base-virtual/
        // abstract -> override dispatch. Stripped so a call on the open-generic base reaches overrides
        // on subtypes that store the instantiated base edge (see TypeClosure). Empty when no base edges.
        public ILookup<string, string> StrippedBaseEdges = Enumerable.Empty<string>().ToLookup(x => x, StringComparer.Ordinal);

        // Memoised strict-descendant closure per (stripped) base type, so transitive override dispatch
        // doesn't re-BFS the hierarchy on every visit during the main traversal.
        public Dictionary<string, HashSet<string>> DescendantsCache = new(StringComparer.Ordinal);
        public HashSet<string> Nodes = new(StringComparer.Ordinal);

        // When true (the default for the in-memory traversal), virtual/base/interface dispatch is
        // NARROWED to the call edge's static receiver type (CallEdge.ReceiverType). When false, full
        // CHA — every same-named override/impl — is used (the sound superset, for AllDispatchEdges /
        // dispatch_edges and the SQL-equivalence oracle path).
        public bool NarrowDispatch = true;

        // Traversal-cut rules (Task B): when ApplyTraversalCuts is true, a node matching any of
        // these rules is a traversal leaf — its successors are NOT yielded by Successors. Only
        // enabled for BuildTree / ReachesWithFanout (tree/reaches/path); never for dead-code or
        // callers traversals (which must see the full graph).
        public bool ApplyTraversalCuts = false;
        public IReadOnlyList<FactTraversalCutRule>? TraversalCutRules = null;

        public bool IsTraversalCut(string symbolId)
        {
            if (TraversalCutRules is null)
            {
                return false;
            }

            foreach (var rule in TraversalCutRules)
            {
                if (rule.IsMatch(symbolId))
                {
                    return true;
                }
            }

            return false;
        }

        // Context-bound interface dispatch (state-family narrowing). ContextInterfacePatterns holds the
        // configured interface substrings (e.g. "IWorkflowState"); StateFamilyByController maps a context
        // type (a "controller", normalised "T:"+stripped) to the concrete impl types bound to it via the
        // BindingBase{C} base edge (e.g. all InvoiceDebtChase state types). Empty unless a context-dispatch
        // rule is supplied. Used in Successors (carry the controller across the interface call) and
        // DispatchTargets (narrow the impl fan-out to the controller's family).
        public IReadOnlyList<string> ContextInterfacePatterns = [];
        public readonly Dictionary<string, HashSet<string>> StateFamilyByController = new(StringComparer.Ordinal);

        public bool IsContextInterface(string typeId)
        {
            foreach (var pattern in ContextInterfacePatterns)
            {
                if (typeId.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static GraphIndex BuildIndex(FactGraphData graph, bool narrowDispatch = true)
    {
        var index = new GraphIndex { NarrowDispatch = narrowDispatch };
        foreach (var edge in graph.CallEdges)
        {
            if (!index.Adjacency.TryGetValue(edge.Caller, out var list))
            {
                index.Adjacency[edge.Caller] = list = new List<CallEdge>();
            }

            list.Add(edge);
            index.Nodes.Add(edge.Caller);
            index.Nodes.Add(edge.Callee);
        }
        index.MethodsByType = graph
            .Methods.Where(m => m.ContainingTypeId is not null)
            .GroupBy(m => m.ContainingTypeId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        index.MethodsByStrippedType = graph
            .Methods.Where(m => m.ContainingTypeId is not null)
            .GroupBy(m => TypeClosure.StripGeneric(m.ContainingTypeId!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        index.ImplsByInterface = graph
            .ImplementsEdges.GroupBy(e => e.InterfaceType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ImplType).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        index.ImplsByErrorInterfaceName = graph
            .ImplementsEdges.Where(e => e.InterfaceType.StartsWith("!:", StringComparison.Ordinal))
            .GroupBy(e => SimpleTypeName(e.InterfaceType), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ImplType).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        index.StrippedBaseEdges = TypeClosure.BuildBaseEdgeLookup(
            (graph.BaseEdges ?? new List<BaseEdge>()).Select(e => (e.SubType, e.BaseType))
        );
        BuildContextFamilies(index, graph, graph.ContextRules);
        // Shaping carried on the graph (set once by ShapeGraph at load) drives the cut uniformly for
        // every traversal that builds an index — forward Successors AND reverse Predecessors — so no
        // command can accidentally walk an unshaped graph. Null/empty => no cut (the --raw / dead path).
        if (graph.CutRules is { Count: > 0 })
        {
            index.TraversalCutRules = graph.CutRules;
            index.ApplyTraversalCuts = true;
        }
        foreach (var fact in graph.MinedDispatch ?? new List<DispatchFact>())
        {
            if (!index.MinedDispatchBySource.TryGetValue(fact.SourceMember, out var list))
            {
                index.MinedDispatchBySource[fact.SourceMember] = list = new List<(string, string)>();
            }

            if (!list.Contains((fact.TargetMember, fact.Kind)))
            {
                list.Add((fact.TargetMember, fact.Kind));
            }
        }
        foreach (var method in graph.Methods)
        {
            index.Nodes.Add(method.SymbolId);
        }

        return index;
    }

    // Builds the context-bound dispatch maps from the configured rules: for each base edge of the form
    // `S --base--> BindingBase{C}` (the binding base matched by substring), bind the impl S — and every
    // transitive subtype of S — to the context type C. So a dispatch of a context-interface member carried
    // with controller C narrows to exactly the family { S, descendants(S) } per C. No rules => no-op.
    private static void BuildContextFamilies(GraphIndex index, FactGraphData graph, IReadOnlyList<FactContextDispatchRule>? rules)
    {
        if (rules is not { Count: > 0 })
        {
            return;
        }

        index.ContextInterfacePatterns = rules.Select(r => r.Interface).Distinct(StringComparer.Ordinal).ToArray();

        void Bind(string controllerKey, string stateTypeId)
        {
            if (!index.StateFamilyByController.TryGetValue(controllerKey, out var family))
            {
                index.StateFamilyByController[controllerKey] = family = new HashSet<string>(StringComparer.Ordinal);
            }

            family.Add(stateTypeId);
        }

        foreach (var edge in graph.BaseEdges ?? new List<BaseEdge>())
        foreach (var rule in rules)
        {
            if (edge.BaseType.IndexOf(rule.BindingBase, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var contextArg = ExtractGenericArg(edge.BaseType);
            if (contextArg is null)
            {
                continue;
            }

            var controllerKey = NormType(contextArg);
            Bind(controllerKey: controllerKey, stateTypeId: NormType(edge.SubType));
            foreach (var descendant in Descendants(edge.SubType, index))
            {
                Bind(controllerKey: controllerKey, stateTypeId: NormType(descendant));
            }
        }
    }

    // Normalised type key: a leading "T:" plus the generic-stripped name, matching the form ParseMethod
    // type ids and Descendants results use, so context-family membership compares apples to apples.
    private static string NormType(string typeId)
    {
        var body = typeId.StartsWith("T:", StringComparison.Ordinal) ? typeId.Substring(2) : typeId;
        return "T:" + TypeClosure.StripGeneric(body);
    }

    // The first top-level generic argument of a DocID type, i.e. the X in "Ns.Base{X}" (DocID renders
    // closed generics with braces). Null when there is no brace group. Honours nesting so "Base{A{B}}"
    // returns "A{B}".
    private static string? ExtractGenericArg(string typeId)
    {
        var open = typeId.IndexOf('{');
        if (open < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = open; i < typeId.Length; i++)
        {
            if (typeId[i] == '{')
            {
                depth++;
            }
            else if (typeId[i] == '}' && --depth == 0)
            {
                return typeId.Substring(startIndex: open + 1, length: i - open - 1).Trim();
            }
        }
        return null;
    }
}

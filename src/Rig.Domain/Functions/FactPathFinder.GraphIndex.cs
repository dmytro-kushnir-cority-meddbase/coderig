using System.Collections.Concurrent;
using Rig.Domain.Data;

namespace Rig.Domain.Functions;

public static partial class FactPathFinder
{
    private sealed class ReverseMaps
    {
        public Dictionary<string, List<string>> Callers = new(StringComparer.Ordinal);

        // callee -> the set of callers that reach it via a NON-VIRTUAL `base.M()` call (CallEdge.NonVirtual).
        // These stay in `Callers` (a base call IS a real direct caller of the base BODY — direct
        // `callers(base)` still lists them), but they are EXCLUDED when the callee was itself reached via the
        // reverse override-dispatch fan: a `base.M()` binds to exactly the base and can never be a reverse-
        // reacher of a SIBLING override, so it must not ride the override→base→hub-callers fan. A caller may
        // reach a callee both virtually and non-virtually (two call sites); it's only excluded from the fan
        // when EVERY edge from it to the callee is non-virtual — recorded here as "non-virtual edge present"
        // and reconciled against `Callers` at query time.
        public Dictionary<string, HashSet<string>> NonVirtualCallers = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> VirtualCallers = new(StringComparer.Ordinal);

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

    // descendantsFrom: when the caller already holds an index built from the SAME graph, its
    // DescendantsCache is shared into the internal index below — the strict-descendant closure is a pure
    // function of graph.BaseEdges (identical across every index from this graph), so a set cached via one
    // index is valid for the other. Lets each type's descendants be computed ONCE per command (the dispatch
    // scan here + the caller's later Predecessors hops) instead of once per index.
    private static ReverseMaps BuildReverseMaps(
        FactGraphData graph,
        bool narrowDispatch = true,
        TraversalMode mode = TraversalMode.SyncCut,
        GraphIndex? descendantsFrom = null
    )
    {
        var rev = new ReverseMaps { NarrowDispatch = narrowDispatch };
        foreach (var edge in graph.CallEdges)
        {
            // A handoff edge is NOT a synchronous caller->callee link, so under SyncCut it must not make
            // the registrar a predecessor of the callback (else `callers` would claim the registrar reaches
            // the callback synchronously, and the callback wouldn't surface as a background origin via
            // `--roots`). Under AsyncExact the link is kept EXCEPT for delivery fan-out; AsyncInclude keeps
            // all. CutsHandoff centralizes the policy so this reverse walk and the forward Dispatch walk agree.
            if (CutsHandoff(mode, edge))
            {
                continue;
            }

            if (!rev.Callers.TryGetValue(edge.Callee, out var list))
            {
                rev.Callers[edge.Callee] = list = new List<string>();
            }

            list.Add(edge.Caller);

            // Track whether THIS caller reaches the callee virtually vs via a non-virtual `base.M()` call.
            // A caller excluded from the reverse override-dispatch fan only when ALL its edges to the callee
            // are non-virtual (a single virtual call site keeps it on the fan), so record both and reconcile.
            var bucket = edge.NonVirtual ? rev.NonVirtualCallers : rev.VirtualCallers;
            if (!bucket.TryGetValue(edge.Callee, out var callerSet))
            {
                bucket[edge.Callee] = callerSet = new HashSet<string>(StringComparer.Ordinal);
            }

            callerSet.Add(edge.Caller);

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
        // Share the caller's descendant-closure cache (see descendantsFrom note) so the scan below and the
        // caller's later Descendants() hops compute each type's strict descendants once, not once per index.
        if (descendantsFrom is not null)
        {
            index.DescendantsCache = descendantsFrom.DescendantsCache;
        }

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

    private static IEnumerable<(string Pred, bool ViaReverseDispatch)> Predecessors(
        string current,
        GraphIndex index,
        ReverseMaps rev,
        // True when `current` was itself reached via the reverse override-dispatch fan (it is a base/virtual
        // method climbed up from one of its overrides). Then its NON-VIRTUAL `base.M()` direct callers must
        // be skipped: a `base.M()` binds to exactly THIS base and can never have run the SIBLING override we
        // climbed from, so it isn't a reverse-reacher of that override. Direct `callers(base)` queries pass
        // false, so those base callers are still listed. (Off => prior all-fan behavior, e.g. old graphs.)
        bool currentViaReverseDispatch = false
    )
    {
        // Cut symmetry: a cut node yields NO successors forward (Successors `yield break`s on it), so it
        // can never be the runtime caller/dispatcher of `current` — it must not surface as a predecessor
        // in reverse either. Dropping it here is exactly the reverse of the forward leaf-stop, so
        // `callers` cuts the reflection/service-locator seams at the same boundary `reaches`/`tree` do
        // (e.g. a ProvideService<T> seam stops the reverse BFS instead of fanning to all its callers).
        var cutting = index.ApplyTraversalCuts;

        if (rev.Callers.TryGetValue(current, out var direct))
        {
            // When `current` was reached via the override-dispatch fan, exclude callers whose ONLY edges to
            // it are non-virtual `base.M()` calls (present in NonVirtualCallers and absent from VirtualCallers).
            HashSet<string>? excluded = null;
            if (currentViaReverseDispatch && rev.NonVirtualCallers.TryGetValue(current, out var nonVirtual))
            {
                rev.VirtualCallers.TryGetValue(current, out var virtualCallers);
                foreach (var c in nonVirtual)
                {
                    if (virtualCallers is null || !virtualCallers.Contains(c))
                    {
                        (excluded ??= new HashSet<string>(StringComparer.Ordinal)).Add(c);
                    }
                }
            }

            foreach (var c in direct)
            {
                if ((excluded is null || !excluded.Contains(c)) && (!cutting || !index.IsTraversalCut(c)))
                {
                    yield return (c, false);
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
                    // The source `s` is the base/virtual method `current` dispatches FROM — it was reached via
                    // the reverse override-dispatch fan, so when its own predecessors are walked, its
                    // non-virtual `base.M()` callers must be excluded (they can't have run `current`'s override).
                    yield return (s, true);
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

            // Interface arm: receiver r is an interface that the override's declaring type implements.
            // Mirror of InReceiverScope (forward) — without it, a cleanly-typed interface receiver
            // can't narrow to its impl and the legitimate reverse-dispatch edge is pruned.
            if (ImplementsInterface(strippedType: overrideStripped, interfaceTypeId: r, index: index))
            {
                return true;
            }
        }
        return false;
    }

    private sealed class GraphIndex
    {
        public Dictionary<string, List<CallEdge>> Adjacency = new(StringComparer.Ordinal);

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
        // doesn't re-BFS the hierarchy on every visit during the main traversal. Concurrent so ONE index
        // can be shared across threads (ReachesFromEachSeed's parallel per-seed reach): the cache is pure
        // idempotent memoization — a racing double-compute yields the same set, harmless.
        public ConcurrentDictionary<string, HashSet<string>> DescendantsCache = new(StringComparer.Ordinal);
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
        public bool ApplyTraversalCuts;
        public IReadOnlyList<FactTraversalCutRule>? TraversalCutRules;

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

        // Sort each adjacency list ONCE here — total order: call-site line (primary, preserves source
        // order for distinct-line children), then callee SymbolId (first tie-break, ordinal), then edge
        // Kind (second tie-break), then ReceiverType (final tie-break) — so Successors iterates it
        // directly instead of re-running OrderBy().ThenBy() on every node expansion. The four-key total
        // order is store-independent: same-line edges that share even the callee id are distinguished by
        // Kind/ReceiverType, so a re-index (which reshuffles SQLite rowids) or a parallel-load (which
        // does not preserve insertion order) cannot change child ordering. Line stays primary, so
        // distinct-line children are unaffected. Adjacency is immutable after this build, and BuildIndex
        // finishes single-threaded before any (possibly parallel, e.g. ReachesFromEachSeed) traversal
        // reads the shared index, so the in-place sort is race-free.
        foreach (var list in index.Adjacency.Values)
        {
            list.Sort(
                static (a, b) =>
                {
                    var byLine = a.Line.CompareTo(b.Line);
                    if (byLine != 0)
                    {
                        return byLine;
                    }

                    var byCallee = string.CompareOrdinal(a.Callee, b.Callee);
                    if (byCallee != 0)
                    {
                        return byCallee;
                    }

                    var byKind = string.CompareOrdinal(a.Kind, b.Kind);
                    if (byKind != 0)
                    {
                        return byKind;
                    }

                    return string.CompareOrdinal(a.ReceiverType, b.ReceiverType);
                }
            );
        }

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

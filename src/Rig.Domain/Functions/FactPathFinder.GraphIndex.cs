using System.Collections.Concurrent;
using Rig.Domain.Data;

namespace Rig.Domain.Functions;

public static partial class FactPathFinder
{
    private sealed class ReverseMaps
    {
        public Dictionary<string, List<string>> Callers = new(StringComparer.Ordinal);

        // The reverse-dispatch map. Its MEANING differs by mode:
        //
        //   * NARROWED (NarrowDispatch=true): concrete dispatch TARGET O -> the set of CALLER methods that
        //     dispatch to O, already RECEIVER-NARROWED per call edge. Built by INVERTING forward's own
        //     per-edge output: for each real call edge `caller -(R)-> B`, the targets `DispatchTargets(B, R)`
        //     are exactly the concrete methods that edge can reach, so `caller` reverse-reaches each via that
        //     one dispatch hop. This is the precise mirror of Successors' forward narrowing — the god-seam
        //     over-approximation (a hub's 3,000 unrelated callers riding the fan to every override) is gone
        //     because each caller is only ever attributed to the override ITS receiver resolves to. A
        //     `base.M()` (NonVirtual) edge contributes NO entry (it reaches only the base BODY via Callers,
        //     never a sibling override). Predecessors yields these caller methods DIRECTLY — no further
        //     receiver gate is needed (the narrowing already happened at build time).
        //
        //   * RECEIVER-BLIND (NarrowDispatch=false): the sound superset — concrete TARGET O -> the dispatch
        //     SOURCE (base/interface/hub) methods that resolve to it, the exact reverse of the receiver-blind
        //     forward DispatchTargets(node, null) over every node. Predecessors yields the full hub-fan
        //     (every caller of the hub rides up), matching the SQL/dispatch_edges oracle. Unchanged.
        public Dictionary<string, List<string>> ReverseDispatch = new(StringComparer.Ordinal);

        public bool NarrowDispatch = true;
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

        // The internal index used for dispatch resolution. Receiver narrowing in DispatchTargets is driven by
        // the receiverType ARGUMENT, not by index.NarrowDispatch, so a narrowDispatch:false index serves both
        // the per-edge narrowed inversion (true mode, passing the real receiver) and the per-node blind
        // inversion (false mode, passing null) — exactly mirroring the forward walk.
        var index = BuildIndex(graph, narrowDispatch: false);
        // Share the caller's descendant-closure cache (see descendantsFrom note) so the dispatch resolution
        // here and the caller's later Descendants() hops compute each type's strict descendants once.
        if (descendantsFrom is not null)
        {
            index.DescendantsCache = descendantsFrom.DescendantsCache;
        }

        // Memoise DispatchTargets per (hub B, stripped receiver R) — the god-seam has ~49 distinct receivers
        // across ~3,000 call edges into the same hub, so this collapses ~3,000 resolutions to ~49. A distinct
        // sentinel keys the null/unstripped-receiver case (full CHA, DispatchTargets(B, null)).
        const string nullReceiverSentinel = "\0null";
        var dispatchMemo = new Dictionary<(string Hub, string ReceiverKey), List<(string Node, string Kind, string Basis)>>();

        List<(string Node, string Kind, string Basis)> DispatchTargetsMemo(string hub, string? receiver)
        {
            var stripped = string.IsNullOrEmpty(receiver) ? null : ReceiverToStrippedTypeId(receiver!);
            var key = (hub, stripped ?? nullReceiverSentinel);
            if (!dispatchMemo.TryGetValue(key, out var targets))
            {
                // Pass the ORIGINAL receiver string (not the stripped key) so DispatchTargets does its own
                // ResolveNarrowRoot exactly as the forward walk does; a null/unstripped receiver -> full CHA.
                dispatchMemo[key] = targets = DispatchTargets(method: hub, index: index, receiverType: stripped is null ? null : receiver);
            }

            return targets;
        }

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

            // Direct callers (every mode): the real caller of the callee BODY. Includes base.M() callers —
            // a base call IS a direct caller of the base body, so `callers(base)` still lists it.
            if (!rev.Callers.TryGetValue(edge.Callee, out var list))
            {
                rev.Callers[edge.Callee] = list = new List<string>();
            }

            list.Add(edge.Caller);

            if (!narrowDispatch)
            {
                continue; // false mode builds ReverseDispatch per-NODE below (receiver-blind hub-fan)
            }

            // NARROWED per-edge reverse dispatch (true mode): invert forward's own output. A base.M() edge
            // binds to exactly the base body and never a sibling override, so it contributes no dispatch fan
            // (it is already a direct caller above). For an ordinary edge `caller -(R)-> B`, every concrete
            // target O of DispatchTargets(B, R) is a method `caller` reverse-reaches via this one dispatch
            // hop — so `caller` is a (already receiver-narrowed) reverse-dispatch caller of O.
            if (edge.NonVirtual)
            {
                continue;
            }

            foreach (var target in DispatchTargetsMemo(hub: edge.Callee, receiver: edge.ReceiverType))
            {
                if (!rev.ReverseDispatch.TryGetValue(target.Node, out var sources))
                {
                    rev.ReverseDispatch[target.Node] = sources = new List<string>();
                }

                sources.Add(edge.Caller);
            }
        }

        if (!narrowDispatch)
        {
            // RECEIVER-BLIND superset: ReverseDispatch = the forward CHA dispatch edges inverted, per node.
            // O -> [hub methods that resolve to O]. Predecessors yields the full hub-fan (every caller of the
            // hub rides up). The SQL/dispatch_edges oracle equivalence is by construction. Unchanged behaviour.
            foreach (var node in index.Nodes)
            foreach (var target in DispatchTargets(method: node, index: index, receiverType: null))
            {
                if (!rev.ReverseDispatch.TryGetValue(target.Node, out var sources))
                {
                    rev.ReverseDispatch[target.Node] = sources = new List<string>();
                }

                sources.Add(node);
            }
        }

        return rev;
    }

    private static IEnumerable<(string Pred, bool ViaReverseDispatch)> Predecessors(string current, GraphIndex index, ReverseMaps rev)
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
                    yield return (c, false);
                }
            }
        }

        // Reverse dispatch.
        //   * NARROWED mode: `sources` are the CALLER methods that dispatch to `current`, already
        //     receiver-narrowed at build time (BuildReverseMaps inverted forward's per-edge DispatchTargets).
        //     Yield them directly — no per-method receiver gate is needed; the wrong-receiver callers were
        //     never added. The base.M() exclusion falls out for free (NonVirtual edges contributed nothing).
        //   * RECEIVER-BLIND mode: `sources` are the hub/base/interface methods that resolve to `current` —
        //     the full hub-fan rides up, as before.
        if (rev.ReverseDispatch.TryGetValue(current, out var sources))
        {
            foreach (var s in sources)
            {
                if (!cutting || !index.IsTraversalCut(s))
                {
                    yield return (s, true);
                }
            }
        }
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

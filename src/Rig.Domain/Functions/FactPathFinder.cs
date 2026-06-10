using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Stage-2-over-facts path finding: BFS the fact-derived call graph from any symbol matching
// `fromPattern` to any symbol matching `toPattern`, cross-project, with no entry-point anchoring.
// Includes the interface->concrete DI hop (the single-impl dispatch from Q5) reconstructed from
// type-relation facts + DocID member-name matching — no Roslyn, no SemanticModel.
// (Rig.Domain targets netstandard2.0, so this avoids TryAdd / ranges / Contains(string,cmp).)
//
// Dispatch is resolved EDGE-AWARE (receiver-type narrowing): the in-memory traversal narrows a
// virtual/base/interface call to the STATIC RECEIVER TYPE mined onto the call edge (CallEdge.
// ReceiverType) — `company.Save()` reaches CompanyEntity.Save (+ Company subtypes), not all 114
// CommonEntityBase.Save overrides. It falls back to full CHA whenever the receiver is unreliable
// (null/interface/error-type/the declaring base/not a known first-party type), so no real target
// is ever dropped. The precomputed dispatch_edges table and AllDispatchEdges stay CHA (the sound
// superset that bounds the SQL load); narrowing lives ONLY in the in-memory edge traversal.
public static class FactPathFinder
{
    public static IReadOnlyList<PathStep>? Find(FactGraphData graph, string fromPattern, string toPattern, int maxDepth = 20)
    {
        var index = BuildIndex(graph);

        // Parent links carry the edge that reached the node (for path + kind reconstruction),
        // including its enclosing-loop context so the reconstructed path can mark looped hops, and
        // the dispatch fan-out degree so a path that traverses a base-virtual fan-out shows it.
        var parent = new Dictionary<
            string,
            (string From, string Kind, string? File, int Line, string? LoopKind, string? LoopDetail, int Fanout)?
        >(StringComparer.Ordinal);
        var queue = new Queue<(string Node, int Depth)>();
        // Receiver of the edge that reached each node — narrows that node's dispatch when expanded.
        var receiverOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var start in index.Nodes.Where(n => Contains(n, fromPattern)))
        {
            if (parent.ContainsKey(start))
                continue;
            parent[start] = null;
            receiverOf[start] = null;
            queue.Enqueue((start, 0));
        }

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            if (parent[current] is not null && Contains(current, toPattern))
                return Reconstruct(parent, current);

            if (depth >= maxDepth)
                continue;

            foreach (var s in Successors(current, index, receiverOf.TryGetValue(current, out var rc) ? rc : null))
            {
                if (!parent.ContainsKey(s.Node))
                    receiverOf[s.Node] = s.OutReceiver;
                Enqueue(parent, queue, s.Node, current, s.Kind, s.File, s.Line, s.LoopKind, s.LoopDetail, s.Fanout, depth);
            }
        }

        return null;
    }

    // What reaching a node cost: shortest BFS depth, plus the loop-fanout picked up along that
    // shortest path. LoopNesting = how many looped call edges were traversed to first reach the
    // node (0 = no loop on the path; >=1 = fanned out; >=2 = loop-within-loop / nested fanout).
    // NearestLoop* = the enclosing-loop kind/detail of the looped edge closest to the node (the
    // innermost loop wrapping its call chain), for display. BFS-shortest path is used, so the
    // fanout reported is the one on the shortest route — a defensible single answer when a node is
    // reachable several ways.
    // DispatchVia/DispatchDegree (A1/D3/D7): when the shortest path to this node crossed a base->
    // override (or interface->impl) dispatch that fanned ONE source method out to N(>1) targets,
    // DispatchVia = that source method's DocID (e.g. EntityBase.Save) and DispatchDegree = N. The tag
    // is inherited forward through the fanned-out subtree (like NearestLoop), and is null/0 when the
    // node is reachable directly (a real call) or only through single-target dispatch. Lets `reaches`
    // separate genuine per-entry reach from base-virtual dispatch fan-out instead of over-counting it.
    public sealed record ReachInfo(
        int Depth,
        int LoopNesting,
        string? NearestLoopKind,
        string? NearestLoopDetail,
        string? DispatchVia = null,
        int DispatchDegree = 0
    );

    // Full reachability: BFS the call graph (incl. interface->impl dispatch) from every node
    // matching `fromPattern`, returning each reachable method DocID with its shortest depth.
    // Same traversal as Find — so "what does this entry point reach" is consistent with `rig path`.
    // narrowDispatch=false forces full CHA (the receiver-blind superset) — the equivalence oracle
    // path that matches the CHA SQL traversal exactly; the live CLI uses the default (narrowed).
    public static IReadOnlyDictionary<string, int> Reaches(
        FactGraphData graph,
        string fromPattern,
        int maxDepth = 20,
        int maxNodes = 20000,
        bool narrowDispatch = true
    )
    {
        var info = ReachesWithFanout(graph, fromPattern, maxDepth, maxNodes, narrowDispatch);
        var depthOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in info)
            depthOf[kv.Key] = kv.Value.Depth;
        return depthOf;
    }

    // Reachability enriched with loop-fanout: same BFS as Reaches, but each node also carries how
    // many looped call edges were crossed to reach it (and the innermost such loop). Lets callers
    // flag effects that fire inside a loop somewhere along the call chain — the static signal behind
    // the "🔁/⇉ fanout" annotations (true runtime ×N is not statically known; this is the nesting).
    public static IReadOnlyDictionary<string, ReachInfo> ReachesWithFanout(
        FactGraphData graph,
        string fromPattern,
        int maxDepth = 20,
        int maxNodes = 20000,
        bool narrowDispatch = true
    )
    {
        var index = BuildIndex(graph, narrowDispatch);
        var info = new Dictionary<string, ReachInfo>(StringComparer.Ordinal);
        // The static receiver type of the (BFS-shortest) edge that reached each node, carried so that
        // node's own dispatch fan-out can be narrowed edge-aware when it is expanded.
        var receiverOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var start in index.Nodes.Where(n => Contains(n, fromPattern)))
        {
            if (info.ContainsKey(start))
                continue;
            info[start] = new ReachInfo(0, 0, null, null);
            receiverOf[start] = null;
            queue.Enqueue(start);
        }

        while (queue.Count > 0 && info.Count < maxNodes)
        {
            var current = queue.Dequeue();
            var cur = info[current];
            if (cur.Depth >= maxDepth)
                continue;
            foreach (var s in Successors(current, index, receiverOf[current]))
            {
                if (info.ContainsKey(s.Node))
                    continue;
                var looped = s.LoopKind is not null;
                var nesting = cur.LoopNesting + (looped ? 1 : 0);
                var nearKind = looped ? s.LoopKind : cur.NearestLoopKind;
                var nearDetail = looped ? s.LoopDetail : cur.NearestLoopDetail;
                // Dispatch fan-out (A1/D3): when the reaching edge fanned a virtual/base method out to
                // >1 targets, this node is reached via that fan-out, not a real call — tag it with the
                // dispatch SOURCE (s.Via, the virtual method) and degree. A single-target dispatch
                // (degree 1) is deterministic, so it's treated like a real call. Otherwise inherit the
                // tag, so the whole fanned-out subtree (BFS-shortest) carries it — unless reached more
                // directly elsewhere.
                var fannedOut = s.Fanout > 1;
                var via = fannedOut ? s.Via : cur.DispatchVia;
                var degree = fannedOut ? s.Fanout : cur.DispatchDegree;
                info[s.Node] = new ReachInfo(cur.Depth + 1, nesting, nearKind, nearDetail, via, degree);
                receiverOf[s.Node] = s.OutReceiver;
                queue.Enqueue(s.Node);
            }
        }

        return info;
    }

    // Builds the call TREE rooted at every node matching `fromPattern` (rig tree). Same edge model
    // as Reaches/Find (direct calls + interface->impl + base->override dispatch, with loop context),
    // but materialized as a tree for rendering. Each method is EXPANDED ONCE globally: the first time
    // it's reached its children are built; later encounters become a Truncated leaf ("seen"), so a
    // cycle or a heavily-shared callee can't blow the tree up. maxDepth bounds depth; maxNodes bounds
    // total emitted nodes (a Truncated leaf is emitted at the cut). Returns one TraceNode per root.
    public static IReadOnlyList<TraceNode> BuildTree(FactGraphData graph, string fromPattern, int maxDepth = 20, int maxNodes = 20000)
    {
        var index = BuildIndex(graph);
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var budget = new int[] { maxNodes };

        var roots = new List<TraceNode>();
        foreach (var root in index.Nodes.Where(n => Contains(n, fromPattern)).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (budget[0] <= 0)
                break;
            roots.Add(
                BuildNode(
                    root,
                    "entry",
                    loopKind: null,
                    loopDetail: null,
                    fanout: 0,
                    incomingReceiver: null,
                    depth: 0,
                    maxDepth,
                    index,
                    expanded,
                    budget
                )
            );
        }
        return roots;
    }

    private static TraceNode BuildNode(
        string symbol,
        string edgeKind,
        string? loopKind,
        string? loopDetail,
        int fanout,
        // The receiver type of the edge that reached this node — narrows this node's own dispatch fan-out.
        string? incomingReceiver,
        int depth,
        int maxDepth,
        GraphIndex index,
        HashSet<string> expanded,
        int[] budget
    )
    {
        budget[0]--;

        // Already expanded elsewhere (cycle / shared callee), at the depth cap, or out of budget:
        // emit a leaf and don't descend, so the tree stays finite and each method's subtree is
        // printed once.
        if (expanded.Contains(symbol) || depth >= maxDepth || budget[0] <= 0)
            return new TraceNode(symbol, edgeKind, loopKind, loopDetail, EmptyNodes, Truncated: true, Fanout: fanout);

        expanded.Add(symbol);

        var children = new List<TraceNode>();
        foreach (var s in Successors(symbol, index, incomingReceiver))
        {
            if (budget[0] <= 0)
                break;
            children.Add(
                BuildNode(s.Node, s.Kind, s.LoopKind, s.LoopDetail, s.Fanout, s.OutReceiver, depth + 1, maxDepth, index, expanded, budget)
            );
        }
        return new TraceNode(symbol, edgeKind, loopKind, loopDetail, children, Fanout: fanout);
    }

    private static readonly IReadOnlyList<TraceNode> EmptyNodes = new TraceNode[0];

    // Multi-source forward reachability: the union of everything reachable from ANY of the given root
    // symbol IDs, using the same edge model as Reaches/Find/tree (direct calls + method-group/ctor
    // edges + interface->impl and base->override dispatch). Roots are matched by EXACT SymbolId (not
    // substring) — callers pass concrete entry-point DocIDs. Unknown root ids (not present as graph
    // nodes) are skipped. Underpins the unreachable-symbol / dead-code finder: dead = first-party
    // methods − this set − the roots themselves.
    public static HashSet<string> ReachableFromAll(FactGraphData graph, IEnumerable<string> roots, int maxNodes = 2_000_000)
    {
        var index = BuildIndex(graph);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var receiverOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var root in roots)
            if (index.Nodes.Contains(root) && seen.Add(root))
            {
                receiverOf[root] = null;
                queue.Enqueue(root);
            }

        while (queue.Count > 0 && seen.Count < maxNodes)
        {
            var current = queue.Dequeue();
            foreach (var s in Successors(current, index, receiverOf.TryGetValue(current, out var rc) ? rc : null))
                if (seen.Add(s.Node))
                {
                    receiverOf[s.Node] = s.OutReceiver;
                    queue.Enqueue(s.Node);
                }
        }

        return seen;
    }

    // Reverse reachability — every method that can REACH any node matching toPattern (transitive
    // callers), keyed to its shortest reverse hop count. Inverts Successors: direct caller edges,
    // plus the reverse of the dispatch hops — an impl method is reached via its interface's
    // same-named method, an override via its base's. Powers `rig callers` ("which entry points
    // touch this method"), and underpins the planned unreachable-symbol (dead-code) finder.
    public static IReadOnlyDictionary<string, int> ReachedBy(
        FactGraphData graph,
        string toPattern,
        int maxDepth = 20,
        int maxNodes = 20000,
        bool narrowDispatch = true
    )
    {
        var index = BuildIndex(graph, narrowDispatch);
        var rev = BuildReverseMaps(graph, narrowDispatch);

        var depthOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var start in index.Nodes.Where(n => Contains(n, toPattern)))
        {
            if (depthOf.ContainsKey(start))
                continue;
            depthOf[start] = 0;
            queue.Enqueue(start);
        }

        while (queue.Count > 0 && depthOf.Count < maxNodes)
        {
            var current = queue.Dequeue();
            var depth = depthOf[current];
            if (depth >= maxDepth)
                continue;
            foreach (var pred in Predecessors(current, index, rev))
            {
                if (depthOf.ContainsKey(pred))
                    continue;
                depthOf[pred] = depth + 1;
                queue.Enqueue(pred);
            }
        }

        return depthOf;
    }

    // Entry-point CANDIDATES that reach toPattern: the reachable methods with NO predecessor at all
    // (no caller, not an impl of a called interface, not an override of a called base) — the tops of
    // the reverse closure, i.e. methods invoked only by the framework / DI / reflection / externally.
    // The honest static approximation of "which entry points touch this method".
    public static IReadOnlyList<string> EntryRootsReaching(FactGraphData graph, string toPattern, int maxDepth = 20, int maxNodes = 20000)
    {
        var index = BuildIndex(graph);
        var rev = BuildReverseMaps(graph);
        var reachable = ReachedBy(graph, toPattern, maxDepth, maxNodes);
        var roots = new List<string>();
        foreach (var m in reachable.Keys)
            if (!Predecessors(m, index, rev).Any())
                roots.Add(m);
        roots.Sort(StringComparer.Ordinal);
        return roots;
    }

    private sealed class ReverseMaps
    {
        public Dictionary<string, List<string>> Callers = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> InterfacesByType = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> BasesByType = new(StringComparer.Ordinal);
        public HashSet<string> IsOverride = new(StringComparer.Ordinal);

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

    private static ReverseMaps BuildReverseMaps(FactGraphData graph, bool narrowDispatch = true)
    {
        var rev = new ReverseMaps { NarrowDispatch = narrowDispatch };
        foreach (var edge in graph.CallEdges)
        {
            if (!rev.Callers.TryGetValue(edge.Callee, out var list))
                rev.Callers[edge.Callee] = list = new List<string>();
            list.Add(edge.Caller);

            if (!rev.ReceiverProfileByCallee.TryGetValue(edge.Callee, out var profile))
                rev.ReceiverProfileByCallee[edge.Callee] = profile = new ReceiverProfile();
            var stripped = string.IsNullOrEmpty(edge.ReceiverType) ? null : ReceiverToStrippedTypeId(edge.ReceiverType!);
            if (stripped is null)
                profile.AnyUnreliable = true; // null/unresolved receiver — can dispatch anywhere (CHA)
            else
                profile.StrippedReceivers.Add(stripped);
        }
        foreach (var e in graph.ImplementsEdges)
        {
            if (!rev.InterfacesByType.TryGetValue(e.ImplType, out var list))
                rev.InterfacesByType[e.ImplType] = list = new List<string>();
            list.Add(e.InterfaceType);
        }
        foreach (var e in graph.BaseEdges ?? new List<BaseEdge>())
        {
            if (!rev.BasesByType.TryGetValue(e.SubType, out var list))
                rev.BasesByType[e.SubType] = list = new List<string>();
            list.Add(e.BaseType);
        }
        foreach (var m in graph.Methods)
            if (m.IsOverride)
                rev.IsOverride.Add(m.SymbolId);
        return rev;
    }

    private static IEnumerable<string> Predecessors(string current, GraphIndex index, ReverseMaps rev)
    {
        var callers = rev.Callers;
        var interfacesByType = rev.InterfacesByType;
        var basesByType = rev.BasesByType;
        var isOverride = rev.IsOverride;

        if (callers.TryGetValue(current, out var direct))
            foreach (var c in direct)
                yield return c;

        var parsed = ParseMethod(current);
        if (parsed is null)
            yield break;
        var (typeId, name) = parsed.Value;

        // Reverse impl-dispatch: callers invoke the interface method, which dispatches to this impl.
        // Narrowed: only yield the interface method when SOME caller of it could dispatch to THIS impl
        // type (its receiver is unreliable or names this type / a supertype-in-scope of it). Else the
        // impl can't be the runtime target of any of that interface method's call sites — drop it.
        if (interfacesByType.TryGetValue(typeId, out var ifaces))
            foreach (var iface in ifaces)
                if (index.MethodsByStrippedType.TryGetValue(TypeClosure.StripGeneric(iface), out var im))
                    foreach (var m in im)
                        if (string.Equals(m.Name, name, StringComparison.Ordinal) && ReverseDispatchReaches(m.SymbolId, typeId, index, rev))
                            yield return m.SymbolId;

        // Reverse override-dispatch: a call to a base virtual dispatches to this override, so the
        // base's same-named method reaches it. Gated on this being an override; walk all ancestors.
        // Narrowed the same way: the base method only reverse-reaches this override when some caller's
        // receiver type could resolve to this override's declaring type.
        if (isOverride.Contains(current))
            foreach (var baseType in Ancestors(typeId, basesByType))
                if (index.MethodsByStrippedType.TryGetValue(TypeClosure.StripGeneric(baseType), out var bm))
                    foreach (var m in bm)
                        if (string.Equals(m.Name, name, StringComparison.Ordinal) && ReverseDispatchReaches(m.SymbolId, typeId, index, rev))
                            yield return m.SymbolId;
    }

    // True when the virtual/base/interface method `baseMethod` can dispatch to an override/impl whose
    // declaring type is `overrideTypeId` (a stripped or DocID type) — i.e. when SOME direct caller of
    // `baseMethod` has a receiver in scope of `overrideTypeId`. CHA fallback (always true) when
    // narrowing is off, when any caller's receiver is unreliable, or when `baseMethod` has no recorded
    // callers (e.g. an entry-point virtual reached only by the framework — keep recall).
    private static bool ReverseDispatchReaches(string baseMethod, string overrideTypeId, GraphIndex index, ReverseMaps rev)
    {
        if (!rev.NarrowDispatch)
            return true;
        if (!rev.ReceiverProfileByCallee.TryGetValue(baseMethod, out var profile))
            return true; // no call edges target it (framework-invoked) — don't narrow it away
        if (profile.AnyUnreliable || profile.StrippedReceivers.Count == 0)
            return true;

        var overrideStripped = TypeClosure.StripGeneric(overrideTypeId);
        foreach (var r in profile.StrippedReceivers)
        {
            // A receiver R reaches this override type when R == overrideType, or overrideType is a
            // descendant of R (R is a base typing of the runtime object), or R is a descendant of
            // overrideType (the override lives on a supertype of R's static type — the CLR walks up).
            if (string.Equals(r, overrideStripped, StringComparison.Ordinal))
                return true;
            if (Descendants(r, index).Contains(overrideTypeId) || DescendantsContainStripped(r, overrideStripped, index))
                return true;
            if (Descendants(overrideTypeId, index).Contains(r) || DescendantsContainStripped(overrideTypeId, r, index))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> Ancestors(string typeId, Dictionary<string, List<string>> basesByType)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(typeId);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (basesByType.TryGetValue(t, out var bases))
                foreach (var b in bases)
                    if (seen.Add(b))
                    {
                        stack.Push(b);
                        yield return b;
                    }
        }
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
    }

    private static GraphIndex BuildIndex(FactGraphData graph, bool narrowDispatch = true)
    {
        var index = new GraphIndex { NarrowDispatch = narrowDispatch };
        foreach (var edge in graph.CallEdges)
        {
            if (!index.Adjacency.TryGetValue(edge.Caller, out var list))
                index.Adjacency[edge.Caller] = list = new List<CallEdge>();
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
            .ToDictionary(g => g.Key, g => g.Select(e => e.ImplType).Distinct().ToList(), StringComparer.Ordinal);
        index.ImplsByErrorInterfaceName = graph
            .ImplementsEdges.Where(e => e.InterfaceType.StartsWith("!:", StringComparison.Ordinal))
            .GroupBy(e => SimpleTypeName(e.InterfaceType), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ImplType).Distinct().ToList(), StringComparer.Ordinal);
        index.StrippedBaseEdges = TypeClosure.BuildBaseEdgeLookup(
            (graph.BaseEdges ?? new List<BaseEdge>()).Select(e => (e.SubType, e.BaseType))
        );
        foreach (var method in graph.Methods)
            index.Nodes.Add(method.SymbolId);
        return index;
    }

    // Direct call edges + the interface->concrete DI dispatch hop (single shared definition so
    // Find and Reaches traverse identically). Each dispatch edge carries the FAN-OUT DEGREE of its
    // source method — the total number of dispatch targets the virtual/base method resolves to
    // (impl-dispatch + override-dispatch) — so a `base.M()` that explodes to all N overrides is
    // distinguishable from a single concrete dispatch (degree 1) and from a real call (degree 0).
    // Direct call edges are degree 0. (A1/D3.)
    //
    // Dispatch is resolved EDGE-AWARE: it is computed per direct call edge `current -(R)-> callee`,
    // narrowed by that edge's receiver type R (CallEdge.ReceiverType) when narrowing is on. The
    // dispatch SOURCE attributed to the fanned-out targets is the `callee` (the virtual/base method),
    // matching the prior per-node model where the same fan-out was attributed to the virtual method.
    private static IEnumerable<(
        string Node,
        string Kind,
        string? File,
        int Line,
        string? LoopKind,
        string? LoopDetail,
        int Fanout,
        // The dispatch SOURCE method when this is a fanned-out dispatch edge (the virtual/base/interface
        // method `current` itself, whose call fanned out) — the DispatchVia tag for the reached node.
        // Null for direct call edges. Set even when degree==1 (harmless; fan-out tagging fires only >1).
        string? Via,
        // The static receiver type to carry forward to the TARGET node, so that when the target is later
        // expanded its own dispatch fan-out can be narrowed edge-aware. For a direct call edge this is
        // the edge's ReceiverType (the receiver of `target` at that call site). Null for dispatch hops
        // (a dispatch edge has no further call-site receiver).
        string? OutReceiver
    )> Successors(string current, GraphIndex index, string? incomingReceiver = null)
    {
        // Emit direct call edges in CALL-SITE SOURCE ORDER (by line, then callee for stable ties), not
        // storage order. The graph is loaded from SQL with no ORDER BY, so adjacency order is arbitrary
        // and non-deterministic; C# executes calls eagerly inline, so line order is a good approximation
        // of execution order and makes tree/path/reaches read top-to-bottom and reproduce deterministically.
        // (Approximation only: branches/loops/early-return mean lexical order != runtime order.) Each edge
        // carries its ReceiverType forward so the target's dispatch can be narrowed when it is expanded.
        if (index.Adjacency.TryGetValue(current, out var edges))
            foreach (var edge in edges.OrderBy(e => e.Line).ThenBy(e => e.Callee, StringComparer.Ordinal))
                yield return (edge.Callee, edge.Kind, edge.FilePath, edge.Line, edge.LoopKind, edge.LoopDetail, 0, null, edge.ReceiverType);

        // Dispatch (synthetic, no call-site line) edges AFTER the line-ordered real calls — the fan-out
        // of `current` itself when it is a virtual/base/interface method, narrowed by the receiver of the
        // call that REACHED current (edge-aware). Tagged with the group's fan-out degree (N) and attributed
        // to `current` (the dispatch source). This preserves the prior per-node tree shape and ordering.
        var dispatch = DispatchTargets(current, index, index.NarrowDispatch ? incomingReceiver : null);
        var degree = dispatch.Count;
        foreach (var d in dispatch)
            yield return (d.Node, d.Kind, null, 0, null, null, degree, current, null);
    }

    // All synthetic dispatch successors of `method` (a virtual/base/interface method node):
    // interface->impl (incl. the error-type simple-name recovery) and base-virtual/abstract->override.
    // Materialized (not lazily yielded) so the caller knows the fan-out degree before emitting any
    // edge. Deduped by target so a method reached via two mechanisms isn't double-counted in the degree.
    //
    // `receiverType` is the static receiver type mined at the call site that reached `method` (a display
    // FQN, e.g. "MedDBase.CompanyEntity"). When it resolves to a concrete first-party type that is a
    // descendant of `method`'s declaring type, override/impl dispatch is NARROWED to that receiver's
    // own subtree — the precise CLR dispatch target set. Otherwise (null/interface/error-type/the
    // declaring base itself/an unknown type) it falls back to full CHA so no real target is dropped.
    private static List<(string Node, string Kind)> DispatchTargets(string method, GraphIndex index, string? receiverType = null)
    {
        var targets = new List<(string Node, string Kind)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parsed = ParseMethod(method);
        if (parsed is null)
            return targets;

        // Resolve the receiver to a narrowing subtree, if it is reliable. `narrowRoot` non-null means
        // override-dispatch (and impl-dispatch) restricts to {narrowRoot} ∪ descendants(narrowRoot)
        // instead of all descendants of the declaring type. Null => CHA fallback.
        var narrowRoot = ResolveNarrowRoot(receiverType, parsed.Value.TypeId, index);

        void AddImplMethods(IEnumerable<string> impls)
        {
            foreach (var impl in impls)
            {
                // Receiver narrowing for interface dispatch: when the receiver resolves to a concrete
                // type, only that type (and its subtypes) can be the runtime impl — skip the others.
                if (narrowRoot is not null && !InNarrowSubtree(impl, narrowRoot, index))
                    continue;
                if (!index.MethodsByStrippedType.TryGetValue(TypeClosure.StripGeneric(impl), out var implMethods))
                    continue;
                foreach (var concrete in implMethods)
                    if (string.Equals(concrete.Name, parsed.Value.Name, StringComparison.Ordinal) && seen.Add(concrete.SymbolId))
                        targets.Add((concrete.SymbolId, "impl-dispatch"));
            }
        }

        // Interface -> concrete DI dispatch.
        if (index.ImplsByInterface.TryGetValue(parsed.Value.TypeId, out var impls))
            AddImplMethods(impls);

        // Simple-name fallback: recover dispatch to implementers whose interface edge failed to
        // resolve (!:IFoo) by matching the call's interface simple name. Method-name-gated; only
        // consults error-type edges, so the blast radius is the partial-binding cases that would
        // otherwise be invisible. (May slightly over-dispatch across same-named interfaces in
        // different namespaces — a deliberate recall-over-precision trade for broken bindings.)
        if (index.ImplsByErrorInterfaceName.TryGetValue(SimpleTypeName(parsed.Value.TypeId), out var nameImpls))
            AddImplMethods(nameImpls);

        // Base-virtual/abstract -> override dispatch (G6/G3): a call resolved to a base-type method
        // also reaches the SAME-named OVERRIDE on every (transitive) subtype. This is what makes an
        // abstract [ClientAction] (or framework virtual like OnSave) reach the effects in its concrete
        // override. Gated on IsOverride so it doesn't dispatch to unrelated same-named (hidden) methods.
        // When the receiver narrows, scan only the receiver's subtree (its own override + its subtypes')
        // instead of every descendant of the declaring base — the headline 114-way fan-out collapse.
        var subtree = narrowRoot is not null ? NarrowSubtree(narrowRoot, index) : Descendants(parsed.Value.TypeId, index);
        foreach (var sub in subtree)
        {
            if (!index.MethodsByStrippedType.TryGetValue(TypeClosure.StripGeneric(sub), out var subMethods))
                continue;
            foreach (var m in subMethods)
                if (m.IsOverride && string.Equals(m.Name, parsed.Value.Name, StringComparison.Ordinal) && seen.Add(m.SymbolId))
                    targets.Add((m.SymbolId, "override-dispatch"));
        }

        return targets;
    }

    // Resolves a call-site receiver type (a display FQN, e.g. "MedDBase.CompanyEntity"/"Ns.Foo<T>")
    // to the stripped type DocID to narrow override/impl dispatch to, or null to fall back to CHA.
    // Narrows ONLY when the receiver is a reliable concrete first-party type that is a STRICT
    // descendant of the declaring type `declaringTypeId` (so it is one of the real dispatch targets):
    //   * null/empty receiver, error-type, or interface  -> CHA (caller can't be pinned down),
    //   * the declaring base type itself                  -> CHA (no narrowing possible — the call
    //                                                         could hit any override),
    //   * a type with no methods in the index (unknown / not first-party) -> CHA (don't drop targets).
    // (Mirrors the existing recall-over-precision stance for broken bindings.)
    private static string? ResolveNarrowRoot(string? receiverType, string declaringTypeId, GraphIndex index)
    {
        if (string.IsNullOrEmpty(receiverType))
            return null;

        var stripped = ReceiverToStrippedTypeId(receiverType!);
        if (stripped is null)
            return null;

        var declaringStripped = TypeClosure.StripGeneric(declaringTypeId);

        // Receiver IS the declaring base type (or its stripped form): no narrowing — a `base.M()` or a
        // call typed as the base could dispatch to any override, so keep CHA.
        if (string.Equals(stripped, declaringStripped, StringComparison.Ordinal))
            return null;

        // The receiver must be a real CHA dispatch target of `declaringTypeId` to narrow to it:
        //  * a (transitive) base-edge descendant — the base-virtual/override case, OR
        //  * an implementer of the declaring INTERFACE (or a subtype of one) — the impl-dispatch case.
        // Being a descendant/implementer also confirms the receiver is a KNOWN FIRST-PARTY type (it
        // appears in the indexed type hierarchy). If it is neither, the binding is suspect (cross-
        // hierarchy simple-name collision, partial binding, an external type, etc.) — CHA is the safe
        // choice so no real override is dropped. (Note: we do NOT require the receiver type itself to
        // have methods in the closure — the bounded graph only loads methods inside the reach set, so a
        // pure intermediate base like ReferralEntityBase legitimately has none; its concrete override on
        // ReferralEntity is what matters and is found by scanning the narrowed subtree below.)
        var isBaseDescendant =
            Descendants(declaringTypeId, index).Contains(stripped) || DescendantsContainStripped(declaringTypeId, stripped, index);
        if (!isBaseDescendant && !ImplementsInterface(stripped, declaringTypeId, index))
            return null;

        return stripped;
    }

    // True when type `strippedType` implements the interface `interfaceTypeId` directly, or a base-edge
    // ancestor of it does (so a subtype of a declared implementer still narrows to the interface).
    private static bool ImplementsInterface(string strippedType, string interfaceTypeId, GraphIndex index)
    {
        if (!index.ImplsByInterface.TryGetValue(interfaceTypeId, out var impls))
            return false;
        foreach (var impl in impls)
        {
            var implStripped = TypeClosure.StripGeneric(impl);
            if (string.Equals(implStripped, strippedType, StringComparison.Ordinal))
                return true;
            // The receiver may be a subtype of a declared implementer.
            if (Descendants(impl, index).Contains(strippedType) || DescendantsContainStripped(impl, strippedType, index))
                return true;
        }
        return false;
    }

    private static bool DescendantsContainStripped(string declaringTypeId, string strippedReceiver, GraphIndex index)
    {
        foreach (var d in Descendants(declaringTypeId, index))
            if (string.Equals(TypeClosure.StripGeneric(d), strippedReceiver, StringComparison.Ordinal))
                return true;
        return false;
    }

    // The narrowed dispatch subtree for a concrete receiver root: the root itself PLUS its transitive
    // descendants (an override may live on the receiver's own type or any subtype of it).
    private static HashSet<string> NarrowSubtree(string narrowRoot, GraphIndex index)
    {
        var set = new HashSet<string>(Descendants(narrowRoot, index), StringComparer.Ordinal) { narrowRoot };
        return set;
    }

    private static bool InNarrowSubtree(string typeId, string narrowRoot, GraphIndex index)
    {
        var stripped = TypeClosure.StripGeneric(typeId);
        if (string.Equals(stripped, narrowRoot, StringComparison.Ordinal))
            return true;
        foreach (var d in Descendants(narrowRoot, index))
            if (string.Equals(TypeClosure.StripGeneric(d), stripped, StringComparison.Ordinal))
                return true;
        return false;
    }

    // A receiver display FQN ("MedDBase.CompanyEntity", "Ns.Foo<T>") -> the generic-stripped type
    // DocID form ("T:MedDBase.CompanyEntity", "T:Ns.Foo") used as the MethodsByStrippedType / base-edge
    // key. Returns null for an error/unresolved receiver (a non-type display, e.g. one carrying "?"
    // or "<error>") so those degrade to CHA. Interfaces are NOT distinguishable here by name alone, so
    // an interface receiver that happens to resolve to a known type with no descendants simply narrows
    // to nothing extra (its own impls are found by the impl-dispatch path); the ResolveNarrowRoot
    // descendant check keeps a non-descendant interface receiver on the CHA path.
    private static string? ReceiverToStrippedTypeId(string receiver)
    {
        // Reject shapes that aren't first-party named dispatch types: anonymous/error types (leading
        // '<'), nullable/pointer/tuple/by-ref shapes ('?' '*' ' '), and arrays ('['). These degrade to
        // CHA via a null narrow-root.
        if (
            receiver.Length == 0
            || receiver[0] == '<'
            || receiver.IndexOf('?') >= 0
            || receiver.IndexOf('*') >= 0
            || receiver.IndexOf('[') >= 0
            || receiver.IndexOf(' ') >= 0
        )
            return null;

        // Drop a generic argument list ("Foo<A,B>") down to the bare name, then strip the `n arity
        // form too; the MethodsByStrippedType key is the generic-stripped DocID.
        var angle = receiver.IndexOf('<');
        if (angle > 0)
            receiver = receiver.Substring(0, angle);
        receiver = TypeClosure.StripGeneric(receiver);
        return receiver.Length == 0 ? null : "T:" + receiver;
    }

    // Materialises EVERY synthetic dispatch edge in the graph — (sourceMethod -> targetMethod, kind)
    // for interface->impl (incl. error-type simple-name recovery) and base-virtual/abstract->override,
    // for all method nodes, in FULL CHA (receiver-blind). This feeds the precomputed `dispatch_edges`
    // table, the SOUND SUPERSET that bounds the SQL reachability load — narrowing happens only in the
    // in-memory edge traversal (Successors), so dispatch_edges must stay receiver-blind so the bounded
    // subgraph it produces still contains every edge a narrowed traversal could visit. Deduped per
    // source; sources are distinct.
    public static IEnumerable<(string From, string To, string Kind)> AllDispatchEdges(FactGraphData graph)
    {
        var index = BuildIndex(graph, narrowDispatch: false);
        foreach (var node in index.Nodes)
        foreach (var target in DispatchTargets(node, index, receiverType: null))
            yield return (node, target.Node, target.Kind);
    }

    // The direct call edges as (caller -> callee, kind), deduped — the other half of the graph the
    // SQL reachability path traverses. Mirrors graph.CallEdges (already filtered to first-party
    // invocation/methodGroup/ctor at load), exposed here so the materialiser and the oracle agree.
    public static IEnumerable<(
        string From,
        string To,
        string Kind,
        string? File,
        int Line,
        string? LoopKind,
        string? LoopDetail,
        string? ReceiverType
    )> AllCallEdges(FactGraphData graph)
    {
        foreach (var edge in graph.CallEdges)
            yield return (edge.Caller, edge.Callee, edge.Kind, edge.FilePath, edge.Line, edge.LoopKind, edge.LoopDetail, edge.ReceiverType);
    }

    // Transitive strict descendants of a type, memoised. Keyed on the generic-stripped id so the
    // instantiated/open-generic forms share one cache entry.
    private static HashSet<string> Descendants(string typeId, GraphIndex index)
    {
        var key = TypeClosure.StripGeneric(typeId);
        if (!index.DescendantsCache.TryGetValue(key, out var set))
            index.DescendantsCache[key] = set = TypeClosure.ComputeStrictDescendants(index.StrippedBaseEdges, new[] { typeId });
        return set;
    }

    private static void Enqueue(
        Dictionary<string, (string From, string Kind, string? File, int Line, string? LoopKind, string? LoopDetail, int Fanout)?> parent,
        Queue<(string, int)> queue,
        string node,
        string from,
        string kind,
        string? file,
        int line,
        string? loopKind,
        string? loopDetail,
        int fanout,
        int depth
    )
    {
        if (parent.ContainsKey(node))
            return;
        parent[node] = (from, kind, file, line, loopKind, loopDetail, fanout);
        queue.Enqueue((node, depth + 1));
    }

    private static IReadOnlyList<PathStep> Reconstruct(
        Dictionary<string, (string From, string Kind, string? File, int Line, string? LoopKind, string? LoopDetail, int Fanout)?> parent,
        string target
    )
    {
        var steps = new List<PathStep>();
        var node = target;
        while (true)
        {
            var link = parent[node];
            steps.Add(
                new PathStep(node, link?.Kind ?? "entry", link?.File, link?.Line ?? 0, link?.LoopKind, link?.LoopDetail, link?.Fanout ?? 0)
            );
            if (link is null)
                break;
            node = link.Value.From;
        }
        steps.Reverse();
        return steps;
    }

    // "M:Ns.Type.Member(args)" -> ("T:Ns.Type", "Member"). Null when not a method DocID.
    private static (string TypeId, string Name)? ParseMethod(string docId)
    {
        if (!docId.StartsWith("M:", StringComparison.Ordinal))
            return null;
        var body = docId.Substring(2);
        var paren = body.IndexOf('(');
        if (paren >= 0)
            body = body.Substring(0, paren);
        var lastDot = body.LastIndexOf('.');
        if (lastDot < 0)
            return null;
        return ("T:" + body.Substring(0, lastDot), body.Substring(lastDot + 1));
    }

    // Simple (un-namespaced, arity-stripped) name from a type DocID:
    // "T:Ns.IFoo`1" / "!:IFoo" -> "IFoo".
    private static string SimpleTypeName(string typeId)
    {
        var s = typeId;
        if (s.Length >= 2 && s[1] == ':')
            s = s.Substring(2);
        var lastDot = s.LastIndexOf('.');
        if (lastDot >= 0)
            s = s.Substring(lastDot + 1);
        var tick = s.IndexOf('`');
        if (tick >= 0)
            s = s.Substring(0, tick);
        return s;
    }

    private static bool Contains(string value, string pattern) => value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
}

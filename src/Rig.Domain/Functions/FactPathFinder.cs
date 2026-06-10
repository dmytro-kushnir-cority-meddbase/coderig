using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Stage-2-over-facts path finding: BFS the fact-derived call graph from any symbol matching
// `fromPattern` to any symbol matching `toPattern`, cross-project, with no entry-point anchoring.
// Includes the interface->concrete DI hop (the single-impl dispatch from Q5) reconstructed from
// type-relation facts + DocID member-name matching — no Roslyn, no SemanticModel.
// (Rig.Domain targets netstandard2.0, so this avoids TryAdd / ranges / Contains(string,cmp).)
public static class FactPathFinder
{
    public static IReadOnlyList<PathStep>? Find(
        FactGraphData graph, string fromPattern, string toPattern, int maxDepth = 20)
    {
        var index = BuildIndex(graph);

        // Parent links carry the edge that reached the node (for path + kind reconstruction),
        // including its enclosing-loop context so the reconstructed path can mark looped hops, and
        // the dispatch fan-out degree so a path that traverses a base-virtual fan-out shows it.
        var parent = new Dictionary<string, (string From, string Kind, string? File, int Line, string? LoopKind, string? LoopDetail, int Fanout)?>(StringComparer.Ordinal);
        var queue = new Queue<(string Node, int Depth)>();
        foreach (var start in index.Nodes.Where(n => Contains(n, fromPattern)))
        {
            if (parent.ContainsKey(start))
                continue;
            parent[start] = null;
            queue.Enqueue((start, 0));
        }

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            if (parent[current] is not null && Contains(current, toPattern))
                return Reconstruct(parent, current);

            if (depth >= maxDepth)
                continue;

            foreach (var s in Successors(current, index))
                Enqueue(parent, queue, s.Node, current, s.Kind, s.File, s.Line, s.LoopKind, s.LoopDetail, s.Fanout, depth);
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
        int Depth, int LoopNesting, string? NearestLoopKind, string? NearestLoopDetail,
        string? DispatchVia = null, int DispatchDegree = 0);

    // Full reachability: BFS the call graph (incl. interface->impl dispatch) from every node
    // matching `fromPattern`, returning each reachable method DocID with its shortest depth.
    // Same traversal as Find — so "what does this entry point reach" is consistent with `rig path`.
    public static IReadOnlyDictionary<string, int> Reaches(
        FactGraphData graph, string fromPattern, int maxDepth = 20, int maxNodes = 20000)
    {
        var info = ReachesWithFanout(graph, fromPattern, maxDepth, maxNodes);
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
        FactGraphData graph, string fromPattern, int maxDepth = 20, int maxNodes = 20000)
    {
        var index = BuildIndex(graph);
        var info = new Dictionary<string, ReachInfo>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var start in index.Nodes.Where(n => Contains(n, fromPattern)))
        {
            if (info.ContainsKey(start))
                continue;
            info[start] = new ReachInfo(0, 0, null, null);
            queue.Enqueue(start);
        }

        while (queue.Count > 0 && info.Count < maxNodes)
        {
            var current = queue.Dequeue();
            var cur = info[current];
            if (cur.Depth >= maxDepth)
                continue;
            foreach (var s in Successors(current, index))
            {
                if (info.ContainsKey(s.Node))
                    continue;
                var looped = s.LoopKind is not null;
                var nesting = cur.LoopNesting + (looped ? 1 : 0);
                var nearKind = looped ? s.LoopKind : cur.NearestLoopKind;
                var nearDetail = looped ? s.LoopDetail : cur.NearestLoopDetail;
                // Dispatch fan-out (A1/D3): when the reaching edge fanned `current` out to >1 targets,
                // this node is reached via that fan-out, not a real call — tag it with the source
                // (current) and degree. A single-target dispatch (degree 1) is deterministic, so it's
                // treated like a real call. Otherwise inherit the tag, so the whole fanned-out subtree
                // (BFS-shortest) carries it — unless reached more directly elsewhere.
                var fannedOut = s.Fanout > 1;
                var via = fannedOut ? current : cur.DispatchVia;
                var degree = fannedOut ? s.Fanout : cur.DispatchDegree;
                info[s.Node] = new ReachInfo(cur.Depth + 1, nesting, nearKind, nearDetail, via, degree);
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
    public static IReadOnlyList<TraceNode> BuildTree(
        FactGraphData graph, string fromPattern, int maxDepth = 20, int maxNodes = 20000)
    {
        var index = BuildIndex(graph);
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var budget = new int[] { maxNodes };

        var roots = new List<TraceNode>();
        foreach (var root in index.Nodes.Where(n => Contains(n, fromPattern)).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (budget[0] <= 0)
                break;
            roots.Add(BuildNode(root, "entry", loopKind: null, loopDetail: null, fanout: 0, depth: 0, maxDepth, index, expanded, budget));
        }
        return roots;
    }

    private static TraceNode BuildNode(
        string symbol, string edgeKind, string? loopKind, string? loopDetail, int fanout,
        int depth, int maxDepth, GraphIndex index, HashSet<string> expanded, int[] budget)
    {
        budget[0]--;

        // Already expanded elsewhere (cycle / shared callee), at the depth cap, or out of budget:
        // emit a leaf and don't descend, so the tree stays finite and each method's subtree is
        // printed once.
        if (expanded.Contains(symbol) || depth >= maxDepth || budget[0] <= 0)
            return new TraceNode(symbol, edgeKind, loopKind, loopDetail, EmptyNodes, Truncated: true, Fanout: fanout);

        expanded.Add(symbol);

        var children = new List<TraceNode>();
        foreach (var s in Successors(symbol, index))
        {
            if (budget[0] <= 0)
                break;
            children.Add(BuildNode(s.Node, s.Kind, s.LoopKind, s.LoopDetail, s.Fanout, depth + 1, maxDepth, index, expanded, budget));
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
    public static HashSet<string> ReachableFromAll(
        FactGraphData graph, IEnumerable<string> roots, int maxNodes = 2_000_000)
    {
        var index = BuildIndex(graph);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var root in roots)
            if (index.Nodes.Contains(root) && seen.Add(root))
                queue.Enqueue(root);

        while (queue.Count > 0 && seen.Count < maxNodes)
        {
            var current = queue.Dequeue();
            foreach (var s in Successors(current, index))
                if (seen.Add(s.Node))
                    queue.Enqueue(s.Node);
        }

        return seen;
    }

    // Reverse reachability — every method that can REACH any node matching toPattern (transitive
    // callers), keyed to its shortest reverse hop count. Inverts Successors: direct caller edges,
    // plus the reverse of the dispatch hops — an impl method is reached via its interface's
    // same-named method, an override via its base's. Powers `rig callers` ("which entry points
    // touch this method"), and underpins the planned unreachable-symbol (dead-code) finder.
    public static IReadOnlyDictionary<string, int> ReachedBy(
        FactGraphData graph, string toPattern, int maxDepth = 20, int maxNodes = 20000)
    {
        var index = BuildIndex(graph);
        var rev = BuildReverseMaps(graph);

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
    public static IReadOnlyList<string> EntryRootsReaching(
        FactGraphData graph, string toPattern, int maxDepth = 20, int maxNodes = 20000)
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
    }

    private static ReverseMaps BuildReverseMaps(FactGraphData graph)
    {
        var rev = new ReverseMaps();
        foreach (var edge in graph.CallEdges)
        {
            if (!rev.Callers.TryGetValue(edge.Callee, out var list))
                rev.Callers[edge.Callee] = list = new List<string>();
            list.Add(edge.Caller);
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
        if (interfacesByType.TryGetValue(typeId, out var ifaces))
            foreach (var iface in ifaces)
                if (index.MethodsByStrippedType.TryGetValue(TypeClosure.StripGeneric(iface), out var im))
                    foreach (var m in im)
                        if (string.Equals(m.Name, name, StringComparison.Ordinal))
                            yield return m.SymbolId;

        // Reverse override-dispatch: a call to a base virtual dispatches to this override, so the
        // base's same-named method reaches it. Gated on this being an override; walk all ancestors.
        if (isOverride.Contains(current))
            foreach (var baseType in Ancestors(typeId, basesByType))
                if (index.MethodsByStrippedType.TryGetValue(TypeClosure.StripGeneric(baseType), out var bm))
                    foreach (var m in bm)
                        if (string.Equals(m.Name, name, StringComparison.Ordinal))
                            yield return m.SymbolId;
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
    }

    private static GraphIndex BuildIndex(FactGraphData graph)
    {
        var index = new GraphIndex();
        foreach (var edge in graph.CallEdges)
        {
            if (!index.Adjacency.TryGetValue(edge.Caller, out var list))
                index.Adjacency[edge.Caller] = list = new List<CallEdge>();
            list.Add(edge);
            index.Nodes.Add(edge.Caller);
            index.Nodes.Add(edge.Callee);
        }
        index.MethodsByType = graph.Methods
            .Where(m => m.ContainingTypeId is not null)
            .GroupBy(m => m.ContainingTypeId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        index.MethodsByStrippedType = graph.Methods
            .Where(m => m.ContainingTypeId is not null)
            .GroupBy(m => TypeClosure.StripGeneric(m.ContainingTypeId!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        index.ImplsByInterface = graph.ImplementsEdges
            .GroupBy(e => e.InterfaceType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ImplType).Distinct().ToList(), StringComparer.Ordinal);
        index.ImplsByErrorInterfaceName = graph.ImplementsEdges
            .Where(e => e.InterfaceType.StartsWith("!:", StringComparison.Ordinal))
            .GroupBy(e => SimpleTypeName(e.InterfaceType), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ImplType).Distinct().ToList(), StringComparer.Ordinal);
        index.StrippedBaseEdges = TypeClosure.BuildBaseEdgeLookup(
            (graph.BaseEdges ?? new List<BaseEdge>()).Select(e => (e.SubType, e.BaseType)));
        foreach (var method in graph.Methods)
            index.Nodes.Add(method.SymbolId);
        return index;
    }

    // Direct call edges + the interface->concrete DI dispatch hop (single shared definition so
    // Find and Reaches traverse identically). Each dispatch edge carries the FAN-OUT DEGREE of its
    // source method — the total number of dispatch targets `current` resolves to (impl-dispatch +
    // override-dispatch) — so a `base.M()` that explodes to all N overrides is distinguishable from a
    // single concrete dispatch (degree 1) and from a real call (degree 0). Direct call edges are
    // degree 0. (A1/D3.)
    private static IEnumerable<(string Node, string Kind, string? File, int Line, string? LoopKind, string? LoopDetail, int Fanout)> Successors(string current, GraphIndex index)
    {
        if (index.Adjacency.TryGetValue(current, out var edges))
            foreach (var edge in edges)
                yield return (edge.Callee, edge.Kind, edge.FilePath, edge.Line, edge.LoopKind, edge.LoopDetail, 0);

        // Dispatch targets are collected first so each can be tagged with the group's degree (N).
        var dispatch = DispatchTargets(current, index);
        var degree = dispatch.Count;
        foreach (var d in dispatch)
            yield return (d.Node, d.Kind, null, 0, null, null, degree);
    }

    // All synthetic dispatch successors of `current`: interface->impl (incl. the error-type simple-
    // name recovery) and base-virtual/abstract->override. Materialized (not lazily yielded) so the
    // caller knows the fan-out degree before emitting any edge. Deduped by target so a method reached
    // via two mechanisms isn't double-counted in the degree.
    private static List<(string Node, string Kind)> DispatchTargets(string current, GraphIndex index)
    {
        var targets = new List<(string Node, string Kind)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parsed = ParseMethod(current);
        if (parsed is null)
            return targets;

        void AddImplMethods(IEnumerable<string> impls)
        {
            foreach (var impl in impls)
            {
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
        foreach (var sub in Descendants(parsed.Value.TypeId, index))
        {
            if (!index.MethodsByStrippedType.TryGetValue(TypeClosure.StripGeneric(sub), out var subMethods))
                continue;
            foreach (var m in subMethods)
                if (m.IsOverride && string.Equals(m.Name, parsed.Value.Name, StringComparison.Ordinal) && seen.Add(m.SymbolId))
                    targets.Add((m.SymbolId, "override-dispatch"));
        }

        return targets;
    }

    // Materialises EVERY synthetic dispatch edge in the graph — (sourceMethod -> targetMethod, kind)
    // for interface->impl (incl. error-type simple-name recovery) and base-virtual/abstract->override,
    // for all method nodes. This is the SAME per-node DispatchTargets the lazy traversal uses, just
    // enumerated over the whole node set, so a precomputed `dispatch_edges` table built from this is
    // identical to what Successors computes on the fly — the SQL reachability path traverses the same
    // edges as this in-memory oracle. Deduped per source by DispatchTargets; sources are distinct.
    public static IEnumerable<(string From, string To, string Kind)> AllDispatchEdges(FactGraphData graph)
    {
        var index = BuildIndex(graph);
        foreach (var node in index.Nodes)
            foreach (var target in DispatchTargets(node, index))
                yield return (node, target.Node, target.Kind);
    }

    // The direct call edges as (caller -> callee, kind), deduped — the other half of the graph the
    // SQL reachability path traverses. Mirrors graph.CallEdges (already filtered to first-party
    // invocation/methodGroup/ctor at load), exposed here so the materialiser and the oracle agree.
    public static IEnumerable<(string From, string To, string Kind, string? File, int Line, string? LoopKind, string? LoopDetail)> AllCallEdges(FactGraphData graph)
    {
        foreach (var edge in graph.CallEdges)
            yield return (edge.Caller, edge.Callee, edge.Kind, edge.FilePath, edge.Line, edge.LoopKind, edge.LoopDetail);
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
        string node, string from, string kind, string? file, int line, string? loopKind, string? loopDetail, int fanout, int depth)
    {
        if (parent.ContainsKey(node))
            return;
        parent[node] = (from, kind, file, line, loopKind, loopDetail, fanout);
        queue.Enqueue((node, depth + 1));
    }

    private static IReadOnlyList<PathStep> Reconstruct(
        Dictionary<string, (string From, string Kind, string? File, int Line, string? LoopKind, string? LoopDetail, int Fanout)?> parent, string target)
    {
        var steps = new List<PathStep>();
        var node = target;
        while (true)
        {
            var link = parent[node];
            steps.Add(new PathStep(node, link?.Kind ?? "entry", link?.File, link?.Line ?? 0, link?.LoopKind, link?.LoopDetail, link?.Fanout ?? 0));
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

    private static bool Contains(string value, string pattern) =>
        value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
}

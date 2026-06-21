using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Stage-2-over-facts path finding: BFS the fact-derived call graph from any symbol matching
// `fromPattern` to any symbol matching `toPattern`, cross-project, with no entry-point anchoring.
// Includes the interface->concrete DI hop (the single-impl dispatch from Q5) reconstructed from
// type-relation facts + DocID member-name matching — no Roslyn, no SemanticModel.
// (Rig.Domain targets netstandard2.0, so this avoids TryAdd / ranges / Contains(string,cmp).)
//
// Dispatch is resolved EXACT-FIRST: the member-level interface→impl / base→override correspondence
// comes from the Roslyn-MINED dispatch facts when present (FactGraphData.MinedDispatch, Basis=
// "roslyn" — signature-exact, generic-correct), with the name/arity CHA scan kept only as a FLAGGED
// fallback (Basis="heuristic") for members Roslyn couldn't bind (`!:` error-typed interfaces, unmined
// stores). And EDGE-AWARE (receiver-type narrowing): the in-memory traversal narrows a
// virtual/base/interface call to the STATIC RECEIVER TYPE mined onto the call edge (CallEdge.
// ReceiverType) — `company.Save()` reaches CompanyEntity.Save (+ Company subtypes), not all 114
// CommonEntityBase.Save overrides. It falls back to the full receiver-blind set whenever the receiver
// is unreliable (null/interface/error-type/the declaring base/not a known first-party type), so no
// real target is ever dropped. The precomputed dispatch_edges table and AllDispatchEdges stay
// receiver-blind (the sound superset that bounds the SQL load); narrowing lives ONLY in the
// in-memory edge traversal.
public static partial class FactPathFinder
{
    // How traversal treats async HANDOFF edges (Kind=="handoff" — a delegate handed to a dispatcher
    // to run later / on another thread). SyncCut (the default everywhere) skips them, so a timer/
    // background registration does NOT look like it executes its callback synchronously. AsyncInclude
    // walks them, tagging the reached subtree with HandoffVia provenance (cloned from the DispatchVia
    // machinery) so `--async` can show the scheduled reach distinctly. sync ⊆ async by construction.
    public enum TraversalMode
    {
        SyncCut,
        AsyncInclude,
    }

    public static IReadOnlyList<PathStep>? Find(
        FactGraphData graph,
        string fromPattern,
        string toPattern,
        int maxDepth = 20,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph);

        // Parent links carry the edge that reached the node (for path + kind reconstruction),
        // including its enclosing-loop context so the reconstructed path can mark looped hops, the
        // dispatch fan-out degree so a path that traverses a base-virtual fan-out shows it, the
        // handoff-dispatcher provenance so an --async path can render the cross-thread hop, and the
        // dispatch BASIS (roslyn-mined vs name/arity heuristic) so inferred hops are flagged.
        var parent = new Dictionary<
            string,
            (
                string From,
                string Kind,
                string? File,
                int Line,
                string? LoopKind,
                string? LoopDetail,
                int Fanout,
                string? HandoffVia,
                string? Basis
            )?
        >(StringComparer.Ordinal);
        var queue = new Queue<(string Node, int Depth)>();
        // Receiver of the edge that reached each node — narrows that node's dispatch when expanded.
        var receiverOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var start in index.Nodes.Where(n => Contains(value: n, pattern: fromPattern)))
        {
            if (parent.ContainsKey(start))
            {
                continue;
            }

            parent[start] = null;
            receiverOf[start] = null;
            queue.Enqueue((start, 0));
        }

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            if (parent[current] is not null && Contains(value: current, pattern: toPattern))
            {
                return Reconstruct(parent, current);
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (
                var s in Successors(
                    current: current,
                    index: index,
                    incomingReceiver: receiverOf.TryGetValue(key: current, value: out var rc) ? rc : null,
                    incomingBinding: null,
                    mode: mode,
                    fromDispatch: parent.TryGetValue(current, out var pe) && pe is { } p && IsDispatchEdgeKind(p.Kind)
                )
            )
            {
                if (!parent.ContainsKey(s.Node))
                {
                    receiverOf[s.Node] = s.OutReceiver;
                }

                Enqueue(
                    parent,
                    queue,
                    node: s.Node,
                    from: current,
                    kind: s.Kind,
                    file: s.File,
                    line: s.Line,
                    loopKind: s.LoopKind,
                    loopDetail: s.LoopDetail,
                    fanout: s.Fanout,
                    handoffVia: s.HandoffVia,
                    basis: s.Basis,
                    depth: depth
                );
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
    // HandoffVia (clone of DispatchVia): under AsyncInclude, when the BFS-shortest path to this node
    // crossed an async handoff edge, HandoffVia = that edge's dispatcher id (e.g. the rule that
    // matched RepeatingBackgroundProcessSchedule). Inherited forward through the scheduled subtree
    // (like NearestLoop/DispatchVia), and null when the node is also reachable synchronously (the
    // shorter sync route reaches it first, with no handoff ancestor). Always null under SyncCut.
    // DispatchBasis: provenance of the dispatch hops on the BFS-shortest path to this node —
    // "heuristic" (STICKY: at least one name/arity-guessed dispatch hop on the path; the reach is
    // only as trustworthy as that guess), "roslyn" (dispatch crossed, all hops exact mined facts),
    // or null (no dispatch hop on the path). Inherited forward like NearestLoop/HandoffVia.
    public sealed record ReachInfo(
        int Depth,
        int LoopNesting,
        string? NearestLoopKind,
        string? NearestLoopDetail,
        string? DispatchVia = null,
        int DispatchDegree = 0,
        string? HandoffVia = null,
        string? DispatchBasis = null
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
        bool narrowDispatch = true,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var info = ReachesWithFanout(graph, fromPattern, maxDepth, maxNodes, narrowDispatch, mode);
        var depthOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in info)
        {
            depthOf[kv.Key] = kv.Value.Depth;
        }

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
        bool narrowDispatch = true,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph, narrowDispatch);
        return ReachesWithFanoutCore(index, index.Nodes.Where(n => Contains(value: n, pattern: fromPattern)), maxDepth, maxNodes, mode);
    }

    // Exact-id, one-hop forward reach from EACH seed independently, returning one reachable-node set per
    // seed (same dispatch semantics as ReachesWithFanout — narrowDispatch one-hop — but seeded by EXACT
    // symbol id, not a substring pattern, so `EditLive.Save` does NOT also seed `EditLive.SaveFinal`).
    // The index is built ONCE and shared across a parallel per-seed loop (read-only traversal over an
    // immutable graph; the lone mutable cache is concurrent + idempotent). This is the engine behind
    // `rig impact`'s per-EP behavioral attribution: forward-reach hundreds of EPs over one loaded graph.
    public static IReadOnlyList<HashSet<string>> ReachesFromEachSeed(
        FactGraphData graph,
        IReadOnlyList<string> seedIds,
        int maxDepth = 20,
        int maxNodes = 20000,
        bool narrowDispatch = true,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph, narrowDispatch);
        var results = new HashSet<string>[seedIds.Count];
        Parallel.For(
            fromInclusive: 0,
            toExclusive: seedIds.Count,
            body: i =>
            {
                var seed = seedIds[i];
                var seeds = index.Nodes.Contains(seed) ? new[] { seed } : Array.Empty<string>();
                var info = ReachesWithFanoutCore(index, seeds, maxDepth, maxNodes, mode);
                results[i] = new HashSet<string>(info.Keys, StringComparer.Ordinal);
            }
        );
        return results;
    }

    // Exact-id forward reach from EACH seed independently, returning the FULL per-node ReachInfo per seed
    // (NOT just the reachable-node set, as ReachesFromEachSeed does). Same index/dispatch semantics; the
    // extra payload is the loop context (NearestLoopKind) and depth/dispatch tags already computed by the
    // BFS — `rig impact`'s effect-AMPLIFICATION pass needs the loop flag per reachable effect-bearing node,
    // which the set-only twin discards. Built once, run in parallel over the shared read-only index.
    public static IReadOnlyList<IReadOnlyDictionary<string, ReachInfo>> ReachesInfoFromEachSeed(
        FactGraphData graph,
        IReadOnlyList<string> seedIds,
        int maxDepth = 20,
        int maxNodes = 20000,
        bool narrowDispatch = true,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph, narrowDispatch);
        var results = new IReadOnlyDictionary<string, ReachInfo>[seedIds.Count];
        Parallel.For(
            fromInclusive: 0,
            toExclusive: seedIds.Count,
            body: i =>
            {
                var seed = seedIds[i];
                var seeds = index.Nodes.Contains(seed) ? new[] { seed } : Array.Empty<string>();
                results[i] = ReachesWithFanoutCore(index, seeds, maxDepth, maxNodes, mode);
            }
        );
        return results;
    }

    // The shared BFS body of ReachesWithFanout / ReachesFromEachSeed: one-hop dispatch forward reach over a
    // PREBUILT index from the given seed nodes. All traversal state below is LOCAL — safe to run concurrently
    // over one shared (read-only) index (DescendantsCache is concurrent).
    private static IReadOnlyDictionary<string, ReachInfo> ReachesWithFanoutCore(
        GraphIndex index,
        IEnumerable<string> seeds,
        int maxDepth,
        int maxNodes,
        TraversalMode mode
    )
    {
        var info = new Dictionary<string, ReachInfo>(StringComparer.Ordinal);
        // The static receiver type of the (BFS-shortest) edge that reached each node, carried so that
        // node's own dispatch fan-out can be narrowed edge-aware when it is expanded.
        var receiverOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        // Generic-dispatch narrowing in the CLOSURE: the concrete type-arg binding accumulated at each
        // node. Unlike receiverOf (BFS-first-wins), this is UNIONED across every path that reaches a node
        // and the node is re-enqueued when its binding GROWS — so a shared generic hub (e.g. Construct`2.
        // New reached via several entity caches) ends up narrowed to ALL really-reachable constructors,
        // never just the first path's (which would unsoundly drop the others). Monotone (sets only grow,
        // capped) so it reaches a fixpoint. Narrowing is recall-safe in DispatchTargets: an empty/unmatched
        // binding leaves the full CHA set, so a hub reached without a matching type arg is never emptied.
        var bindingOf = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
        // Whether each node was first reached via a dispatch edge — gates one-hop dispatch (no re-dispatch
        // of a resolved concrete target). BFS-first-reach wins, matching the DispatchVia tagging above.
        var viaDispatchOf = new Dictionary<string, bool>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var start in seeds)
        {
            if (info.ContainsKey(start))
            {
                continue;
            }

            info[start] = new ReachInfo(Depth: 0, LoopNesting: 0, NearestLoopKind: null, NearestLoopDetail: null);
            receiverOf[start] = null;
            bindingOf[start] = null;
            viaDispatchOf[start] = false;
            queue.Enqueue(start);
        }

        while (queue.Count > 0 && info.Count < maxNodes)
        {
            var current = queue.Dequeue();
            var cur = info[current];
            if (cur.Depth >= maxDepth)
            {
                continue;
            }

            foreach (
                var s in Successors(
                    current: current,
                    index: index,
                    incomingReceiver: receiverOf[current],
                    incomingBinding: bindingOf.TryGetValue(current, out var curBinding) ? curBinding : null,
                    mode: mode,
                    fromDispatch: viaDispatchOf.TryGetValue(current, out var vd) && vd
                )
            )
            {
                // Merge this edge's carried binding into the target; a node whose binding GREW is
                // re-enqueued (even if already reached) so its generic dispatch re-expands under the
                // larger binding — this is what keeps the closure sound at shared generic hubs.
                var grew = MergeBinding(bindingOf, s.Node, s.OutBinding);
                if (info.ContainsKey(s.Node))
                {
                    if (grew)
                    {
                        queue.Enqueue(s.Node);
                    }

                    continue;
                }
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
                // HandoffVia (clone of the DispatchVia inheritance): set when THIS edge is a handoff,
                // else inherited from the parent — so the whole scheduled subtree carries the
                // provenance, and a node first reached synchronously (shorter route) carries none.
                var handoffVia = s.HandoffVia ?? cur.HandoffVia;
                // Dispatch-basis inheritance: "heuristic" is STICKY (one guessed hop taints the whole
                // downstream reach), otherwise this edge's basis or the inherited one.
                var basis = s.Basis == "heuristic" || cur.DispatchBasis == "heuristic" ? "heuristic" : (s.Basis ?? cur.DispatchBasis);
                info[s.Node] = new ReachInfo(
                    Depth: cur.Depth + 1,
                    LoopNesting: nesting,
                    NearestLoopKind: nearKind,
                    NearestLoopDetail: nearDetail,
                    DispatchVia: via,
                    DispatchDegree: degree,
                    HandoffVia: handoffVia,
                    DispatchBasis: basis
                );
                receiverOf[s.Node] = s.OutReceiver;
                viaDispatchOf[s.Node] = IsDispatchEdgeKind(s.Kind);
                queue.Enqueue(s.Node);
            }
        }

        return info;
    }

    // Upper bound on a node's accumulated type-arg binding — a runaway guard for whole-codebase reaches
    // (real per-node bindings are a handful of types; entity-construct fan-outs are ~tens). At the cap,
    // growth stops: the binding stays recall-safe (it only ever narrows when a candidate matches, never
    // empties the set), so a saturated binding simply narrows less, never wrongly.
    private const int MaxBinding = 256;

    // Unions `incoming` into the carried binding of `node`, creating the entry if absent. Returns true
    // when the node's binding actually GREW (new concrete types added) — the signal to re-enqueue a
    // shared generic hub so its dispatch re-expands under the larger binding (the closure fixpoint).
    private static bool MergeBinding(Dictionary<string, HashSet<string>?> bindingOf, string node, IReadOnlyCollection<string>? incoming)
    {
        var hadEntry = bindingOf.TryGetValue(node, out var existing);
        if (incoming is null || incoming.Count == 0)
        {
            if (!hadEntry)
            {
                bindingOf[node] = null; // record the node as reached with no binding (full CHA)
            }

            return false;
        }
        if (!hadEntry || existing is null)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in incoming)
            {
                if (set.Count >= MaxBinding)
                {
                    break;
                }

                set.Add(t);
            }
            bindingOf[node] = set;
            return set.Count > 0;
        }
        var before = existing.Count;
        foreach (var t in incoming)
        {
            if (existing.Count >= MaxBinding)
            {
                break;
            }

            existing.Add(t);
        }
        return existing.Count > before;
    }

    // Builds the call TREE rooted at every node matching `fromPattern` (rig tree). Same edge model
    // as Reaches/Find (direct calls + interface->impl + base->override dispatch, with loop context),
    // but materialized as a tree for rendering. Each method is EXPANDED ONCE globally: the first time
    // it's reached (shallowest depth, source order among same-depth peers) its children are built;
    // later encounters become a Truncated leaf ("⋯elided"), so a cycle or a heavily-shared callee can't
    // blow the tree up. maxDepth bounds depth; maxNodes bounds total emitted nodes (a Truncated leaf
    // is emitted at the cap). Returns one TraceNode per root.
    //
    // BFS (shallowest-first) ensures a shallow direct call is NEVER stolen by a deep infra seam that
    // happened to be expanded first in DFS source order: BFS processes nodes at increasing depth, and
    // among same-depth peers it preserves source order (Successors yields children in line order).
    // Cut + context shaping is carried on `graph` (set by ShapeGraph at load) and applied via BuildIndex:
    // a cut node is expanded as a leaf — its own effects are visible but its subtree is not walked.
    public static IReadOnlyList<TraceNode> BuildTree(
        FactGraphData graph,
        string fromPattern,
        int maxDepth = 20,
        int maxNodes = 20000,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph);

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var budget = maxNodes;

        // Mutable build node — built during BFS, converted to immutable TraceNode at the end.
        // Receiver/Binding are the narrowing contexts inherited from the edge that enqueued this node,
        // passed into Successors when this node is expanded so its own dispatch is narrowed correctly.
        var mutableRoots = new List<MutableNode>();
        // DEPTH-FIRST, PRE-ORDER traversal (a stack, children pushed in reverse so they pop in render
        // order). This makes `expanded` fill in exactly top-to-bottom reading order, so the FIRST visual
        // occurrence of a shared symbol is the one expanded and every LATER occurrence is the "⋯elided"
        // leaf — the marker always refers to a subtree already shown ABOVE it. (A breadth-first walk
        // expanded whichever occurrence was shallowest, which could render BELOW a deeper twin, leaving
        // the "⋯elided" reading before its expansion.)
        var stack = new Stack<MutableNode>();

        var matched = index.Nodes.Where(n => Contains(value: n, pattern: fromPattern)).ToHashSet(StringComparer.Ordinal);
        foreach (var root in matched.Where(n => !IsContainedLambdaOfMatched(n, matched)).OrderBy(n => n, StringComparer.Ordinal))
        {
            var node = new MutableNode(
                symbol: root,
                edgeKind: "entry",
                loopKind: null,
                loopDetail: null,
                depth: 0,
                handoffVia: null,
                dispatchBasis: null,
                fanout: 0,
                receiver: null,
                binding: null,
                declaringTypeArgBinding: null,
                methodTypeArgBinding: null,
                callFile: null,
                callLine: 0
            );
            mutableRoots.Add(node);
        }
        // Push roots reversed so the first root's whole subtree is walked before the next root's.
        for (var i = mutableRoots.Count - 1; i >= 0; i--)
        {
            stack.Push(mutableRoots[i]);
        }

        while (stack.Count > 0 && budget > 0)
        {
            var n = stack.Pop();
            budget--;

            // Already expanded elsewhere (cycle / shared callee), at depth cap, or out of budget:
            // mark as truncated and do NOT expand. budget check is re-checked after decrement.
            if (expanded.Contains(n.Symbol) || n.Depth >= maxDepth || budget <= 0)
            {
                n.Truncated = true;
                continue;
            }

            expanded.Add(n.Symbol);

            // Traversal cut: the node is a leaf — emit it but don't walk its successors.
            // The cut is checked AFTER marking expanded so the node itself is rendered correctly
            // (its own effects are visible); only its successors are suppressed.
            if (index.ApplyTraversalCuts && index.IsTraversalCut(n.Symbol))
            {
                continue;
            }

            foreach (
                var s in Successors(
                    current: n.Symbol,
                    index: index,
                    incomingReceiver: n.Receiver,
                    incomingBinding: n.Binding,
                    mode: mode,
                    fromDispatch: IsDispatchEdgeKind(n.EdgeKind)
                )
            )
            {
                // Collapse identical sibling edges: a generic method or bodied accessor called N times
                // under one parent resolves to one symbol → N edges that would render byte-identically
                // (1 expansion + N-1 "⋯elided"). Fold them into a single kid carrying a call-site count.
                // Keyed on every field that affects the rendered line so only true duplicates merge.
                // Manual scan rather than Kids.FirstOrDefault(k => ...): the lambda captures `s`, so the
                // LINQ form heap-allocated a closure + delegate on every successor edge of every node.
                MutableNode? dup = null;
                foreach (var k in n.Kids)
                {
                    if (
                        k.Symbol == s.Node
                        && k.EdgeKind == s.Kind
                        && k.LoopKind == s.LoopKind
                        && k.LoopDetail == s.LoopDetail
                        && k.HandoffVia == s.HandoffVia
                        && k.Fanout == s.Fanout
                        && k.DispatchBasis == s.Basis
                    )
                    {
                        dup = k;
                        break;
                    }
                }

                if (dup is not null)
                {
                    dup.CallSites++;
                    continue;
                }
                var kid = new MutableNode(
                    symbol: s.Node,
                    edgeKind: s.Kind,
                    loopKind: s.LoopKind,
                    loopDetail: s.LoopDetail,
                    depth: n.Depth + 1,
                    handoffVia: s.HandoffVia,
                    dispatchBasis: s.Basis,
                    fanout: s.Fanout,
                    receiver: s.OutReceiver,
                    binding: s.OutBinding,
                    declaringTypeArgBinding: s.OutDeclaringBinding,
                    methodTypeArgBinding: s.OutMethodBinding,
                    callFile: s.File,
                    callLine: s.Line
                );
                n.Kids.Add(kid);
            }

            // Push this node's children reversed, so the first child (render order) is popped — and thus
            // expanded — next: a pre-order depth-first walk.
            for (var i = n.Kids.Count - 1; i >= 0; i--)
            {
                stack.Push(n.Kids[i]);
            }
        }

        return mutableRoots.Select(ToTraceNode).ToArray();
    }

    // Mutable node used during BFS tree construction; converted to immutable TraceNode afterward.
    private sealed class MutableNode
    {
        public readonly string Symbol;
        public readonly string EdgeKind;
        public readonly string? LoopKind;
        public readonly string? LoopDetail;
        public readonly int Depth;
        public readonly string? HandoffVia;
        public readonly string? DispatchBasis;
        public readonly int Fanout;

        // Narrowing contexts carried to Successors when this node is expanded:
        public readonly string? Receiver;
        public readonly IReadOnlyCollection<string>? Binding;

        // Generic monomorphization bindings of the reaching edge — RENDERING only (-> TraceNode).
        public readonly string? DeclaringTypeArgBinding;
        public readonly string? MethodTypeArgBinding;

        // The reaching edge's call site (File/Line) — RENDERING only (-> TraceNode.CallFile/CallLine).
        public readonly string? CallFile;
        public readonly int CallLine;
        public bool Truncated;

        // Distinct call sites under this node's parent that produced an identical edge (collapsed
        // siblings). Bumped instead of adding a duplicate kid; rendered as "×N calls".
        public int CallSites = 1;
        public readonly List<MutableNode> Kids = new List<MutableNode>();

        public MutableNode(
            string symbol,
            string edgeKind,
            string? loopKind,
            string? loopDetail,
            int depth,
            string? handoffVia,
            string? dispatchBasis,
            int fanout,
            string? receiver,
            IReadOnlyCollection<string>? binding,
            string? declaringTypeArgBinding,
            string? methodTypeArgBinding,
            string? callFile,
            int callLine
        )
        {
            Symbol = symbol;
            EdgeKind = edgeKind;
            LoopKind = loopKind;
            LoopDetail = loopDetail;
            Depth = depth;
            HandoffVia = handoffVia;
            DispatchBasis = dispatchBasis;
            Fanout = fanout;
            Receiver = receiver;
            Binding = binding;
            DeclaringTypeArgBinding = declaringTypeArgBinding;
            MethodTypeArgBinding = methodTypeArgBinding;
            CallFile = callFile;
            CallLine = callLine;
        }
    }

    private static TraceNode ToTraceNode(MutableNode n)
    {
        if (n.Truncated)
        {
            return new TraceNode(
                SymbolId: n.Symbol,
                EdgeKind: n.EdgeKind,
                LoopKind: n.LoopKind,
                LoopDetail: n.LoopDetail,
                Children: EmptyNodes,
                Truncated: true,
                Fanout: n.Fanout,
                HandoffVia: n.HandoffVia,
                DispatchBasis: n.DispatchBasis,
                CallSites: n.CallSites,
                DeclaringTypeArgBinding: n.DeclaringTypeArgBinding,
                MethodTypeArgBinding: n.MethodTypeArgBinding,
                CallFile: n.CallFile,
                CallLine: n.CallLine
            );
        }

        var children = n.Kids.Count == 0 ? EmptyNodes : n.Kids.Select(ToTraceNode).ToArray();

        return new TraceNode(
            SymbolId: n.Symbol,
            EdgeKind: n.EdgeKind,
            LoopKind: n.LoopKind,
            LoopDetail: n.LoopDetail,
            Children: children,
            Fanout: n.Fanout,
            HandoffVia: n.HandoffVia,
            DispatchBasis: n.DispatchBasis,
            CallSites: n.CallSites,
            DeclaringTypeArgBinding: n.DeclaringTypeArgBinding,
            MethodTypeArgBinding: n.MethodTypeArgBinding,
            CallFile: n.CallFile,
            CallLine: n.CallLine
        );
    }

    private static readonly IReadOnlyList<TraceNode> EmptyNodes = [];

    // Multi-source forward reachability: the union of everything reachable from ANY of the given root
    // symbol IDs, using the same edge model as Reaches/Find/tree (direct calls + method-group/ctor
    // edges + interface->impl and base->override dispatch). Roots are matched by EXACT SymbolId (not
    // substring) — callers pass concrete entry-point DocIDs. Unknown root ids (not present as graph
    // nodes) are skipped. Underpins the unreachable-symbol / dead-code finder: dead = first-party
    // methods − this set − the roots themselves.
    public static HashSet<string> ReachableFromAll(
        FactGraphData graph,
        IEnumerable<string> roots,
        int maxNodes = 2_000_000,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var receiverOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var root in roots)
        {
            if (index.Nodes.Contains(root) && seen.Add(root))
            {
                receiverOf[root] = null;
                queue.Enqueue(root);
            }
        }

        while (queue.Count > 0 && seen.Count < maxNodes)
        {
            var current = queue.Dequeue();
            foreach (
                var s in Successors(
                    current: current,
                    index: index,
                    incomingReceiver: receiverOf.TryGetValue(key: current, value: out var rc) ? rc : null,
                    incomingBinding: null,
                    mode: mode
                )
            )
            {
                if (seen.Add(s.Node))
                {
                    receiverOf[s.Node] = s.OutReceiver;
                    queue.Enqueue(s.Node);
                }
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
        bool narrowDispatch = true,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph, narrowDispatch);
        var rev = BuildReverseMaps(graph, narrowDispatch, mode, descendantsFrom: index);
        return ReachedByCore(index, rev, toPattern, maxDepth, maxNodes);
    }

    // The reverse-BFS core, factored out so a caller that ALREADY holds the index + reverse maps can reuse
    // them instead of rebuilding. EntryRootsReaching builds both for its own no-predecessor root check and
    // then needs this same closure — calling ReachedBy() rebuilt index + reverse maps a second time, and
    // BuildReverseMaps does a whole-graph receiver-blind dispatch scan, so that was the dominant cost of
    // `callers --roots`. Passing the prebuilt pair here halves it.
    private static IReadOnlyDictionary<string, int> ReachedByCore(
        GraphIndex index,
        ReverseMaps rev,
        string toPattern,
        int maxDepth,
        int maxNodes
    )
    {
        var depthOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var start in index.Nodes.Where(n => Contains(value: n, pattern: toPattern)))
        {
            if (depthOf.ContainsKey(start))
            {
                continue;
            }

            depthOf[start] = 0;
            queue.Enqueue(start);
        }

        while (queue.Count > 0 && depthOf.Count < maxNodes)
        {
            var current = queue.Dequeue();
            var depth = depthOf[current];
            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var pred in Predecessors(current, index, rev))
            {
                if (depthOf.ContainsKey(pred))
                {
                    continue;
                }

                depthOf[pred] = depth + 1;
                queue.Enqueue(pred);
            }
        }

        return depthOf;
    }

    // Multi-source reverse reachability — like ReachedBy, but seeded from a SET of EXACT SymbolIds
    // (not a substring pattern), returning the union of everything that can reach ANY of them keyed
    // to its shortest reverse hop count to the nearest seed. Mirrors ReachableFromAll's exact-id
    // seeding on the reverse maps: unknown seed ids (not graph nodes) are skipped. This is the engine
    // `rig impact` reverse-reaches from a diff's changed-method set with — one index/reverse-map build
    // shared across all seeds, instead of calling ReachedBy once per changed method (which rebuilds
    // both each time). Same Predecessors edge model as ReachedBy, so the closure is identical.
    public static IReadOnlyDictionary<string, int> ReachedByAny(
        FactGraphData graph,
        IEnumerable<string> seeds,
        int maxDepth = 20,
        int maxNodes = 20000,
        bool narrowDispatch = true,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph, narrowDispatch);
        var rev = BuildReverseMaps(graph, narrowDispatch, mode, descendantsFrom: index);

        var depthOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var seed in seeds)
        {
            // Seed by EXACT id (the changed methods are concrete DocIDs). A seed absent from the graph
            // (e.g. a method with no edges either way) is simply not a traversal node — skip it.
            if (index.Nodes.Contains(seed) && !depthOf.ContainsKey(seed))
            {
                depthOf[seed] = 0;
                queue.Enqueue(seed);
            }
        }

        while (queue.Count > 0 && depthOf.Count < maxNodes)
        {
            var current = queue.Dequeue();
            var depth = depthOf[current];
            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var pred in Predecessors(current, index, rev))
            {
                if (depthOf.ContainsKey(pred))
                {
                    continue;
                }

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
        FactGraphData graph,
        string toPattern,
        int maxDepth = 20,
        int maxNodes = 20000,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph);
        var rev = BuildReverseMaps(graph, narrowDispatch: true, mode, descendantsFrom: index);
        // Reuse the index + reverse maps just built (for the Predecessors root check below) — ReachedByCore
        // takes them prebuilt, so the closure shares this one build instead of ReachedBy rebuilding both.
        var reachable = ReachedByCore(index, rev, toPattern, maxDepth, maxNodes);
        var roots = new List<string>();
        foreach (var m in reachable.Keys)
        {
            if (!Predecessors(m, index, rev).Any())
            {
                roots.Add(m);
            }
        }

        roots.Sort(StringComparer.Ordinal);
        return roots;
    }

    // Materialises EVERY synthetic dispatch edge in the graph — (sourceMethod -> targetMethod, kind,
    // basis) for interface->impl and base-virtual/abstract->override: the Roslyn-MINED edges
    // (Basis="roslyn", the forward closure of dispatch_facts) plus the flagged heuristic fallback
    // (error-type simple-name recovery + name/arity CHA for unmined members, Basis="heuristic"), for
    // all method nodes, receiver-blind. This feeds the precomputed `dispatch_edges` table, the SOUND
    // SUPERSET that bounds the SQL reachability load — narrowing happens only in the in-memory edge
    // traversal (Successors), so dispatch_edges must stay receiver-blind so the bounded subgraph it
    // produces still contains every edge a narrowed traversal could visit. Deduped per source;
    // sources are distinct.
    public static IEnumerable<(string From, string To, string Kind, string Basis)> AllDispatchEdges(FactGraphData graph)
    {
        var index = BuildIndex(graph, narrowDispatch: false);
        foreach (var node in index.Nodes)
        foreach (var target in DispatchTargets(node, index, receiverType: null))
        {
            yield return (node, target.Node, target.Kind, target.Basis);
        }
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
        string? ReceiverType,
        string? HandoffDispatcher
    )> AllCallEdges(FactGraphData graph)
    {
        foreach (var edge in graph.CallEdges)
        {
            yield return (
                edge.Caller,
                edge.Callee,
                edge.Kind,
                edge.FilePath,
                edge.Line,
                edge.LoopKind,
                edge.LoopDetail,
                edge.ReceiverType,
                edge.HandoffDispatcher
            );
        }
    }

    // Transitive strict descendants of a type, memoised. Keyed on the generic-stripped id so the
    // instantiated/open-generic forms share one cache entry.
    private static HashSet<string> Descendants(string typeId, GraphIndex index)
    {
        var key = TypeClosure.StripGeneric(typeId);
        return index.DescendantsCache.GetOrAdd(key, _ => TypeClosure.ComputeStrictDescendants(index.StrippedBaseEdges, new[] { typeId }));
    }

    private static void Enqueue(
        Dictionary<
            string,
            (
                string From,
                string Kind,
                string? File,
                int Line,
                string? LoopKind,
                string? LoopDetail,
                int Fanout,
                string? HandoffVia,
                string? Basis
            )?
        > parent,
        Queue<(string, int)> queue,
        string node,
        string from,
        string kind,
        string? file,
        int line,
        string? loopKind,
        string? loopDetail,
        int fanout,
        string? handoffVia,
        string? basis,
        int depth
    )
    {
        if (parent.ContainsKey(node))
        {
            return;
        }

        parent[node] = (from, kind, file, line, loopKind, loopDetail, fanout, handoffVia, basis);
        queue.Enqueue((node, depth + 1));
    }

    private static IReadOnlyList<PathStep> Reconstruct(
        Dictionary<
            string,
            (
                string From,
                string Kind,
                string? File,
                int Line,
                string? LoopKind,
                string? LoopDetail,
                int Fanout,
                string? HandoffVia,
                string? Basis
            )?
        > parent,
        string target
    )
    {
        var steps = new List<PathStep>();
        var node = target;
        while (true)
        {
            var link = parent[node];
            steps.Add(
                new PathStep(
                    SymbolId: node,
                    Kind: link?.Kind ?? "entry",
                    FilePath: link?.File,
                    Line: link?.Line ?? 0,
                    LoopKind: link?.LoopKind,
                    LoopDetail: link?.LoopDetail,
                    Fanout: link?.Fanout ?? 0,
                    HandoffVia: link?.HandoffVia,
                    DispatchBasis: link?.Basis
                )
            );
            if (link is null)
            {
                break;
            }

            node = link.Value.From;
        }
        steps.Reverse();
        return steps;
    }

    // "M:Ns.Type.Member(args)" -> ("T:Ns.Type", "Member"). Null when not a method DocID.
    private static (string TypeId, string Name)? ParseMethod(string docId)
    {
        if (!docId.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        var body = docId.Substring(2);
        var paren = body.IndexOf('(');
        if (paren >= 0)
        {
            body = body.Substring(startIndex: 0, length: paren);
        }

        var lastDot = body.LastIndexOf('.');
        if (lastDot < 0)
        {
            return null;
        }

        return ("T:" + body.Substring(startIndex: 0, length: lastDot), body.Substring(lastDot + 1));
    }

    // Parameter ARITY of a method DocID: the number of top-level parameters in its "(...)" list, or 0
    // when there is none ("M:T.M" / "M:T.M()"). Commas inside generic-argument braces "{...}" or array
    // brackets "[...]" don't count (e.g. "Func{A,B,C}" is ONE parameter). Used to stop name-only
    // interface/override dispatch from matching a same-named OVERLOAD with a different signature.
    private static int ParamArity(string docId)
    {
        var open = docId.IndexOf('(');
        if (open < 0)
        {
            return 0;
        }

        var close = docId.LastIndexOf(')');
        if (close <= open + 1)
        {
            return 0; // "()" — no parameters
        }

        var count = 1;
        var depth = 0;
        for (var i = open + 1; i < close; i++)
        {
            var c = docId[i];
            if (c is '{' or '[' or '(')
            {
                depth++;
            }
            else if (c is '}' or ']' or ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                count++;
            }
        }
        return count;
    }

    // Simple (un-namespaced, arity-stripped) name from a type DocID:
    // "T:Ns.IFoo`1" / "!:IFoo" -> "IFoo".
    private static string SimpleTypeName(string typeId)
    {
        var s = typeId;
        if (s.Length >= 2 && s[1] == ':')
        {
            s = s.Substring(2);
        }

        var lastDot = s.LastIndexOf('.');
        if (lastDot >= 0)
        {
            s = s.Substring(lastDot + 1);
        }

        var tick = s.IndexOf('`');
        if (tick >= 0)
        {
            s = s.Substring(startIndex: 0, length: tick);
        }

        return s;
    }

    private static bool Contains(string value, string pattern) => value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;

    // A synthetic lambda node id is `{containerMemberId}~λ{ordinal}` (FactExtractor). When the root pattern
    // matches a method AND its inline lambdas (e.g. `tree "Foo"` matches Foo, Foo~λ0, Foo~λ1), the lambdas
    // are NOT independent roots: each already renders inline under its container, so re-rooting it would
    // emit a spurious top-level `⋯elided` (the container's expansion already marked it seen). Drop a matched
    // lambda only when its container ALSO matched; a lambda whose container did not match (e.g. a promoted
    // async-handoff entry point targeted on its own) stays a legitimate root.
    private static bool IsContainedLambdaOfMatched(string nodeId, HashSet<string> matched)
    {
        var marker = nodeId.IndexOf("~λ", StringComparison.Ordinal);
        return marker > 0 && matched.Contains(nodeId.Substring(0, marker));
    }
}

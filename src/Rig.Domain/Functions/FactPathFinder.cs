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
public static class FactPathFinder
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

            foreach (var s in Successors(current, index, receiverOf.TryGetValue(current, out var rc) ? rc : null, null, mode))
            {
                if (!parent.ContainsKey(s.Node))
                    receiverOf[s.Node] = s.OutReceiver;
                Enqueue(
                    parent,
                    queue,
                    s.Node,
                    current,
                    s.Kind,
                    s.File,
                    s.Line,
                    s.LoopKind,
                    s.LoopDetail,
                    s.Fanout,
                    s.HandoffVia,
                    s.Basis,
                    depth
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
        TraversalMode mode = TraversalMode.SyncCut,
        IReadOnlyList<FactTraversalCutRule>? cutRules = null
    )
    {
        var info = ReachesWithFanout(graph, fromPattern, maxDepth, maxNodes, narrowDispatch, mode, cutRules);
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
        bool narrowDispatch = true,
        TraversalMode mode = TraversalMode.SyncCut,
        IReadOnlyList<FactTraversalCutRule>? cutRules = null
    )
    {
        var index = BuildIndex(graph, narrowDispatch);
        if (cutRules is { Count: > 0 })
        {
            index.TraversalCutRules = cutRules;
            index.ApplyTraversalCuts = true;
        }
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
        var queue = new Queue<string>();
        foreach (var start in index.Nodes.Where(n => Contains(n, fromPattern)))
        {
            if (info.ContainsKey(start))
                continue;
            info[start] = new ReachInfo(0, 0, null, null);
            receiverOf[start] = null;
            bindingOf[start] = null;
            queue.Enqueue(start);
        }

        while (queue.Count > 0 && info.Count < maxNodes)
        {
            var current = queue.Dequeue();
            var cur = info[current];
            if (cur.Depth >= maxDepth)
                continue;
            foreach (
                var s in Successors(
                    current,
                    index,
                    receiverOf[current],
                    bindingOf.TryGetValue(current, out var curBinding) ? curBinding : null,
                    mode
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
                        queue.Enqueue(s.Node);
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
                info[s.Node] = new ReachInfo(cur.Depth + 1, nesting, nearKind, nearDetail, via, degree, handoffVia, basis);
                receiverOf[s.Node] = s.OutReceiver;
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
                bindingOf[node] = null; // record the node as reached with no binding (full CHA)
            return false;
        }
        if (!hadEntry || existing is null)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in incoming)
            {
                if (set.Count >= MaxBinding)
                    break;
                set.Add(t);
            }
            bindingOf[node] = set;
            return set.Count > 0;
        }
        var before = existing.Count;
        foreach (var t in incoming)
        {
            if (existing.Count >= MaxBinding)
                break;
            existing.Add(t);
        }
        return existing.Count > before;
    }

    // Monomorphizes generic-FACTORY call edges (see FactGenericFactoryRule): an edge
    // `caller -> Factory<X,..>` whose call-site construct type arg X is concrete is rewritten to
    // `caller -> X.Target`, so the traversal goes straight to the constructed type's method and skips the
    // generic plumbing the factory forwards through (Entity.New``3 -> EntityCache`3.New -> ItemCache`3.Get
    // -> Construct`2.New -> ×N entity ctors). Edges with no concrete construct (a forwarded type
    // parameter) or whose target can't be resolved in the loaded graph are left intact — the in-memory
    // generic-dispatch narrowing remains the fallback there. Pure: returns a new FactGraphData with the
    // rewritten edges; Methods and type-relation edges are unchanged. Applied once after graph load so
    // tree / reaches / callers all see the collapsed graph.
    public static FactGraphData RewriteGenericFactories(FactGraphData graph, IReadOnlyList<FactGenericFactoryRule> rules)
    {
        if (rules.Count == 0)
            return graph;

        // (stripped construct type DocID, method name) -> overloads, for resolving X.Target.
        var methodsByTypeAndName = new Dictionary<(string Type, string Name), List<MethodRef>>();
        foreach (var m in graph.Methods)
        {
            if (m.ContainingTypeId is null)
                continue;
            var key = (TypeClosure.StripGeneric(m.ContainingTypeId), m.Name);
            if (!methodsByTypeAndName.TryGetValue(key, out var list))
                methodsByTypeAndName[key] = list = new List<MethodRef>();
            list.Add(m);
        }

        var ruleByMethod = new Dictionary<string, FactGenericFactoryRule>(StringComparer.Ordinal);
        foreach (var r in rules)
            ruleByMethod[r.Method] = r;

        var rewritten = new List<CallEdge>(graph.CallEdges.Count);
        var changed = false;
        foreach (var edge in graph.CallEdges)
        {
            var resolved = ResolveFactoryEdge(edge, ruleByMethod, methodsByTypeAndName);
            if (resolved is null)
                rewritten.Add(edge);
            else
            {
                rewritten.AddRange(resolved);
                changed = true;
            }
        }
        return changed ? graph with { CallEdges = rewritten } : graph;
    }

    // Resolves one call edge against the factory rules: null when the edge isn't a (concrete) factory
    // call, else the rewritten edge(s) targeting the construct type's method overloads (arity-matched to
    // the factory call). Returns null (keep the edge) when nothing resolves — never drops the edge.
    private static List<CallEdge>? ResolveFactoryEdge(
        CallEdge edge,
        Dictionary<string, FactGenericFactoryRule> ruleByMethod,
        Dictionary<(string Type, string Name), List<MethodRef>> methodsByTypeAndName
    )
    {
        var parsed = ParseMethod(edge.Callee);
        if (parsed is null)
            return null;
        // ParseMethod returns TypeId WITH the "T:" prefix and a name that still carries the method
        // generic-arity marker (e.g. "New``3"); rule.Method is a plain "<declType>.<name>", so strip
        // the "``N" before matching.
        var name = parsed.Value.Name;
        var tick = name.IndexOf("``", StringComparison.Ordinal);
        if (tick >= 0)
            name = name.Substring(0, tick);
        var methodKey = parsed.Value.TypeId.Substring(2) + "." + name;
        if (!ruleByMethod.TryGetValue(methodKey, out var rule))
            return null;
        if (string.IsNullOrEmpty(edge.TypeArguments))
            return null;
        var construct = NthTopLevelArg(edge.TypeArguments!, rule.ConstructArgIndex);
        // Only a concrete, namespaced type can name a real construct; a bare type-parameter token
        // ("TConstruct") or primitive has no '.' and isn't resolvable -> leave the edge for the fallback.
        if (construct is null || construct.IndexOf('.') < 0)
            return null;
        var constructType = "T:" + TypeClosure.StripGeneric(construct);
        if (!methodsByTypeAndName.TryGetValue((constructType, rule.TargetMethod), out var candidates))
            return null;
        var arity = ParamArity(edge.Callee);
        var matched = candidates.Where(c => ParamArity(c.SymbolId) == arity).ToList();
        if (matched.Count == 0)
            matched = candidates; // no arity match — take all overloads; still bypasses the plumbing
        if (matched.Count == 0)
            return null;

        // Disambiguate same-arity overloads by the PK type. The factory's first parameter is a method
        // type-param reference (Entity.New``3(``1) -> index 1 = TPk), so the pk type is type-arg[1];
        // keep only target overloads whose own first parameter type matches it (C#-keyword normalized,
        // so `int` == System.Int32). Without this, `Entity.New<Account,Guid,…>` resolved to BOTH
        // Account.New(Guid) and Account.New(Int32) (same arity). Recall-safe: an empty match keeps the
        // arity-matched set.
        if (matched.Count > 1)
        {
            var pkIndex = TypeParamRefIndex(FirstTopLevelParam(edge.Callee));
            if (pkIndex >= 0 && NthTopLevelArg(edge.TypeArguments!, pkIndex) is { } pkType)
            {
                var pkNorm = NormalizeTypeName(pkType);
                var byPk = matched.Where(c => NormalizeTypeName(FirstTopLevelParam(c.SymbolId)) == pkNorm).ToList();
                if (byPk.Count > 0)
                    matched = byPk;
            }
        }
        return matched.Select(m => edge with { Callee = m.SymbolId, TypeArguments = null }).ToList();
    }

    // C# keyword aliases -> BCL simple name, so a pk type arg rendered as a keyword (`int`) compares
    // equal to a DocID parameter type (`System.Int32`).
    private static readonly Dictionary<string, string> CSharpKeywordTypes = new(StringComparer.Ordinal)
    {
        ["int"] = "Int32",
        ["uint"] = "UInt32",
        ["long"] = "Int64",
        ["ulong"] = "UInt64",
        ["short"] = "Int16",
        ["ushort"] = "UInt16",
        ["byte"] = "Byte",
        ["sbyte"] = "SByte",
        ["bool"] = "Boolean",
        ["char"] = "Char",
        ["string"] = "String",
        ["object"] = "Object",
        ["float"] = "Single",
        ["double"] = "Double",
        ["decimal"] = "Decimal",
        ["nint"] = "IntPtr",
        ["nuint"] = "UIntPtr",
    };

    // Simple type name (namespace + generic/array suffix stripped), with C# keyword aliases mapped to
    // their BCL name so "int" and "System.Int32" compare equal. "" for null/blank.
    private static string NormalizeTypeName(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "";
        var t = type!.Trim();
        var marker = t.IndexOfAny(['{', '<', '[']);
        if (marker >= 0)
            t = t.Substring(0, marker);
        var dot = t.LastIndexOf('.');
        var simple = dot >= 0 ? t.Substring(dot + 1) : t;
        return CSharpKeywordTypes.TryGetValue(simple, out var bcl) ? bcl : simple;
    }

    // The first top-level parameter substring of a method DocID's "(...)" list, or null if there is none.
    private static string? FirstTopLevelParam(string docId)
    {
        var open = docId.IndexOf('(');
        if (open < 0)
            return null;
        var close = docId.LastIndexOf(')');
        if (close <= open + 1)
            return null;
        var depth = 0;
        for (var i = open + 1; i < close; i++)
        {
            var c = docId[i];
            if (c is '{' or '[' or '(' or '<')
                depth++;
            else if (c is '}' or ']' or ')' or '>')
                depth--;
            else if (c == ',' && depth == 0)
                return docId.Substring(open + 1, i - (open + 1)).Trim();
        }
        return docId.Substring(open + 1, close - (open + 1)).Trim();
    }

    // A method type-parameter reference token ("``N") -> its index N; -1 for anything else (a concrete
    // type, a type-level "`N", or null). Used to find which type arg fills a factory's pk parameter.
    private static int TypeParamRefIndex(string? param)
    {
        if (param is null || !param.StartsWith("``", StringComparison.Ordinal))
            return -1;
        return int.TryParse(param.Substring(2), out var n) ? n : -1;
    }

    // The Nth (0-based) top-level element of a comma-joined type-arg list — commas inside <>/()/[]
    // don't split (so a tuple/generic arg stays whole). Null when out of range / blank.
    private static string? NthTopLevelArg(string typeArguments, int index)
    {
        if (index < 0)
            return null;
        var depth = 0;
        var position = 0;
        var start = 0;
        for (var i = 0; i < typeArguments.Length; i++)
        {
            var c = typeArguments[i];
            if (c is '<' or '(' or '[')
                depth++;
            else if (c is '>' or ')' or ']')
                depth--;
            else if (c == ',' && depth == 0)
            {
                if (position == index)
                    return typeArguments.Substring(start, i - start).Trim();
                position++;
                start = i + 1;
            }
        }
        return position == index ? typeArguments.Substring(start).Trim() : null;
    }

    // Builds the call TREE rooted at every node matching `fromPattern` (rig tree). Same edge model
    // as Reaches/Find (direct calls + interface->impl + base->override dispatch, with loop context),
    // but materialized as a tree for rendering. Each method is EXPANDED ONCE globally: the first time
    // it's reached (shallowest depth, source order among same-depth peers) its children are built;
    // later encounters become a Truncated leaf ("seen"), so a cycle or a heavily-shared callee can't
    // blow the tree up. maxDepth bounds depth; maxNodes bounds total emitted nodes (a Truncated leaf
    // is emitted at the cap). Returns one TraceNode per root.
    //
    // BFS (shallowest-first) ensures a shallow direct call is NEVER stolen by a deep infra seam that
    // happened to be expanded first in DFS source order: BFS processes nodes at increasing depth, and
    // among same-depth peers it preserves source order (Successors yields children in line order).
    // `cutRules`: when non-null, nodes matching a traversal-cut rule are expanded as leaves — their
    // own effects are visible but their subtree is not walked (stops reflection seams from expanding).
    public static IReadOnlyList<TraceNode> BuildTree(
        FactGraphData graph,
        string fromPattern,
        int maxDepth = 20,
        int maxNodes = 20000,
        TraversalMode mode = TraversalMode.SyncCut,
        IReadOnlyList<FactTraversalCutRule>? cutRules = null
    )
    {
        var index = BuildIndex(graph);
        if (cutRules is { Count: > 0 })
        {
            index.TraversalCutRules = cutRules;
            index.ApplyTraversalCuts = true;
        }

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var budget = maxNodes;

        // Mutable build node — built during BFS, converted to immutable TraceNode at the end.
        // Receiver/Binding are the narrowing contexts inherited from the edge that enqueued this node,
        // passed into Successors when this node is expanded so its own dispatch is narrowed correctly.
        var mutableRoots = new List<MutableNode>();
        var queue = new Queue<MutableNode>();

        foreach (var root in index.Nodes.Where(n => Contains(n, fromPattern)).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (budget <= 0)
                break;
            var node = new MutableNode(root, "entry", null, null, 0, null, null, 0, null, null);
            mutableRoots.Add(node);
            queue.Enqueue(node);
        }

        while (queue.Count > 0 && budget > 0)
        {
            var n = queue.Dequeue();
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
                continue;

            foreach (var s in Successors(n.Symbol, index, n.Receiver, n.Binding, mode))
            {
                var kid = new MutableNode(
                    s.Node,
                    s.Kind,
                    s.LoopKind,
                    s.LoopDetail,
                    n.Depth + 1,
                    s.HandoffVia,
                    s.Basis,
                    s.Fanout,
                    s.OutReceiver,
                    s.OutBinding
                );
                n.Kids.Add(kid);
                queue.Enqueue(kid);
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
        public bool Truncated;
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
            IReadOnlyCollection<string>? binding
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
        }
    }

    private static TraceNode ToTraceNode(MutableNode n)
    {
        if (n.Truncated)
            return new TraceNode(
                n.Symbol,
                n.EdgeKind,
                n.LoopKind,
                n.LoopDetail,
                EmptyNodes,
                Truncated: true,
                Fanout: n.Fanout,
                HandoffVia: n.HandoffVia,
                DispatchBasis: n.DispatchBasis
            );

        var children = n.Kids.Count == 0 ? EmptyNodes : (IReadOnlyList<TraceNode>)n.Kids.Select(ToTraceNode).ToArray();
        return new TraceNode(
            n.Symbol,
            n.EdgeKind,
            n.LoopKind,
            n.LoopDetail,
            children,
            Fanout: n.Fanout,
            HandoffVia: n.HandoffVia,
            DispatchBasis: n.DispatchBasis
        );
    }

    private static readonly IReadOnlyList<TraceNode> EmptyNodes = new TraceNode[0];

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
            if (index.Nodes.Contains(root) && seen.Add(root))
            {
                receiverOf[root] = null;
                queue.Enqueue(root);
            }

        while (queue.Count > 0 && seen.Count < maxNodes)
        {
            var current = queue.Dequeue();
            foreach (var s in Successors(current, index, receiverOf.TryGetValue(current, out var rc) ? rc : null, null, mode))
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
        bool narrowDispatch = true,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph, narrowDispatch);
        var rev = BuildReverseMaps(graph, narrowDispatch, mode);

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
        FactGraphData graph,
        string toPattern,
        int maxDepth = 20,
        int maxNodes = 20000,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        var index = BuildIndex(graph);
        var rev = BuildReverseMaps(graph, narrowDispatch: true, mode);
        var reachable = ReachedBy(graph, toPattern, maxDepth, maxNodes, narrowDispatch: true, mode);
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
            if (mode == TraversalMode.SyncCut && edge.Kind == "handoff")
                continue;

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

        // Reverse dispatch = the forward CHA dispatch edges, inverted. (The receiver-blind superset;
        // ReverseDispatchReaches narrows per hop when narrowing is on.)
        var index = BuildIndex(graph, narrowDispatch: false);
        foreach (var node in index.Nodes)
        foreach (var target in DispatchTargets(node, index, receiverType: null))
        {
            if (!rev.ReverseDispatch.TryGetValue(target.Node, out var sources))
                rev.ReverseDispatch[target.Node] = sources = new List<string>();
            sources.Add(node);
        }
        return rev;
    }

    private static IEnumerable<string> Predecessors(string current, GraphIndex index, ReverseMaps rev)
    {
        if (rev.Callers.TryGetValue(current, out var direct))
            foreach (var c in direct)
                yield return c;

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
                if (typeId is null || ReverseDispatchReaches(s, typeId, index, rev))
                    yield return s;
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
                return false;
            foreach (var rule in TraversalCutRules)
                if (rule.IsMatch(symbolId))
                    return true;
            return false;
        }
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
        foreach (var fact in graph.MinedDispatch ?? new List<DispatchFact>())
        {
            if (!index.MinedDispatchBySource.TryGetValue(fact.SourceMember, out var list))
                index.MinedDispatchBySource[fact.SourceMember] = list = new List<(string, string)>();
            if (!list.Contains((fact.TargetMember, fact.Kind)))
                list.Add((fact.TargetMember, fact.Kind));
        }
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
        string? OutReceiver,
        // The dispatcher id when THIS successor is reached via an async handoff edge (Kind=="handoff"),
        // else null — the HandoffVia provenance seed for the reached node. Only produced under
        // AsyncInclude (SyncCut skips the edge entirely).
        string? HandoffVia,
        // Provenance of a dispatch edge: "roslyn" (exact mined fact) or "heuristic" (name/arity CHA
        // fallback — flagged to the user). Null for direct call / handoff edges.
        string? Basis,
        // The concrete generic type-arg binding to carry to the TARGET node (parallel to OutReceiver):
        // the incoming binding extended with this edge's own concrete type args. Drives generic-dispatch
        // narrowing when the target is later expanded. Reference-equal to the incoming binding when the
        // edge adds no concrete type args (the common case — most edges forward type parameters or are
        // non-generic), so threading it costs no allocation on those.
        IReadOnlyCollection<string>? OutBinding
    )> Successors(
        string current,
        GraphIndex index,
        string? incomingReceiver = null,
        IReadOnlyCollection<string>? incomingBinding = null,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        // Traversal cut: if the current node matches a cut rule, emit no successors — it is a leaf.
        // The node itself was already emitted by the caller; we just stop walking into it here.
        if (index.ApplyTraversalCuts && index.IsTraversalCut(current))
            yield break;

        // Emit direct call edges in CALL-SITE SOURCE ORDER (by line, then callee for stable ties), not
        // storage order. The graph is loaded from SQL with no ORDER BY, so adjacency order is arbitrary
        // and non-deterministic; C# executes calls eagerly inline, so line order is a good approximation
        // of execution order and makes tree/path/reaches read top-to-bottom and reproduce deterministically.
        // (Approximation only: branches/loops/early-return mean lexical order != runtime order.) Each edge
        // carries its ReceiverType forward so the target's dispatch can be narrowed when it is expanded.
        if (index.Adjacency.TryGetValue(current, out var edges))
            foreach (var edge in edges.OrderBy(e => e.Line).ThenBy(e => e.Callee, StringComparer.Ordinal))
            {
                // Sync-cut: an async handoff edge schedules its callback to run later / elsewhere — it
                // is NOT a synchronous call, so we don't cross it. --async crosses it, seeding the
                // reached node with the dispatcher provenance (HandoffVia).
                // Extend the carried binding with THIS edge's concrete type args (a forwarded type
                // parameter / non-generic call adds nothing and returns the same set reference).
                var outBinding = ExtendBinding(incomingBinding, edge.TypeArguments);
                if (edge.Kind == "handoff")
                {
                    if (mode == TraversalMode.SyncCut)
                        continue;
                    yield return (
                        edge.Callee,
                        edge.Kind,
                        edge.FilePath,
                        edge.Line,
                        edge.LoopKind,
                        edge.LoopDetail,
                        0,
                        null,
                        edge.ReceiverType,
                        edge.HandoffDispatcher ?? "handoff",
                        null,
                        outBinding
                    );
                    continue;
                }
                yield return (
                    edge.Callee,
                    edge.Kind,
                    edge.FilePath,
                    edge.Line,
                    edge.LoopKind,
                    edge.LoopDetail,
                    0,
                    null,
                    PropagateReceiver(incomingReceiver, edge, index),
                    null,
                    null,
                    outBinding
                );
            }

        // Dispatch (synthetic, no call-site line) edges AFTER the line-ordered real calls — the fan-out
        // of `current` itself when it is a virtual/base/interface method, narrowed by the receiver of the
        // call that REACHED current (edge-aware) AND by the carried type-arg binding (generic-dispatch
        // narrowing). Tagged with the group's fan-out degree (N) and attributed to `current` (the dispatch
        // source). The dispatch hop adds no call-site type args, so targets inherit `current`'s binding.
        var dispatch = DispatchTargets(
            current,
            index,
            index.NarrowDispatch ? incomingReceiver : null,
            index.NarrowDispatch ? incomingBinding : null
        );
        var degree = dispatch.Count;
        // Dispatch SEEDS the concrete `this`-type for the target frame: resolving a virtual/interface call
        // to `Bar.M` means the object IS a `Bar`, so the method runs with `this` : Bar. Carry that target's
        // DECLARING type forward as the receiver (instead of null) so the dispatched-to method's own
        // `this`-virtual self-calls narrow to it — the seed the self-call propagation then threads down.
        // This is what collapses the post-impl-dispatch fan-outs (e.g. `Master.SetInvoiceSettings` resolved
        // by impl-dispatch, whose inner `this.ProvideRoles()` otherwise CHA-fanned to every workflow Master).
        foreach (var d in dispatch)
            yield return (
                d.Node,
                d.Kind,
                null,
                0,
                null,
                null,
                degree,
                current,
                DeclaringTypeDisplay(d.Node),
                null,
                d.Basis,
                incomingBinding
            );
    }

    // The display-FQN form of a method's declaring type ("M:Ns.Bar.M(..)" -> "Ns.Bar"), as ResolveNarrowRoot
    // / ReceiverToStrippedTypeId expect (no "T:" prefix; generic markers stripped downstream). Null for a
    // non-method id. Used to seed the dispatch target's concrete `this`-type as the carried receiver.
    private static string? DeclaringTypeDisplay(string methodDocId)
    {
        var typeId = ParseMethod(methodDocId)?.TypeId;
        return typeId is null ? null : typeId.Substring(2);
    }

    // Extends a carried type-arg binding with the CONCRETE type args of one call edge. "Concrete" =
    // contains a '.' (a namespaced first-party/system type, e.g. "MedDBase.…Account"); a forwarded
    // type-PARAMETER token ("TConstruct") or a bare primitive ("int") has no '.' and is skipped — only
    // namespaced types can be a dispatch candidate's declaring type. Splits on the TOP-LEVEL comma so a
    // tuple/generic arg never mis-splits. Returns the same set reference when nothing concrete is added
    // (the overwhelmingly common case), so threading the binding allocates only on genuinely generic
    // concrete call sites.
    private static IReadOnlyCollection<string>? ExtendBinding(IReadOnlyCollection<string>? current, string? typeArguments)
    {
        if (string.IsNullOrEmpty(typeArguments) || typeArguments!.IndexOf('.') < 0)
            return current;

        HashSet<string>? extended = null;
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= typeArguments.Length; i++)
        {
            if (i < typeArguments.Length)
            {
                var c = typeArguments[i];
                if (c is '<' or '(' or '[')
                    depth++;
                else if (c is '>' or ')' or ']')
                    depth--;
                if (!(c == ',' && depth == 0) && i < typeArguments.Length)
                    continue;
            }
            var part = typeArguments.Substring(start, i - start).Trim();
            start = i + 1;
            // Only namespaced types can name a dispatch candidate's declaring type; tuples/primitives/
            // type-parameter tokens can't, so they're never useful as a binding and are skipped.
            if (part.IndexOf('.') >= 0 && part.IndexOf(' ') < 0 && part.IndexOf('(') < 0)
            {
                extended ??= current is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(current, StringComparer.Ordinal);
                extended.Add(part);
            }
        }
        return extended ?? current;
    }

    // RECEIVER-CONTEXT RECOVERY (1-level object sensitivity). The static receiver type mined onto a
    // call edge is the DECLARED type at that site — for a `base.M()` it's the base, for a `this.M()` it's
    // the enclosing type — which loses the CONCRETE type the current frame is actually running as. That
    // loss is exactly what makes a `this`-virtual self-call inside a base method fan out to every sibling
    // override under CHA (e.g. `WorkflowControllerBase.Initialise` → all N workflow `Controller`s, when
    // this object is only ever `InvoiceDebtChase.Controller`). The concrete type IS known — it was pinned
    // at the outer call that entered this object (`InstantiateController<Controller>` / `c.Initialise()`)
    // and carried in `incomingReceiver`. A self-call runs the callee on the SAME object, so its `this`-type
    // is the caller's `this`-type: propagate the carried concrete receiver across self-call edges instead
    // of overwriting it with the (base-typed) edge receiver. External calls (a different object) keep the
    // edge's own receiver. Pure precision — narrowing stays recall-safe (an unmatched receiver falls back
    // to full CHA in ResolveNarrowRoot), so no real target is dropped.
    private static string? PropagateReceiver(string? incomingReceiver, CallEdge edge, GraphIndex index) =>
        IsSelfCall(incomingReceiver, edge, index) ? incomingReceiver : edge.ReceiverType;

    // True when `edge` is a call on the SAME object as the carrying frame (`this.M()` / `base.M()` /
    // implicit-`this` `M()`), given the carried concrete this-type `incomingReceiver`. Requires a known
    // concrete carried type to be meaningful (else there is nothing better to propagate). For an explicit
    // receiver, self iff the edge's (declared) receiver is an ancestor-or-equal of the concrete this-type
    // (the `this`/`base` shape) — a sibling/unrelated/more-derived-downcast receiver is an external call.
    // For a bare receiver (implicit this OR a static call), self iff the callee is declared on the carried
    // this-type's own hierarchy — so a bare static call into an unrelated type is correctly NOT self.
    private static bool IsSelfCall(string? incomingReceiver, CallEdge edge, GraphIndex index)
    {
        var inThis = incomingReceiver is null ? null : ReceiverToStrippedTypeId(incomingReceiver);
        if (inThis is null)
            return false;

        if (!string.IsNullOrEmpty(edge.ReceiverType))
        {
            var edgeRecv = ReceiverToStrippedTypeId(edge.ReceiverType!);
            return edgeRecv is not null && AncestorOrEqual(edgeRecv, inThis, index);
        }

        var calleeType = ParseMethod(edge.Callee)?.TypeId;
        if (calleeType is null)
            return false;
        var calleeStripped = TypeClosure.StripGeneric(calleeType);
        return AncestorOrEqual(calleeStripped, inThis, index) || AncestorOrEqual(inThis, calleeStripped, index);
    }

    // True when `ancestor` == `descendant`, or `descendant` is a transitive base-edge subtype of
    // `ancestor`. Both are stripped "T:Ns.Type" ids; the stripped-aware descendant checks mirror
    // ResolveNarrowRoot so instantiated subtype edges (Foo{X}) still match their open-generic form.
    private static bool AncestorOrEqual(string ancestor, string descendant, GraphIndex index)
    {
        if (string.Equals(ancestor, descendant, StringComparison.Ordinal))
            return true;
        return Descendants(ancestor, index).Contains(descendant) || DescendantsContainStripped(ancestor, descendant, index);
    }

    // All synthetic dispatch successors of `method` (a virtual/base/interface method node), with the
    // PROVENANCE of each edge. Materialized (not lazily yielded) so the caller knows the fan-out
    // degree before emitting any edge. Deduped by target so a method reached via two mechanisms isn't
    // double-counted in the degree.
    //
    // Resolution order (exact facts first, heuristic only where Roslyn couldn't bind — flagged):
    //  1. MINED dispatch facts (Basis="roslyn"): the forward closure of the exact Roslyn-mined
    //     override/interface-impl edges from `method` (dispatch_facts; IMethodSymbol.OverriddenMethod +
    //     FindImplementationForInterfaceMember at extraction). Signature-exact and generic-correct —
    //     a same-named OVERLOAD can never be a target. Closure (not one hop) so a receiver narrowed to
    //     a grandchild type still finds the grandchild's override directly from the base method.
    //  2. Error-type simple-name recovery (Basis="heuristic", ALWAYS on): implementers whose interface
    //     edge failed to bind (`!:IFoo` — net48 partial binding) can have no mined edge by definition;
    //     recover them by simple name + arity. The single highest-recall feature — never dropped.
    //  3. Name/arity CHA fallback (Basis="heuristic"): ONLY when `method` has NO mined dispatch edges
    //     (Roslyn didn't see/bind it at extraction, or the store predates dispatch_facts) — the
    //     pre-mining interface-impl + override-descendant scan, arity-gated. ~99% correct; the CLI
    //     tells the user via the ~heuristic marker.
    //
    // `receiverType` is the static receiver type mined at the call site that reached `method` (a display
    // FQN, e.g. "MedDBase.CompanyEntity"). When it resolves to a concrete first-party type that is a
    // descendant of `method`'s declaring type, override/impl dispatch is NARROWED to that receiver's
    // own subtree — the precise CLR dispatch target set (orthogonal to WHICH member: it trims runtime
    // TYPES, the mined facts fix the member correspondence). Otherwise (null/interface/error-type/the
    // declaring base itself/an unknown type) it falls back to the full receiver-blind set.
    private static List<(string Node, string Kind, string Basis)> DispatchTargets(
        string method,
        GraphIndex index,
        string? receiverType = null,
        // Concrete generic type args in scope on the path that reached `method` (the carried binding).
        // When `method` is a GENERIC dispatch hub whose CHA fan-out includes the constructor/impl of one
        // of these types (e.g. `Construct`2.New` fanning to all entity constructors, with `Account` in
        // scope from `Entity.New<Account,…>` above), the fan-out is narrowed to that candidate. Recall-
        // safe: applied only when it leaves ≥1 target, else full CHA stands (so a hub reached without a
        // matching binding — the type flowed in by an uncaptured route — is never wrongly emptied).
        IReadOnlyCollection<string>? carriedBinding = null
    )
    {
        var targets = new List<(string Node, string Kind, string Basis)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parsed = ParseMethod(method);
        if (parsed is null)
            return targets;

        // Source method's parameter ARITY — heuristic dispatch is gated on it so an interface/base
        // call only reaches an impl/override with the MATCHING signature, not a same-named OVERLOAD.
        // A true override/impl always shares the base/interface method's arity; an overload (e.g.
        // IWorkflows.Register(int, IWorkflowController) vs ...Register(IWorkflowMaster)) does NOT, so
        // name-only matching cross-contaminated them. Arity (not full param TYPES) is the safe gate:
        // it kills the cross-arity overload collision while staying robust to generic instantiation,
        // where an override's rendered param types legitimately differ from the open-generic base
        // (`0 vs int). Same-ARITY overloads are beyond it — that's what the mined facts resolve.
        var arity = ParamArity(method);

        // Resolve the receiver to a narrowing subtree, if it is reliable. `narrowRoot` non-null means
        // dispatch restricts to {narrowRoot} ∪ descendants(narrowRoot) instead of every candidate.
        var narrowRoot = ResolveNarrowRoot(receiverType, parsed.Value.TypeId, index);

        bool InScope(string targetMethodId)
        {
            if (narrowRoot is null)
                return true;
            var targetType = ParseMethod(targetMethodId)?.TypeId;
            return targetType is not null && InNarrowSubtree(targetType, narrowRoot, index);
        }

        // 1. Mined facts: forward closure from `method`. hasMined reflects whether ANY mined edge
        // leaves the closure (pre-narrowing) — when true, the member correspondence is known exactly
        // and the CHA fallback (3) is suppressed; narrowing may still trim every target.
        var hasMined = false;
        if (index.MinedDispatchBySource.Count > 0)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { method };
            var stack = new Stack<string>();
            stack.Push(method);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!index.MinedDispatchBySource.TryGetValue(current, out var outs))
                    continue;
                foreach (var (target, kind) in outs)
                {
                    if (!visited.Add(target))
                        continue;
                    hasMined = true;
                    stack.Push(target); // walk through narrowed-out intermediates to their subtypes
                    if (InScope(target) && seen.Add(target))
                        targets.Add((target, kind == "impl" ? "impl-dispatch" : "override-dispatch", "roslyn"));
                }
            }
        }

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
                    if (
                        string.Equals(concrete.Name, parsed.Value.Name, StringComparison.Ordinal)
                        && ParamArity(concrete.SymbolId) == arity
                        && seen.Add(concrete.SymbolId)
                    )
                        targets.Add((concrete.SymbolId, "impl-dispatch", "heuristic"));
            }
        }

        // 2. Simple-name fallback (always on): recover dispatch to implementers whose interface edge
        // failed to resolve (!:IFoo) by matching the call's interface simple name. Method-name-gated;
        // only consults error-type edges, so the blast radius is the partial-binding cases that would
        // otherwise be invisible — Roslyn never bound these, so no mined edge can cover them. (May
        // slightly over-dispatch across same-named interfaces in different namespaces — a deliberate
        // recall-over-precision trade for broken bindings.) Deduped against the mined set via `seen`.
        if (index.ImplsByErrorInterfaceName.TryGetValue(SimpleTypeName(parsed.Value.TypeId), out var nameImpls))
            AddImplMethods(nameImpls);

        // 3. Name/arity CHA fallback — only when the member has NO mined dispatch edge (residual
        // binding gaps + stores without dispatch_facts). Same scan as before mining existed.
        if (!hasMined)
        {
            // Interface -> concrete DI dispatch.
            if (index.ImplsByInterface.TryGetValue(parsed.Value.TypeId, out var impls))
                AddImplMethods(impls);

            // Base-virtual/abstract -> override dispatch (G6/G3): a call resolved to a base-type method
            // also reaches the SAME-named OVERRIDE on every (transitive) subtype. This is what makes an
            // abstract [ClientAction] (or framework virtual like OnSave) reach the effects in its
            // concrete override. Gated on IsOverride so it doesn't dispatch to unrelated same-named
            // (hidden) methods. When the receiver narrows, scan only the receiver's subtree (its own
            // override + its subtypes') instead of every descendant of the declaring base.
            var subtree = narrowRoot is not null ? NarrowSubtree(narrowRoot, index) : Descendants(parsed.Value.TypeId, index);
            foreach (var sub in subtree)
            {
                if (!index.MethodsByStrippedType.TryGetValue(TypeClosure.StripGeneric(sub), out var subMethods))
                    continue;
                foreach (var m in subMethods)
                    if (
                        m.IsOverride
                        && string.Equals(m.Name, parsed.Value.Name, StringComparison.Ordinal)
                        && ParamArity(m.SymbolId) == arity
                        && seen.Add(m.SymbolId)
                    )
                        targets.Add((m.SymbolId, "override-dispatch", "heuristic"));
            }
        }

        return NarrowByTypeArguments(targets, carriedBinding, index);
    }

    // Generic-dispatch narrowing (monomorphization): given a fanned-out candidate set and the concrete
    // type arguments in scope on the path, keep only candidates whose DECLARING TYPE is one of those
    // concretes (or a subtype) — `Construct`2.New` fanned to 43 entity constructors collapses to
    // `Account.New` when `Account` is the carried type arg from `Entity.New<Account,…>` above. The
    // concrete entity that the open generic `TConstruct` is bound to on this path picks its own
    // constructor out of the CHA over-approximation. Recall-safe: only applied when it keeps ≥1 target
    // (an unrelated/empty binding leaves the full set), and only when narrowing is enabled. Other carried
    // concretes (the pk type `int`, the record type) match no candidate and harmlessly drop out.
    private static List<(string Node, string Kind, string Basis)> NarrowByTypeArguments(
        List<(string Node, string Kind, string Basis)> targets,
        IReadOnlyCollection<string>? carriedBinding,
        GraphIndex index
    )
    {
        if (!index.NarrowDispatch || carriedBinding is not { Count: > 0 } || targets.Count <= 1)
            return targets;

        var roots = new List<string>();
        foreach (var t in carriedBinding)
            if (ReceiverToStrippedTypeId(t) is { } root)
                roots.Add(root);
        if (roots.Count == 0)
            return targets;

        var narrowed = new List<(string Node, string Kind, string Basis)>();
        foreach (var t in targets)
        {
            var declType = ParseMethod(t.Node)?.TypeId;
            if (declType is not null && roots.Any(r => InNarrowSubtree(declType, r, index)))
                narrowed.Add(t);
        }
        return narrowed.Count > 0 ? narrowed : targets;
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
            yield return (node, target.Node, target.Kind, target.Basis);
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
            return;
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
                    node,
                    link?.Kind ?? "entry",
                    link?.File,
                    link?.Line ?? 0,
                    link?.LoopKind,
                    link?.LoopDetail,
                    link?.Fanout ?? 0,
                    link?.HandoffVia,
                    link?.Basis
                )
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

    // Parameter ARITY of a method DocID: the number of top-level parameters in its "(...)" list, or 0
    // when there is none ("M:T.M" / "M:T.M()"). Commas inside generic-argument braces "{...}" or array
    // brackets "[...]" don't count (e.g. "Func{A,B,C}" is ONE parameter). Used to stop name-only
    // interface/override dispatch from matching a same-named OVERLOAD with a different signature.
    private static int ParamArity(string docId)
    {
        var open = docId.IndexOf('(');
        if (open < 0)
            return 0;
        var close = docId.LastIndexOf(')');
        if (close <= open + 1)
            return 0; // "()" — no parameters
        var count = 1;
        var depth = 0;
        for (var i = open + 1; i < close; i++)
        {
            var c = docId[i];
            if (c is '{' or '[' or '(')
                depth++;
            else if (c is '}' or ']' or ')')
                depth--;
            else if (c == ',' && depth == 0)
                count++;
        }
        return count;
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

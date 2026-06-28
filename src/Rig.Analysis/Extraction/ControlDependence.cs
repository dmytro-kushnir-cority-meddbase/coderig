using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace Rig.Analysis.Extraction;

// Intra-procedural control-dependence over a Roslyn ControlFlowGraph (branch-aware-effects, milestones 1-3).
//
// Sound and SUGAR-PROOF: the CFG lowers if / else / switch / switch-expression / ?: / ?. / && / || into a
// uniform branch-block shape, so every form of conditional execution is handled by one mechanism (this is
// the whole reason the syntactic-ancestor proxy was rejected — it has no `if` node to find for `a?.X()`).
//
// The algorithms here are reachability-based ("delete-test"): a block dominates the Exit iff deleting it
// disconnects Entry from Exit. Obviously correct, BCL-portable, allocation-simple, and adequate for the
// SMALL per-method CFGs we build (handfuls-to-dozens of blocks). A faster dominator-tree port (CHK /
// Lengauer-Tarjan) is gated on a benchmark — see docs/backlog/todo/branch-aware-effects.md; the public
// surface here is algorithm-agnostic so the internals can be swapped without touching callers.
//
// Stage placement: this is STAGE 1 (Roslyn-facing). It computes per-call-site INTRA-procedural facts only.
// "Always-runs-from-the-entry-point" is the inter-procedural composition the derive layer does over the
// call graph; it is deliberately NOT computed here.
internal static class ControlDependence
{
    // A predicate that gates a guarded block: the forking block's ordinal, the human-readable condition
    // text (the `if`/switch/`?.` source), and the polarity under which control flows toward the block —
    // WhenTrue = the guarded effect runs when this predicate evaluates true.
    internal readonly record struct ControlGuard(int BranchBlock, string Predicate, bool WhenTrue);

    // The CFG block whose operation subtree contains `target` (exact syntax-node match), or -1. The
    // BranchValue (the condition operation) is scanned separately because it is NOT in block.Operations.
    // Matching the INVOCATION node (not its enclosing statement) means `a?.Save()` resolves to the block
    // holding the call, not the null-check block it was lowered out of.
    internal static int BlockOf(ControlFlowGraph cfg, SyntaxNode target)
    {
        foreach (var block in cfg.Blocks)
        {
            foreach (var op in block.Operations)
            {
                if (ContainsSyntax(op, target))
                {
                    return block.Ordinal;
                }
            }

            if (block.BranchValue is { } branchValue && ContainsSyntax(branchValue, target))
            {
                return block.Ordinal;
            }
        }

        return -1;
    }

    // Blocks executed on EVERY Entry->Exit path — the must-run spine. A block is must-run iff it
    // DOMINATES the Exit (every Entry->Exit path passes through it), i.e. it lies on the Exit's chain of
    // immediate dominators. We compute the dominator tree ONCE (Cooper-Harvey-Kennedy, "A Simple, Fast
    // Dominance Algorithm", 2001) into int[] buffers, then walk idom from Exit up to Entry.
    //
    // PERF: this replaced an O(V^2 * E) reachability "delete-test" whose per-query HashSet+Stack churn was
    // catastrophic — a 258-block method allocated ~509 MB / ~128 ms (see the benchmark). The dominator tree
    // is ~O(V * E) worst case, near-linear in practice, with O(V) allocation per CFG and ZERO per query.
    // The delete-test survives as the differential test oracle (obviously-correct, validates this).
    // BACKTRACK: if a CHK edge case is ever suspected, the oracle in ControlDependenceTests is ground truth.
    internal static HashSet<int> MustRunBlocks(ControlFlowGraph cfg)
    {
        var n = cfg.Blocks.Length;
        const int entry = 0; // Roslyn invariant: Blocks[0] == Entry, Blocks[^1] == Exit
        var exit = n - 1;

        // 1) Reverse-postorder from Entry via ITERATIVE DFS (no recursion — safe on deep block chains).
        var postorder = new List<int>(n);
        var state = new byte[n]; // 0 = unvisited, 1 = on-stack/expanded, 2 = emitted
        var dfs = new Stack<int>();
        dfs.Push(entry);
        while (dfs.Count > 0)
        {
            var b = dfs.Peek();
            if (state[b] == 0)
            {
                state[b] = 1;
                PushSuccessor(cfg.Blocks[b].ConditionalSuccessor?.Destination?.Ordinal);
                PushSuccessor(cfg.Blocks[b].FallThroughSuccessor?.Destination?.Ordinal);
            }
            else
            {
                dfs.Pop();
                if (state[b] == 1)
                {
                    state[b] = 2;
                    postorder.Add(b);
                }
            }

            void PushSuccessor(int? s)
            {
                if (s is int next && state[next] == 0)
                {
                    dfs.Push(next);
                }
            }
        }

        // rpoIndex[ordinal] = position in reverse-postorder; -1 if unreachable from Entry.
        var rpoIndex = new int[n];
        Array.Fill(rpoIndex, -1);
        var rpo = new int[postorder.Count];
        for (var i = 0; i < postorder.Count; i++)
        {
            var ordinal = postorder[postorder.Count - 1 - i]; // reverse
            rpo[i] = ordinal;
            rpoIndex[ordinal] = i;
        }

        // If Exit is unreachable from Entry (e.g. an infinite loop with no normal completion), there is no
        // must-run spine — nothing runs "on every Entry->Exit path" because there are none.
        if (rpoIndex[exit] < 0)
        {
            return [];
        }

        // 2) Predecessor lists (only reachable blocks matter).
        var preds = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            preds[i] = [];
        }

        foreach (var block in cfg.Blocks)
        {
            if (rpoIndex[block.Ordinal] < 0)
            {
                continue;
            }

            AddPred(block.ConditionalSuccessor?.Destination?.Ordinal, block.Ordinal);
            AddPred(block.FallThroughSuccessor?.Destination?.Ordinal, block.Ordinal);
        }

        void AddPred(int? target, int from)
        {
            if (target is int t)
            {
                preds[t].Add(from);
            }
        }

        // 3) CHK fixpoint over reverse-postorder.
        var idom = new int[n];
        Array.Fill(idom, -1);
        idom[entry] = entry;

        bool changed;
        do
        {
            changed = false;
            for (var i = 1; i < rpo.Length; i++) // skip Entry (rpo[0])
            {
                var b = rpo[i];
                var newIdom = -1;
                foreach (var p in preds[b])
                {
                    if (idom[p] < 0)
                    {
                        continue; // predecessor not yet processed
                    }

                    newIdom = newIdom < 0 ? p : Intersect(p, newIdom, idom, rpoIndex);
                }

                if (newIdom >= 0 && idom[b] != newIdom)
                {
                    idom[b] = newIdom;
                    changed = true;
                }
            }
        } while (changed);

        // 4) must-run = the Exit's chain of dominators (every block that dominates Exit), Entry..Exit.
        var mustRun = new HashSet<int>();
        var cur = exit;
        while (cur >= 0)
        {
            mustRun.Add(cur);
            if (cur == entry)
            {
                break;
            }

            cur = idom[cur];
        }

        return mustRun;
    }

    // CHK "intersect": the nearest common dominator of two blocks, walking the two fingers up the
    // partially-built idom tree by reverse-postorder number (higher rpoIndex = further from Entry).
    private static int Intersect(int a, int b, int[] idom, int[] rpoIndex)
    {
        while (a != b)
        {
            while (rpoIndex[a] > rpoIndex[b])
            {
                a = idom[a];
            }

            while (rpoIndex[b] > rpoIndex[a])
            {
                b = idom[b];
            }
        }

        return a;
    }

    // The control-dependence (guard) set of `block` — the branch predicates that decide whether it runs;
    // EMPTY iff the block is must-run (or unreachable). Convenience wrapper: production callers (extraction)
    // should call ComputeGuards ONCE per CFG and index the result — calling this per-block recomputes the
    // post-dominator tree each time.
    internal static IReadOnlyList<ControlGuard> GuardsOf(ControlFlowGraph cfg, int block) => ComputeGuards(cfg)[block];

    // Control dependence for EVERY block, computed ONCE. Ferrante-Ottenstein-Warren over the POST-dominator
    // tree: a node N is control-dependent on a branch A (taking the edge toward successor B) for every N on
    // the post-dom-tree path from B up to — but excluding — ipdom(A). This is the dual of MustRunBlocks: the
    // post-dominator tree is the dominator tree of the REVERSED CFG rooted at the Exit, built with the same
    // CHK machinery — O(V) allocation, computed once, NO per-query reachability.
    //
    // PERF / BACKTRACK: this replaced an O(V^2*E) delete-test (a reachability walk per fork-edge per block)
    // that allocated MB-GB per method (see the benchmark). The delete-test survives as the differential test
    // ORACLE (NaiveGuards in ControlDependenceTests) — it is ground truth if a CHK edge case is suspected.
    // Blocks that cannot reach the Exit (throw arms, infinite loops) are absent from the reversed traversal,
    // so they're excluded — matching the normal-completion model MustRunBlocks uses.
    internal static IReadOnlyList<ControlGuard>[] ComputeGuards(ControlFlowGraph cfg)
    {
        var n = cfg.Blocks.Length;
        var exit = n - 1; // Roslyn invariant: Blocks[^1] == Exit

        var guards = new List<ControlGuard>[n];
        for (var i = 0; i < n; i++)
        {
            guards[i] = [];
        }

        var ipdom = PostDominatorTree(cfg, out var rpoIndexR);
        if (rpoIndexR[exit] < 0)
        {
            return guards; // Exit unreachable -> no normal completion -> no guards
        }

        foreach (var a in cfg.Blocks)
        {
            var conditional = a.ConditionalSuccessor?.Destination?.Ordinal;
            var fallThrough = a.FallThroughSuccessor?.Destination?.Ordinal;

            // Only a forking block can gate anything, and it must itself be able to reach the Exit.
            if (a.BranchValue is not { } predicate || conditional is null || fallThrough is null || rpoIndexR[a.Ordinal] < 0)
            {
                continue;
            }

            var predicateText = predicate.Syntax.ToString();
            var aOrdinal = a.Ordinal;
            var stop = ipdom[aOrdinal]; // walk each edge's region up to (excluding) A's immediate post-dominator

            MarkRegion(conditional.Value, a.ConditionKind == ControlFlowConditionKind.WhenTrue);
            MarkRegion(fallThrough.Value, a.ConditionKind == ControlFlowConditionKind.WhenFalse);

            // Mark every node on the post-dom path B..stop (exclusive) as control-dependent on (A, polarity).
            void MarkRegion(int b, bool whenTrue)
            {
                if (rpoIndexR[b] < 0 || PostDominatesInTree(b, aOrdinal, ipdom))
                {
                    return; // abnormal-termination edge, or B post-dominates A (A doesn't gate it)
                }

                var node = b;
                var safety = n + 1; // the path is bounded by the tree height; this guards against surprises
                while (node != stop && node >= 0 && safety-- > 0)
                {
                    // Skip the branch block gating ITSELF: a loop-condition block is on the post-dom path of
                    // its own body (via the back-edge), so FOW would mark it control-dependent on itself.
                    // That's textbook PDG (a loop header depends on its own predicate) but useless for
                    // effect-guarding AND wrong for our model — a loop-condition block is MUST-RUN (every
                    // path checks it to exit), so its guard set must be empty (the must-run<=>no-guards
                    // invariant). A block does not meaningfully guard itself.
                    if (node != aOrdinal)
                    {
                        guards[node].Add(new ControlGuard(aOrdinal, predicateText, whenTrue));
                    }

                    var next = ipdom[node];
                    if (next == node)
                    {
                        break; // reached the tree root (Exit)
                    }

                    node = next;
                }
            }
        }

        return guards;
    }

    // CHK dominator tree of the REVERSED CFG rooted at the Exit = the POST-dominator tree. The returned
    // idom[x] is the immediate POST-dominator of x; rpoIndexR[x] < 0 means x cannot reach the Exit. In the
    // reversed graph a node's successors are its ORIGINAL predecessors (used for the DFS/RPO) and its
    // predecessors are its ORIGINAL successors (used in the CHK fixpoint).
    private static int[] PostDominatorTree(ControlFlowGraph cfg, out int[] rpoIndexR)
    {
        var n = cfg.Blocks.Length;
        var exit = n - 1;

        var preds = new List<int>[n]; // original predecessors  == reversed-graph successors
        var succs = new List<int>[n]; // original successors    == reversed-graph predecessors
        for (var i = 0; i < n; i++)
        {
            preds[i] = [];
            succs[i] = [];
        }

        foreach (var b in cfg.Blocks)
        {
            AddEdge(b.Ordinal, b.ConditionalSuccessor?.Destination?.Ordinal);
            AddEdge(b.Ordinal, b.FallThroughSuccessor?.Destination?.Ordinal);
        }

        void AddEdge(int from, int? to)
        {
            if (to is int t)
            {
                succs[from].Add(t);
                preds[t].Add(from);
            }
        }

        // Reverse-postorder of the reversed graph: iterative DFS from Exit over reversed-successors (preds).
        var postorder = new List<int>(n);
        var state = new byte[n];
        var dfs = new Stack<int>();
        dfs.Push(exit);
        while (dfs.Count > 0)
        {
            var b = dfs.Peek();
            if (state[b] == 0)
            {
                state[b] = 1;
                foreach (var p in preds[b])
                {
                    if (state[p] == 0)
                    {
                        dfs.Push(p);
                    }
                }
            }
            else
            {
                dfs.Pop();
                if (state[b] == 1)
                {
                    state[b] = 2;
                    postorder.Add(b);
                }
            }
        }

        rpoIndexR = new int[n];
        Array.Fill(rpoIndexR, -1);
        var rpo = new int[postorder.Count];
        for (var i = 0; i < postorder.Count; i++)
        {
            var ordinal = postorder[postorder.Count - 1 - i]; // reverse
            rpo[i] = ordinal;
            rpoIndexR[ordinal] = i;
        }

        var idom = new int[n];
        Array.Fill(idom, -1);
        idom[exit] = exit;

        bool changed;
        do
        {
            changed = false;
            for (var i = 1; i < rpo.Length; i++) // skip Exit (rpo[0], the reversed-graph root)
            {
                var b = rpo[i];
                var newIdom = -1;
                foreach (var p in succs[b]) // reversed-graph predecessors == original successors
                {
                    if (rpoIndexR[p] < 0 || idom[p] < 0)
                    {
                        continue;
                    }

                    newIdom = newIdom < 0 ? p : Intersect(p, newIdom, idom, rpoIndexR);
                }

                if (newIdom >= 0 && idom[b] != newIdom)
                {
                    idom[b] = newIdom;
                    changed = true;
                }
            }
        } while (changed);

        return idom;
    }

    // B post-dominates A iff B is an ancestor of A in the post-dominator tree (walk A's ipdom chain to root).
    private static bool PostDominatesInTree(int b, int a, int[] ipdom)
    {
        if (b == a)
        {
            return true;
        }

        var x = a;
        while (ipdom[x] >= 0 && ipdom[x] != x)
        {
            x = ipdom[x];
            if (x == b)
            {
                return true;
            }
        }

        return false;
    }

    // Iterative walk of an IOperation subtree: does any node's syntax equal `target`?
    private static bool ContainsSyntax(IOperation root, SyntaxNode target)
    {
        var stack = new Stack<IOperation>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Syntax == target)
            {
                return true;
            }

            foreach (var child in current.ChildOperations)
            {
                stack.Push(child);
            }
        }

        return false;
    }
}

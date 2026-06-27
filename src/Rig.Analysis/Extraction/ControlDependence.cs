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

    // The control-dependence (guard) set of `block`: the branch predicates that decide whether it runs.
    // EMPTY iff the block is must-run (or unreachable). Ferrante-Ottenstein-Warren: a block B is
    // control-dependent on a CFG edge A->t iff B post-dominates t but B does NOT post-dominate A. We test
    // both out-edges of every forking block A, recording the polarity of the edge that leads toward B.
    internal static IReadOnlyList<ControlGuard> GuardsOf(ControlFlowGraph cfg, int block)
    {
        var guards = new List<ControlGuard>();

        foreach (var a in cfg.Blocks)
        {
            var conditional = a.ConditionalSuccessor?.Destination;
            var fallThrough = a.FallThroughSuccessor?.Destination;

            // Only a forking block (two distinct out-edges with a branch value) gates anything.
            if (a.BranchValue is not { } predicate || conditional is null || fallThrough is null)
            {
                continue;
            }

            // (FOW condition 2) if B post-dominates A, B runs no matter which edge A takes -> A is not a guard.
            if (PostDominates(cfg, x: block, y: a.Ordinal))
            {
                continue;
            }

            // An out-edge that leads ONLY to abnormal termination — a block with no path to the Exit (a
            // throw arm, e.g. a switch-expression's synthetic no-match throw; or an infinite loop) — is not
            // a guard in the normal-completion model. Gating on CanReachExit also avoids VACUOUS
            // post-dominance: "X post-dominates a block that can't reach Exit" is trivially true (no paths
            // exist), which otherwise pins a spurious guard on every upstream block. This keeps GuardsOf
            // consistent with MustRunBlocks, which likewise counts only paths that actually reach the Exit.
            // (Surfaced by the switch-expression no-match arm in the sugar fixture.)

            // (FOW condition 1) the conditional edge: taken when BranchValue == (ConditionKind == WhenTrue).
            if (CanReachExit(cfg, conditional.Ordinal) && PostDominates(cfg, x: block, y: conditional.Ordinal))
            {
                guards.Add(new ControlGuard(a.Ordinal, predicate.Syntax.ToString(), a.ConditionKind == ControlFlowConditionKind.WhenTrue));
                continue;
            }

            // the fall-through edge: taken when the conditional edge is NOT -> the complementary polarity.
            if (CanReachExit(cfg, fallThrough.Ordinal) && PostDominates(cfg, x: block, y: fallThrough.Ordinal))
            {
                guards.Add(new ControlGuard(a.Ordinal, predicate.Syntax.ToString(), a.ConditionKind == ControlFlowConditionKind.WhenFalse));
            }
        }

        return guards;
    }

    // Can `b` reach the Exit at all? A block that cannot (a throw arm, an infinite loop) terminates
    // abnormally and has no normal-completion path. PERF: this is a full reachability walk; GuardsOf calls
    // it per fork-edge, so the delete-test path is O(V^2 * E)-ish. Fine for the small per-method CFGs and
    // as the test oracle — the production hot path uses the dominator/post-dominator TREE (computed once
    // per CFG) instead. // BACKTRACK: if the tree rewrite regresses, this delete-test is the reference.
    private static bool CanReachExit(ControlFlowGraph cfg, int b) => Reachable(cfg, start: b, target: cfg.Blocks[^1].Ordinal, excluded: -1);

    // X post-dominates Y iff every path Y->Exit passes through X (delete X; can Y still reach Exit?).
    private static bool PostDominates(ControlFlowGraph cfg, int x, int y)
    {
        if (x == y)
        {
            return true;
        }

        var exit = cfg.Blocks[^1].Ordinal;
        return !Reachable(cfg, start: y, target: exit, excluded: x);
    }

    // Can `start` reach `target` without ever entering `excluded`? Iterative DFS (no recursion — safe on
    // the long straight-line block chains that generated code produces).
    private static bool Reachable(ControlFlowGraph cfg, int start, int target, int excluded)
    {
        if (start == excluded)
        {
            return false;
        }

        if (start == target)
        {
            return true;
        }

        var seen = new HashSet<int> { start };
        var stack = new Stack<int>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            var block = cfg.Blocks[stack.Pop()];
            if (Step(block.ConditionalSuccessor?.Destination?.Ordinal) || Step(block.FallThroughSuccessor?.Destination?.Ordinal))
            {
                return true;
            }
        }

        return false;

        // Visit one out-edge; returns true the moment `target` is hit.
        bool Step(int? destination)
        {
            if (destination is not int next || next == excluded)
            {
                return false;
            }

            if (next == target)
            {
                return true;
            }

            if (seen.Add(next))
            {
                stack.Push(next);
            }

            return false;
        }
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

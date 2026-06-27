using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Shouldly;

namespace Rig.Tests.Analysis;

// Throwaway learning spike for the branch-aware-effects work (docs/backlog/todo/branch-aware-effects.md).
// Goal: SEE a real Roslyn Control Flow Graph and watch it get the guard-clause case right where the
// flat syntax tree got it wrong. Not a real test — it asserts almost nothing; the payoff is the dump.
// Delete once we've internalized the model.
public sealed class CfgSpike
{
    // ── BOILERPLATE: snippet text → SemanticModel (lifted from FactExtractorCaptureTests.Extract) ──
    private static (SyntaxTree tree, SemanticModel model) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Snippet.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "Snippet",
            syntaxTrees: [tree],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        return (tree, compilation.GetSemanticModel(tree));
    }

    [Test]
    public void Dump_cfg_of_guard_clause_method()
    {
        // The method that fooled the syntactic "walk the ancestors" rule: Save(a) is a clean sibling
        // in the body block (no `if` above it), yet it only runs when a != null. The CFG should show
        // that Save(a)'s block is NOT reached on every path from Entry.
        var source =
            // lang=cs
            """
            public sealed class Account { }

            public sealed class C
            {
                public void M1(Account a)
                {
                    if (a == null) return;
                    Save(a);
                    
                    switch (Counter(a))
                    {
                        case -1: return;
                        case 2: 
                        {
                            Console.WriteLine("CATCH");
                            break;
                        }
                        case 3: 
                        {
                            throw new NotImplementedException();
                        }
                        case 5:
                            Console.WriteLine("CATCH");
                        default: break;
                    }
                    
                    Save(a);
                }
                
                public void M2(bool cond)
                {
                    if (cond) A(); else B();
                    C();
                }
                private void A() {}
                private void B() {}
                private void C() {}

                private void Save(Account a) { }
                
                private int Counter(Account a){ return 10 }
            }
            """;

        var (tree, model) = Compile(source);

        // 1) syntax: grab the method M
        var methods = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>();

        var method = methods
            .Single(m => m.Identifier.Text == "M1");

        // 2) syntax → IOperation (layer 3a). GetOperation on a method-with-body yields an
        //    IMethodBodyOperation; on a bare block it yields an IBlockOperation. Both can seed a CFG.
        var operation = model.GetOperation(method);
        Console.WriteLine($"GetOperation({method.Identifier.Text}) → {operation?.GetType().Name ?? "null"}\n");

        // 3) IOperation → ControlFlowGraph (layer 3b).
        var cfg = operation switch
        {
            IMethodBodyOperation methodBody => ControlFlowGraph.Create(methodBody),
            _ => null,
        };

        cfg.ShouldNotBeNull("expected a CFG for M — if null, check what GetOperation returned above");

        // 4) dump every basic block + its edges. Read these like a graph:
        //    - Kind: Entry / Block / Exit
        //    - op:   the statements that live in this block (straight-line, no branches inside)
        //    - branch-on: the value this block forks on (a condition, or a return/throw value)
        //    - ──cond──▶ : where control goes when the branch condition holds (ConditionKind)
        //    - ──fall──▶ : where control goes otherwise (the fall-through edge)
        Console.WriteLine($"{cfg!.Blocks.Length} blocks:\n");
        foreach (var block in cfg.Blocks)
        {
            Console.WriteLine($"  [#{block.Ordinal}] {block.Kind}");

            foreach (var op in block.Operations)
            {
                Console.WriteLine($"        op: {op.Kind} «{Trim(op.Syntax.ToString())}»");
            }

            if (block.IsReachable)
            {
                if (block.BranchValue is { } branchValue)
                {
                    Console.WriteLine($"        branch-on: «{Trim(branchValue.Syntax.ToString())}»  (when {block.ConditionKind})");
                }

                var cond = block.ConditionalSuccessor?.Destination?.Ordinal;
                var fall = block.FallThroughSuccessor?.Destination?.Ordinal;

                if (cond is not null)
                {
                    Console.WriteLine($"        ──cond──▶ #{cond}");
                }

                if (fall is not null)
                {
                    Console.WriteLine($"        ──fall──▶ #{fall}");
                }
                else
                {
                    Console.WriteLine(block.FallThroughSuccessor);
                }
            }
            else
            {
                Console.WriteLine($"        unreachable: {block.Kind}");
            }

            Console.WriteLine();
        }
    }

    private static string Trim(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    // ─────────────────────────────────────────────────────────────────────────────
    // PHASE 3: must-run via the naive delete-test.
    //   must-run(B)  ⟺  deleting B makes Exit unreachable from Entry.
    // ─────────────────────────────────────────────────────────────────────────────

    // ◀ YOUR TODO #1 — the core. Return the ordinals of every must-run block.
    //   For each block b in cfg.Blocks: it's must-run iff ExitReachable(cfg, excluded: b.Ordinal)
    //   is FALSE (removing it strands the Exit). Collect those ordinals.

    private record struct Node(int Fall, int Cond);

    private static HashSet<int> MustRun_N_N2(ControlFlowGraph cfg)
    {
        Node[] nodes = new Node[cfg.Blocks.Length];

        foreach (var block in cfg.Blocks)
        {
            var Fall = block.FallThroughSuccessor?.Destination?.Ordinal ?? -1;
            var Cond = block.ConditionalSuccessor?.Destination?.Ordinal ?? -1;
            nodes[block.Ordinal] = new Node(Fall, Cond);
        }

        var result = new HashSet<int>();
        var visited = new HashSet<int>();

        for (int i = 1; i < nodes.Length - 1; i++)
        {
            visited.Clear();

            Go(0, i, visited);

            void Go(int start, int excluded, HashSet<int> visited)
            {
                if (start < 0 || start == excluded)
                    return;

                if (!visited.Add(start))
                    return;

                Go(nodes[start].Fall, excluded, visited);
                Go(nodes[start].Cond, excluded, visited);
            }

            if (!visited.Contains(nodes.Length - 1))
                result.Add(i);
        }

        result.Add(0);
        result.Add(nodes.Length - 1);

        return result;
    }

    private static HashSet<int> MustRun_N2(ControlFlowGraph cfg)
    {
        var result = new HashSet<int>();
        var visited = new HashSet<int>();

        for (int i = 1; i < cfg.Blocks.Length - 1; i++)
        {
            visited.Clear();

            Go(0, i, visited);

            void Go(int start, int excluded, HashSet<int> visited)
            {
                if (start < 0 || start == excluded)
                    return;

                if (!visited.Add(start))
                    return;

                var block = cfg.Blocks[start];

                var Fall = block.FallThroughSuccessor?.Destination?.Ordinal ?? -1;
                var Cond = block.ConditionalSuccessor?.Destination?.Ordinal ?? -1;

                Go(Fall, excluded, visited);
                Go(Cond, excluded, visited);
            }

            if (!visited.Contains(cfg.Blocks.Length - 1))
                result.Add(i);
        }

        result.Add(0);
        result.Add(cfg.Blocks.Length - 1);

        return result;
    }

    private static HashSet<int> MustRun_CHK(ControlFlowGraph cfg)
    {
        int n = cfg.Blocks.Length;

        // Step 1: reverse postorder via DFS
        var rpo = new List<int>(n);
        var visited = new bool[n];

        void Dfs(int b)
        {
            visited[b] = true;
            var block = cfg.Blocks[b];
            var fall = block.FallThroughSuccessor?.Destination?.Ordinal ?? -1;
            var cond = block.ConditionalSuccessor?.Destination?.Ordinal ?? -1;
            if (fall >= 0 && !visited[fall]) Dfs(fall);
            if (cond >= 0 && !visited[cond]) Dfs(cond);
            rpo.Add(b);
        }

        Dfs(0);
        rpo.Reverse();

        var rpoNum = new int[n];
        for (int i = 0; i < rpo.Count; i++) rpoNum[rpo[i]] = i;

        // Step 2: predecessor lists
        var preds = new List<int>[n];
        for (int i = 0; i < n; i++) preds[i] = new List<int>();
        foreach (var block in cfg.Blocks)
        {
            var fall = block.FallThroughSuccessor?.Destination?.Ordinal ?? -1;
            var cond = block.ConditionalSuccessor?.Destination?.Ordinal ?? -1;
            if (fall >= 0) preds[fall].Add(block.Ordinal);
            if (cond >= 0) preds[cond].Add(block.Ordinal);
        }

        // Step 3: Cooper/Harvey/Kennedy 2001
        var idom = new int[n];
        Array.Fill(idom, -1);
        idom[0] = 0;

        int Intersect(int b1, int b2)
        {
            while (b1 != b2)
            {
                while (rpoNum[b1] > rpoNum[b2]) b1 = idom[b1];
                while (rpoNum[b2] > rpoNum[b1]) b2 = idom[b2];
            }

            return b1;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 1; i < rpo.Count; i++)
            {
                int b = rpo[i];
                int newIdom = -1;
                foreach (int p in preds[b])
                {
                    if (idom[p] == -1) continue;
                    newIdom = newIdom == -1 ? p : Intersect(newIdom, p);
                }

                if (newIdom != -1 && idom[b] != newIdom)
                {
                    idom[b] = newIdom;
                    changed = true;
                }
            }
        }

        // Step 4: Dom(exit) = walk idom chain from exit to entry
        var result = new HashSet<int>();
        int cur = n - 1;
        while (cur != 0)
        {
            result.Add(cur);
            cur = idom[cur];
        }

        result.Add(0);
        return result;
    }

    // ── PROVIDED (API plumbing) — a block's outgoing edges, as destination blocks. ──
    // A block forks via at most two branches: the conditional edge and the fall-through edge.
    // Exit has neither (both null) → no successors.
    private static IEnumerable<BasicBlock> Successors(BasicBlock b)
    {
        if (b.ConditionalSuccessor?.Destination is { } cond)
        {
            yield return cond;
        }

        if (b.FallThroughSuccessor?.Destination is { } fall)
        {
            yield return fall;
        }
    }

    // ── PROVIDED (plumbing) — source + method name → its CFG. ──
    private static ControlFlowGraph BuildCfg(string source, string methodName)
    {
        var (tree, model) = Compile(source);
        var method = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == methodName);
        var op = model.GetOperation(method);
        return op switch
        {
            IMethodBodyOperation mb => ControlFlowGraph.Create(mb),
            IBlockOperation blk => ControlFlowGraph.Create(blk),
            _ => throw new InvalidOperationException($"no CFG for {methodName}"),
        };
    }

// ── the red target: turn these green ──
    [Test]
    public void MustRun_excludes_both_arms_keeps_the_merge()
    {
        // if (cond) A(); else B();  C();   →  C is must-run, A and B are not.
        var cfg = BuildCfg("""
                           public sealed class C
                           {
                               public void M(bool cond)
                               {
                                   if (cond) A(); else B();
                                   C();
                                   
                                   for (int i = 0; i < 100; i++)
                                    C();
                               }
                               private void A() {} private void B() {} private void C() {}
                           }
                           """, "M");

        // #0 Entry, #1 branch, #2 A, #3 B, #4 C(merge), #5 Exit
        MustRun_CHK(cfg).ShouldBe([0, 1, 4, 5, 6, 8], ignoreOrder: true);
    }

    [Test]
    public void MustRun_only_the_first_guard_in_the_double_return()
    {
        var cfg = BuildCfg("""
                           public sealed class Account { }
                           public sealed class C
                           {
                               public void M(Account a)
                               {
                                   if (a == null) return;
                                   if (a != null) return;
                                   Save(a);
                               }
                               private void Save(Account a) {}
                           }
                           """, "M");

        // #0 Entry, #1 first guard, #2 second guard, #3 Save, #4 Exit
        MustRun_CHK(cfg).ShouldBe([0, 1, 4], ignoreOrder: true);
    }
    
    [Test]
    public void BIG()
    {
        var cfg = BuildCfg("""
                           
                           public sealed class C
                           {
                              public static HashSet<int> M(ControlFlowGraph cfg)
                              {
                                  int n = cfg.Blocks.Length;
                           
                                  // Step 1: reverse postorder via DFS
                                  var rpo = new List<int>(n);
                                  var visited = new bool[n];
                           
                                  void Dfs(int b)
                                  {
                                      visited[b] = true;
                                      var block = cfg.Blocks[b];
                                      var fall = block.FallThroughSuccessor?.Destination?.Ordinal ?? -1;
                                      var cond = block.ConditionalSuccessor?.Destination?.Ordinal ?? -1;
                                      if (fall >= 0 && !visited[fall]) Dfs(fall);
                                      if (cond >= 0 && !visited[cond]) Dfs(cond);
                                      rpo.Add(b);
                                  }
                           
                                  Dfs(0);
                                  rpo.Reverse();
                           
                                  var rpoNum = new int[n];
                                  for (int i = 0; i < rpo.Count; i++) rpoNum[rpo[i]] = i;
                           
                                  // Step 2: predecessor lists
                                  var preds = new List<int>[n];
                                  for (int i = 0; i < n; i++) preds[i] = new List<int>();
                                  foreach (var block in cfg.Blocks)
                                  {
                                      var fall = block.FallThroughSuccessor?.Destination?.Ordinal ?? -1;
                                      var cond = block.ConditionalSuccessor?.Destination?.Ordinal ?? -1;
                                      if (fall >= 0) preds[fall].Add(block.Ordinal);
                                      if (cond >= 0) preds[cond].Add(block.Ordinal);
                                  }
                           
                                  // Step 3: Cooper/Harvey/Kennedy 2001
                                  var idom = new int[n];
                                  Array.Fill(idom, -1);
                                  idom[0] = 0;
                           
                                  int Intersect(int b1, int b2)
                                  {
                                      while (b1 != b2)
                                      {
                                          while (rpoNum[b1] > rpoNum[b2]) b1 = idom[b1];
                                          while (rpoNum[b2] > rpoNum[b1]) b2 = idom[b2];
                                      }
                           
                                      return b1;
                                  }
                           
                                  bool changed = true;
                                  while (changed)
                                  {
                                      changed = false;
                                      for (int i = 1; i < rpo.Count; i++)
                                      {
                                          int b = rpo[i];
                                          int newIdom = -1;
                                          foreach (int p in preds[b])
                                          {
                                              if (idom[p] == -1) continue;
                                              newIdom = newIdom == -1 ? p : Intersect(newIdom, p);
                                          }
                           
                                          if (newIdom != -1 && idom[b] != newIdom)
                                          {
                                              idom[b] = newIdom;
                                              changed = true;
                                          }
                                      }
                                  }
                           
                                  // Step 4: Dom(exit) = walk idom chain from exit to entry
                                  var result = new HashSet<int>();
                                  int cur = n - 1;
                                  while (cur != 0)
                                  {
                                      result.Add(cur);
                                      cur = idom[cur];
                                  }
                           
                                  result.Add(0);
                                  return result;
                              }
                           
                           }
                           """, "M");

        // BIG = run on a real, complex 55-block method (CHK's own source). Robust capstone: the fast
        // dominator algorithm must agree with the naive delete-test. (Orchestrator completed this — the
        // original placeholder [0,1,4] was copied from the double-return test. The computed spine is 15
        // of 55 blocks: {0,1,2,3,5,6,7,9,10,34,35,50,51,53,54} — prologue + post-loop merges; loop bodies
        // guarded out. Differential check below is renumber-proof; verify if you want the exact set.)
        MustRun_CHK(cfg).ShouldBe(MustRun_N2(cfg), ignoreOrder: true);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // MILESTONE 1: the syntax-node → basic-block bridge.
    // MILESTONE 2: partition a method's effect call-sites into must-run vs guarded.
    // ─────────────────────────────────────────────────────────────────────────────

    // ── PROVIDED (plumbing) — every IOperation under a root: itself + all descendants. ──
    private static IEnumerable<IOperation> SelfAndDescendants(IOperation op)
    {
        yield return op;
        foreach (var child in op.ChildOperations)
        {
            foreach (var d in SelfAndDescendants(child))
            {
                yield return d;
            }
        }
    }

    // The bridge: the CFG block holding `target`, or -1. Scans each block's Operations AND its
    // BranchValue (the condition is NOT in Operations), walks the IOperation tree, matches the EXACT
    // syntax node — so `a?.Save()` resolves to the block holding the CALL, not the null-check block.
    private static int BlockOf(ControlFlowGraph cfg, SyntaxNode target)
    {
        foreach (var block in cfg.Blocks)
        {
            var roots = block.BranchValue is { } bv ? block.Operations.Append(bv) : block.Operations;
            foreach (var root in roots)
            {
                foreach (var op in SelfAndDescendants(root))
                {
                    if (op.Syntax == target)
                    {
                        return block.Ordinal;
                    }
                }
            }
        }

        return -1;
    }

    // Milestone 2: partition the method's effect-bearing call-sites (here: every invocation) into the
    // must-run spine vs the guarded shell, using BlockOf + the dominator must-run set.
    private static (List<string> MustRunSites, List<string> GuardedSites) Partition(
        ControlFlowGraph cfg,
        MethodDeclarationSyntax method
    )
    {
        var mustRunBlocks = MustRun_CHK(cfg);
        var mustRun = new List<string>();
        var guarded = new List<string>();

        foreach (var inv in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var block = BlockOf(cfg, inv);
            if (block < 0)
            {
                continue; // not a node in this method's CFG
            }

            (mustRunBlocks.Contains(block) ? mustRun : guarded).Add(inv.ToString());
        }

        return (mustRun, guarded);
    }

    [Test]
    public void Save_call_lands_in_a_guarded_block()
    {
        var (tree, model) = Compile(
            """
            public sealed class Account { }
            public sealed class C
            {
                public void M(Account a)
                {
                    if (a == null) return;
                    Save(a);                 // guarded: runs only when a != null
                }
                private void Save(Account a) {}
            }
            """
        );
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "M");
        var cfg = ControlFlowGraph.Create((IMethodBodyOperation)model.GetOperation(method)!);

        var saveCall = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single(i => i.Expression.ToString() == "Save");

        var block = BlockOf(cfg, saveCall);
        var mustRun = MustRun_CHK(cfg);

        block.ShouldBeGreaterThanOrEqualTo(0); // bridge found it
        mustRun.ShouldNotContain(block); // and it's NOT must-run → guarded
    }

    // The payoff: the CFG classifies conditional execution that has NO `if` ancestor — the exact cases
    // the rejected syntactic proxy (Path A) would have silently mislabeled as must-run.
    [Test]
    public void Partition_is_sugar_proof_across_conditional_access_short_circuit_and_switch_expr()
    {
        var (tree, model) = Compile(
            """
            public sealed class Account { public void Touch() {} }
            public sealed class C
            {
                public void Demo(Account a, bool flag, int kind)
                {
                    Open();                              // must-run
                    a?.Touch();                          // guarded — conditional access, no `if`
                    if (flag) Audit();                   // guarded — plain if
                    _ = kind switch                      // guarded arms — switch expression
                    {
                        1 => Price(),
                        _ => Zero(),
                    };
                    Commit();                            // must-run — merge after all branches
                }
                private void Open() {}
                private void Audit() {}
                private void Commit() {}
                private int Price() => 1;
                private int Zero() => 0;
            }
            """
        );
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "Demo");
        var cfg = ControlFlowGraph.Create((IMethodBodyOperation)model.GetOperation(method)!);

        var (mustRun, guarded) = Partition(cfg, method);

        // must-run: only the unconditional spine
        mustRun.ShouldContain(s => s.Contains("Open"));
        mustRun.ShouldContain(s => s.Contains("Commit"));
        // guarded: everything behind a predicate — INCLUDING the sugar with no `if` ancestor
        guarded.ShouldContain(s => s.Contains("Touch")); // a?.Touch()  ← Path A misses this
        guarded.ShouldContain(s => s.Contains("Audit"));
        guarded.ShouldContain(s => s.Contains("Price")); // switch arm  ← Path A misses this
        guarded.ShouldContain(s => s.Contains("Zero"));
        // and the spine is NOT polluted by the guarded ones
        mustRun.ShouldNotContain(s => s.Contains("Touch"));
        mustRun.ShouldNotContain(s => s.Contains("Price"));
    }
}
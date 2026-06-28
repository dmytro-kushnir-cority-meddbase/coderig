using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Rig.Analysis.Extraction;
using Shouldly;

namespace Rig.Tests.Analysis;

// Tests for the production control-dependence engine (ControlDependence). The trust mechanism mirrors how
// the must-run spike was validated: hand-verified fixtures PLUS differential oracles — the engine's
// must-run set must equal an obviously-correct naive delete-test, and "has guards" must equal
// "reachable and not must-run".
public sealed class ControlDependenceTests
{
    private static (ControlFlowGraph Cfg, SyntaxTree Tree) Build(string source, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Snippet.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "Snippet",
            syntaxTrees: [tree],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == methodName);
        var cfg = ControlFlowGraph.Create((IMethodBodyOperation)model.GetOperation(method)!);
        return (cfg, tree);
    }

    private static int BlockOfCall(ControlFlowGraph cfg, SyntaxTree tree, string calleeContains)
    {
        var inv = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().First(i => i.ToString().Contains(calleeContains));
        return ControlDependence.BlockOf(cfg, inv);
    }

    // Obviously-correct oracle: a block is must-run iff deleting it strands the Exit from the Entry.
    private static HashSet<int> NaiveMustRun(ControlFlowGraph cfg)
    {
        var exit = cfg.Blocks.Length - 1;
        var result = new HashSet<int>();

        for (var excluded = 0; excluded < cfg.Blocks.Length; excluded++)
        {
            if (!ReachesExit(cfg, exit, excluded))
            {
                result.Add(excluded);
            }
        }

        return result;

        static bool ReachesExit(ControlFlowGraph cfg, int exit, int excluded)
        {
            if (excluded == 0)
            {
                return false; // deleting Entry strands everything
            }

            var seen = new HashSet<int> { 0 };
            var stack = new Stack<int>();
            stack.Push(0);

            while (stack.Count > 0)
            {
                var block = cfg.Blocks[stack.Pop()];
                foreach (
                    var dest in new[] { block.ConditionalSuccessor?.Destination?.Ordinal, block.FallThroughSuccessor?.Destination?.Ordinal }
                )
                {
                    if (dest is not int next || next == excluded)
                    {
                        continue;
                    }

                    if (next == exit)
                    {
                        return true;
                    }

                    if (seen.Add(next))
                    {
                        stack.Push(next);
                    }
                }
            }

            return false;
        }
    }

    // Obviously-correct guard ORACLE: the original O(V^2*E) delete-test (post-dominance via reachability).
    // ComputeGuards (post-dominator tree + FOW) must equal this for every block on every shape.
    private static List<ControlDependence.ControlGuard> NaiveGuards(ControlFlowGraph cfg, int block)
    {
        var exit = cfg.Blocks.Length - 1;
        var guards = new List<ControlDependence.ControlGuard>();

        foreach (var a in cfg.Blocks)
        {
            var cond = a.ConditionalSuccessor?.Destination?.Ordinal;
            var fall = a.FallThroughSuccessor?.Destination?.Ordinal;
            if (a.BranchValue is null || cond is null || fall is null || PostDom(block, a.Ordinal))
            {
                continue;
            }

            if (CanReach(cond.Value) && PostDom(block, cond.Value))
            {
                guards.Add(new(a.Ordinal, a.BranchValue.Syntax.ToString(), a.ConditionKind == ControlFlowConditionKind.WhenTrue));
                continue;
            }

            if (CanReach(fall.Value) && PostDom(block, fall.Value))
            {
                guards.Add(new(a.Ordinal, a.BranchValue.Syntax.ToString(), a.ConditionKind == ControlFlowConditionKind.WhenFalse));
            }
        }

        return guards;

        bool PostDom(int x, int y) => x == y || !ReachAvoiding(y, exit, x);
        bool CanReach(int s) => ReachAvoiding(s, exit, -1);

        bool ReachAvoiding(int start, int target, int excluded)
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
                var b = cfg.Blocks[stack.Pop()];
                foreach (var d in new[] { b.ConditionalSuccessor?.Destination?.Ordinal, b.FallThroughSuccessor?.Destination?.Ordinal })
                {
                    if (d is not int next || next == excluded)
                    {
                        continue;
                    }
                    if (next == target)
                    {
                        return true;
                    }
                    if (seen.Add(next))
                    {
                        stack.Push(next);
                    }
                }
            }
            return false;
        }
    }

    [Test]
    public void Guards_match_the_delete_test_oracle()
    {
        string[] sources =
        [
            """
                public sealed class Account { }
                public sealed class C { void M(Account a) { if (a == null) return; if (a != null) return; Save(a); } void Save(Account a) {} }
                """,
            """
                public sealed class C { void M(bool c) { if (c) A(); else B(); D(); for (int i = 0; i < 9; i++) E(); } void A(){} void B(){} void D(){} void E(){} }
                """,
            SugarSource,
        ];
        string[] methods = ["M", "M", "Demo"];

        for (var i = 0; i < sources.Length; i++)
        {
            var (cfg, _) = Build(sources[i], methods[i]);
            var fast = ControlDependence.ComputeGuards(cfg);
            for (var b = 0; b < cfg.Blocks.Length; b++)
            {
                var f = fast[b].OrderBy(g => g.BranchBlock).ThenBy(g => g.WhenTrue).ToList();
                var oracle = NaiveGuards(cfg, b).OrderBy(g => g.BranchBlock).ThenBy(g => g.WhenTrue).ToList();
                f.ShouldBe(oracle, $"case {i} block {b}");
            }
        }
    }

    // The Demo method exercises conditional execution with NO `if` ancestor — the cases the rejected
    // syntactic proxy would have mislabeled must-run.
    private const string SugarSource = """
        public sealed class Account { public void Touch() {} }
        public sealed class C
        {
            public void Demo(Account a, bool flag, int kind)
            {
                Open();                              // must-run
                a?.Touch();                          // guarded — conditional access
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
        """;

    [Test]
    public void BlockOf_finds_the_invocation()
    {
        var (cfg, tree) = Build(
            """
            public sealed class Account { }
            public sealed class C { void M(Account a) { if (a == null) return; Save(a); } void Save(Account a) {} }
            """,
            "M"
        );

        BlockOfCall(cfg, tree, "Save(a)").ShouldBeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void MustRun_matches_the_naive_oracle()
    {
        // a spread of shapes: guard clause, if/else + loop, sugar.
        string[] sources =
        [
            """
                public sealed class Account { }
                public sealed class C { void M(Account a) { if (a == null) return; if (a != null) return; Save(a); } void Save(Account a) {} }
                """,
            """
                public sealed class C { void M(bool c) { if (c) A(); else B(); D(); for (int i = 0; i < 9; i++) E(); } void A(){} void B(){} void D(){} void E(){} }
                """,
            SugarSource,
        ];
        string[] methods = ["M", "M", "Demo"];

        for (var i = 0; i < sources.Length; i++)
        {
            var (cfg, _) = Build(sources[i], methods[i]);
            ControlDependence.MustRunBlocks(cfg).ShouldBe(NaiveMustRun(cfg), ignoreOrder: true, customMessage: $"case {i}");
        }
    }

    [Test]
    public void IfElse_merge_is_must_run_but_the_arms_are_not()
    {
        var (cfg, tree) = Build(
            """
            public sealed class C { void M(bool c) { if (c) A(); else B(); Merge(); } void A(){} void B(){} void Merge(){} }
            """,
            "M"
        );
        var mustRun = ControlDependence.MustRunBlocks(cfg);

        mustRun.ShouldContain(BlockOfCall(cfg, tree, "Merge"));
        mustRun.ShouldNotContain(BlockOfCall(cfg, tree, "A()"));
        mustRun.ShouldNotContain(BlockOfCall(cfg, tree, "B()"));
    }

    [Test]
    public void Guard_clause_polarity_runs_when_condition_is_false()
    {
        var (cfg, tree) = Build(
            """
            public sealed class Account { }
            public sealed class C { void M(Account a) { if (a == null) return; Save(a); } void Save(Account a) {} }
            """,
            "M"
        );

        var guards = ControlDependence.GuardsOf(cfg, BlockOfCall(cfg, tree, "Save(a)"));

        guards.Count.ShouldBe(1);
        guards[0].Predicate.ShouldContain("== null");
        guards[0].WhenTrue.ShouldBeFalse(); // Save runs when (a == null) is FALSE
    }

    [Test]
    public void IfElse_arms_are_mutually_exclusive_on_the_same_predicate()
    {
        var (cfg, tree) = Build(
            """
            public sealed class C { void M(bool c) { if (c) A(); else B(); } void A(){} void B(){} }
            """,
            "M"
        );

        var aGuards = ControlDependence.GuardsOf(cfg, BlockOfCall(cfg, tree, "A()"));
        var bGuards = ControlDependence.GuardsOf(cfg, BlockOfCall(cfg, tree, "B()"));

        aGuards.Count.ShouldBe(1);
        bGuards.Count.ShouldBe(1);
        aGuards[0].BranchBlock.ShouldBe(bGuards[0].BranchBlock); // same fork
        aGuards[0].WhenTrue.ShouldBeTrue(); // A runs when c is true
        bGuards[0].WhenTrue.ShouldBeFalse(); // B runs when c is false  → mutually exclusive
    }

    [Test]
    public void Guarded_iff_reachable_and_not_must_run()
    {
        var (cfg, tree) = Build(SugarSource, "Demo");
        var mustRun = ControlDependence.MustRunBlocks(cfg);

        foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var block = ControlDependence.BlockOf(cfg, inv);
            if (block < 0)
            {
                continue;
            }

            var hasGuards = ControlDependence.GuardsOf(cfg, block).Count > 0;
            var expected = cfg.Blocks[block].IsReachable && !mustRun.Contains(block);
            hasGuards.ShouldBe(expected, $"block {block} for `{inv}`");
        }
    }

    [Test]
    public void Sugar_with_no_if_ancestor_is_correctly_guarded()
    {
        var (cfg, tree) = Build(SugarSource, "Demo");

        bool Guarded(string callee) => ControlDependence.GuardsOf(cfg, BlockOfCall(cfg, tree, callee)).Count > 0;

        Guarded("Touch").ShouldBeTrue(); // a?.Touch() — conditional access, no `if`
        Guarded("Price").ShouldBeTrue(); // switch-expression arm
        Guarded("Zero").ShouldBeTrue();
        Guarded("Audit").ShouldBeTrue(); // plain if
        Guarded("Open").ShouldBeFalse(); // must-run spine
        Guarded("Commit").ShouldBeFalse();
    }

    // Regression for the vacuous-post-dominance bug: a switch-expression's synthetic no-match throw arm is
    // a dead-end (no path to Exit). Before the CanReachExit gate, "X post-dominates that dead-end" was
    // vacuously true and pinned a spurious guard on every upstream must-run block.
    [Test]
    public void Switch_expression_no_match_throw_arm_does_not_pin_spurious_guards()
    {
        var (cfg, tree) = Build(SugarSource, "Demo");

        // Open is the first statement — must-run, and must carry NO guards despite the switch's throw arm.
        ControlDependence.GuardsOf(cfg, BlockOfCall(cfg, tree, "Open")).ShouldBeEmpty();
        ControlDependence.GuardsOf(cfg, BlockOfCall(cfg, tree, "Commit")).ShouldBeEmpty();
    }
}

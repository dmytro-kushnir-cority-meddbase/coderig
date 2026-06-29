using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Analysis;

// Pattern resolution is a case-insensitive SUBSTRING match over node DocIDs, shared by every traversal seed
// (tree/reaches/callers roots + path target). A fully-qualified name was therefore dragged in every member it
// is a PREFIX of: `…Search.Proceed` also matched `…Search.ProceedToConfirmationScreen`, so `rig tree <fqn>`
// rendered a spurious second root. These tests pin "exact match wins": when the pattern exactly names node(s)
// — by full DocID or by the M:-stripped param-free FQN (the form rig renders) — only those seed; a partial
// pattern keeps the substring behavior. Exercised through the PUBLIC traversal entry points, on a synthetic
// graph with a Proceed / ProceedToConfirmationScreen prefix-twin pair.
public sealed class PatternExactMatchTests
{
    private const string Proceed = "M:N.T.Proceed";
    private const string ProceedTwin = "M:N.T.ProceedToConfirmationScreen";
    private const string ProceedHelper = "M:N.T.ProceedHelper";
    private const string ConfirmHelper = "M:N.T.ConfirmHelper";
    private const string Entry = "M:N.T.Entry";

    // Entry calls both prefix-twins; each twin calls its own helper.
    private static FactGraphData Graph() =>
        new(
            CallEdges: [Edge(Entry, Proceed), Edge(Entry, ProceedTwin), Edge(Proceed, ProceedHelper), Edge(ProceedTwin, ConfirmHelper)],
            ImplementsEdges: [],
            Methods: []
        );

    private static CallEdge Edge(string caller, string callee) => new(caller, callee, EdgeKinds.Invocation, "F.cs", 1);

    [Test]
    public void Reaches_with_full_fqn_seeds_only_the_exact_member_not_its_prefix_twin()
    {
        // The full FQN exactly names Proceed -> only its subtree (Proceed, ProceedHelper); the prefix-twin
        // ProceedToConfirmationScreen (and its helper) is NOT a root and is unreachable from Proceed.
        var reached = FactPathFinder.Reaches(Graph(), "N.T.Proceed");

        reached.Keys.ShouldContain(Proceed);
        reached.Keys.ShouldContain(ProceedHelper);
        reached.Keys.ShouldNotContain(ProceedTwin);
        reached.Keys.ShouldNotContain(ConfirmHelper);
    }

    [Test]
    public void Reaches_with_a_partial_pattern_still_substring_matches_both_twins()
    {
        // "Proceed" exactly equals no node's FQN (they are namespaced), so it stays a substring match and
        // seeds BOTH twins — today's behavior, preserved for partial/short patterns.
        var reached = FactPathFinder.Reaches(Graph(), "Proceed");

        reached.Keys.ShouldContain(Proceed);
        reached.Keys.ShouldContain(ProceedTwin);
        reached.Keys.ShouldContain(ConfirmHelper);
    }

    [Test]
    public void Reaches_accepts_the_full_docid_with_M_prefix_as_an_exact_match()
    {
        // Pasting the raw DocID (with the M: kind prefix) is an exact match too.
        var reached = FactPathFinder.Reaches(Graph(), Proceed);

        reached.Keys.ShouldContain(Proceed);
        reached.Keys.ShouldNotContain(ProceedTwin);
    }

    [Test]
    public void ReachedBy_with_full_fqn_seeds_only_the_exact_target_not_its_prefix_twin()
    {
        // Reverse: the exact FQN target seeds only Proceed, so the reverse closure is {Proceed, Entry} —
        // the prefix-twin is not a seed, so it (and callers reached only via it) stay out.
        var reachedBy = FactPathFinder.ReachedBy(Graph(), "N.T.Proceed");

        reachedBy.Keys.ShouldContain(Proceed);
        reachedBy.Keys.ShouldContain(Entry);
        reachedBy.Keys.ShouldNotContain(ProceedTwin);
    }

    [Test]
    public void ReachedBy_with_a_partial_pattern_still_seeds_both_twins()
    {
        var reachedBy = FactPathFinder.ReachedBy(Graph(), "Proceed");

        reachedBy.Keys.ShouldContain(Proceed);
        reachedBy.Keys.ShouldContain(ProceedTwin); // the twin is itself a depth-0 reverse seed under substring
    }

    [Test]
    public void Find_resolves_the_target_exactly_so_the_path_ends_at_the_named_member()
    {
        // Both twins are direct children of Entry; an exact `to` must terminate the path at Proceed, never at
        // the prefix-twin ProceedToConfirmationScreen (the bug: the substring target check stopped at whichever
        // twin was dequeued first).
        var path = FactPathFinder.Find(Graph(), "N.T.Entry", "N.T.Proceed");

        path.ShouldNotBeNull();
        path![^1].SymbolId.ShouldBe(Proceed);
    }
}

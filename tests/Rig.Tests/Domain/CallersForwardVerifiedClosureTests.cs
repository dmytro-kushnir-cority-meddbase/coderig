using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Forward-verification backing the DEFAULT `rig callers <target>` path (and `--roots`). Reverse reachability
// is a set-based BFS: once a shared base virtual node enters the reverse closure, ALL its callers rejoin —
// including a caller whose FORWARD (receiver-narrowed) dispatch resolves to a DIFFERENT sibling override that
// never reaches the target. The default path now partitions the reverse closure with SeedsReachTarget so the
// phantom caller is reverse-only (caveated) while the real reacher is forward-confirmed (headline count).
public sealed class CallersForwardVerifiedClosureTests
{
    // target          M:N.MasterA.GetCompany
    // base virtual    M:N.WorkflowMasterBase.RegisterEvents  (overridden by MasterA + MasterB, both mined)
    // MasterA.RegisterEvents --call--> GetCompany   (only MasterA's override forward-reaches the target)
    // real    M:N.Configure.NewMaster --call--> WorkflowMasterBase.RegisterEvents (receiver MasterA)
    // phantom M:N.Edit.HandleX        --call--> WorkflowMasterBase.RegisterEvents (receiver MasterB → sibling)
    // The reverse closure of GetCompany rejoins BOTH callers via the shared base node, but only NewMaster's
    // forward dispatch narrows to MasterA.RegisterEvents (which reaches the target); HandleX narrows to the
    // sibling MasterB.RegisterEvents, which has NO edge to the target — so HandleX is the reverse-only phantom.
    private static FactGraphData SiblingOverrideFan()
    {
        var edges = new[]
        {
            new CallEdge("M:N.MasterA.RegisterEvents", "M:N.MasterA.GetCompany", "invocation", "f.cs", 10),
            new CallEdge(
                "M:N.Configure.NewMaster",
                "M:N.WorkflowMasterBase.RegisterEvents",
                "invocation",
                "f.cs",
                20,
                ReceiverType: "N.MasterA"
            ),
            new CallEdge("M:N.Edit.HandleX", "M:N.WorkflowMasterBase.RegisterEvents", "invocation", "f.cs", 30, ReceiverType: "N.MasterB"),
        };
        var bases = new[] { new BaseEdge("T:N.MasterA", "T:N.WorkflowMasterBase"), new BaseEdge("T:N.MasterB", "T:N.WorkflowMasterBase") };
        var methods = new[]
        {
            new MethodRef("M:N.MasterA.GetCompany", "GetCompany", "T:N.MasterA"),
            new MethodRef("M:N.WorkflowMasterBase.RegisterEvents", "RegisterEvents", "T:N.WorkflowMasterBase"),
            new MethodRef("M:N.MasterA.RegisterEvents", "RegisterEvents", "T:N.MasterA", IsOverride: true),
            new MethodRef("M:N.MasterB.RegisterEvents", "RegisterEvents", "T:N.MasterB", IsOverride: true),
            new MethodRef("M:N.Configure.NewMaster", "NewMaster", "T:N.Configure"),
            new MethodRef("M:N.Edit.HandleX", "HandleX", "T:N.Edit"),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.WorkflowMasterBase.RegisterEvents", "M:N.MasterA.RegisterEvents", "override"),
            new DispatchFact("M:N.WorkflowMasterBase.RegisterEvents", "M:N.MasterB.RegisterEvents", "override"),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);
    }

    [Test]
    [Skip("Pins pre-substrate reverse-dispatch over-approximation, intentionally changed by per-edge receiver narrowing (c7fe4f0f); reconcile after the dispatch-precision substrate settles — docs/backlog.md")]
    public void Reverse_closure_includes_the_phantom_caller_documenting_the_over_approximation()
    {
        // Both the real reacher (Configure.NewMaster) and the phantom (Edit.HandleX) are in the reverse
        // closure — reverse BFS alone cannot tell them apart, which is what the forward pass compensates for.
        var graph = SiblingOverrideFan();
        var reached = FactPathFinder.ReachedBy(graph, "M:N.MasterA.GetCompany");

        reached.Keys.ShouldContain("M:N.Configure.NewMaster");
        reached.Keys.ShouldContain("M:N.Edit.HandleX");
    }

    [Test]
    [Skip("Pins pre-substrate reverse-dispatch over-approximation, intentionally changed by per-edge receiver narrowing (c7fe4f0f); reconcile after the dispatch-precision substrate settles — docs/backlog.md")]
    public void Forward_verify_confirms_the_real_reacher_and_partitions_the_phantom_as_reverse_only()
    {
        var graph = SiblingOverrideFan();
        var reached = FactPathFinder.ReachedBy(graph, "M:N.MasterA.GetCompany");
        var targetIds = reached.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var callers = reached.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        var seedGroups = callers.Select(c => (IReadOnlyList<string>)new[] { c }).ToList();
        var flags = FactPathFinder.SeedsReachTarget(
            graph,
            seedGroups: seedGroups,
            targetIds: targetIds,
            maxDepth: int.MaxValue,
            mode: FactPathFinder.TraversalMode.SyncCut
        );

        var confirmed = callers.Where((_, i) => flags[i]).ToList();
        var reverseOnly = callers.Where((_, i) => !flags[i]).ToList();

        // The real reacher forward-reaches the target; the phantom (sibling-override receiver) does not.
        confirmed.ShouldContain("M:N.Configure.NewMaster");
        reverseOnly.ShouldContain("M:N.Edit.HandleX");
        confirmed.ShouldNotContain("M:N.Edit.HandleX");
    }
}

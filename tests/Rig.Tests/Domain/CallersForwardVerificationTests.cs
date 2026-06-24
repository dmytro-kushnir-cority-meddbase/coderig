using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Forward-verification backing `rig callers <target> --entrypoints`. Reverse reachability is set-based
// BFS: once a shared base/interface virtual node (WorkflowMasterBase.RegisterEvents) enters the reverse
// closure, ALL its callers rejoin — including a caller whose FORWARD (receiver-narrowed) dispatch resolves
// to a DIFFERENT sibling override (MasterB.RegisterEvents) that never reaches the target. SeedsReachTarget
// re-checks each candidate EP FORWARD (where narrowDispatch prunes the sibling override) so the misleading
// reverse-only EP can be partitioned out under a caveat instead of reported with false confidence.
public sealed class CallersForwardVerificationTests
{
    // target              M:N.MasterA.GetCompany
    // base virtual        M:N.WorkflowMasterBase.RegisterEvents
    //   overrides         M:N.MasterA.RegisterEvents, M:N.MasterB.RegisterEvents  (base->both, mined)
    // MasterA.RegisterEvents --call--> GetCompany     (only MasterA's override reaches the target)
    // EP_real  M:N.Configure.NewMaster --call--> WorkflowMasterBase.RegisterEvents  (receiver MasterA)
    // EP_false M:N.Edit.HandleX        --call--> WorkflowMasterBase.RegisterEvents  (receiver MasterB)
    private static FactGraphData SiblingOverrideShape()
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
    public void Forward_verify_confirms_only_the_ep_whose_receiver_narrows_to_the_reaching_override()
    {
        var graph = SiblingOverrideShape();
        var flags = FactPathFinder.SeedsReachTarget(
            graph,
            seedGroups: new IReadOnlyList<string>[] { new[] { "M:N.Configure.NewMaster" }, new[] { "M:N.Edit.HandleX" } },
            targetIds: new[] { "M:N.MasterA.GetCompany" },
            maxDepth: int.MaxValue,
            mode: FactPathFinder.TraversalMode.SyncCut
        );

        flags.ShouldBe(new[] { true, false });
    }

    [Test]
    public void Reverse_reach_includes_both_eps_documenting_the_over_approximation()
    {
        // The over-approximation the forward pass compensates for: reverse BFS from the target rejoins
        // BOTH EPs (the base virtual pulls every caller in), so reverse alone cannot tell them apart.
        var graph = SiblingOverrideShape();
        var reached = FactPathFinder.ReachedBy(graph, "M:N.MasterA.GetCompany");

        reached.Keys.ShouldContain("M:N.Configure.NewMaster");
        reached.Keys.ShouldContain("M:N.Edit.HandleX");
    }
}

using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Receiver-context recovery (1-level object sensitivity) in FactPathFinder: a concrete `this`-type pinned
// at the OUTER call that ENTERS an object is carried across self-calls (this./base./implicit) so a
// `this`-virtual inside a base method narrows to that concrete type's override instead of CHA-fanning to
// every sibling. Mirrors the MedDBase leak: `c.Initialise()` (c : InvoiceDebtChase.Controller) ->
// base WorkflowControllerBase.Initialise -> this.OnInitialise, which CHA-fanned to all N workflow
// Controllers because the base method had lost the concrete receiver across the base-call boundary.
// Pure in-memory graph — no Roslyn, no SQLite.
public sealed class ReceiverContextNarrowingTests
{
    // Hierarchy: Controller / Lab / Pacs : WCB; all three override the virtual WCB.OnInit.
    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var bases = new[]
        {
            new BaseEdge("T:N.Controller", "T:N.WCB"),
            new BaseEdge("T:N.Lab", "T:N.WCB"),
            new BaseEdge("T:N.Pacs", "T:N.WCB"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.Master.Build", "Build", "T:N.Master"),
            new MethodRef("M:N.Controller.Init", "Init", "T:N.Controller"),
            new MethodRef("M:N.WCB.Init", "Init", "T:N.WCB"),
            new MethodRef("M:N.WCB.OnInit", "OnInit", "T:N.WCB"),
            new MethodRef("M:N.Controller.OnInit", "OnInit", "T:N.Controller", IsOverride: true),
            new MethodRef("M:N.Lab.OnInit", "OnInit", "T:N.Lab", IsOverride: true),
            new MethodRef("M:N.Pacs.OnInit", "OnInit", "T:N.Pacs", IsOverride: true),
        };
        return new FactGraphData(edges, System.Array.Empty<ImplementsEdge>(), methods, bases);
    }

    private static HashSet<string> Ids(TraceNode node)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        void Walk(TraceNode n)
        {
            set.Add(n.SymbolId);
            foreach (var c in n.Children)
                Walk(c);
        }
        Walk(node);
        return set;
    }

    [Fact]
    public void Concrete_this_carries_across_base_and_this_self_calls_narrowing_the_inner_virtual()
    {
        // Master.Build builds a concrete Controller and calls Init on it; Controller.Init -> base WCB.Init
        // -> this.OnInit. The concrete `N.Controller` pinned at the first call must survive BOTH self-call
        // hops so the OnInit fan-out narrows to Controller.OnInit alone.
        var graph = Graph(
            new CallEdge("M:N.Master.Build", "M:N.Controller.Init", "invocation", "f.cs", 10, ReceiverType: "N.Controller"),
            new CallEdge("M:N.Controller.Init", "M:N.WCB.Init", "invocation", "f.cs", 20, ReceiverType: "N.WCB"), // base.Init()
            new CallEdge("M:N.WCB.Init", "M:N.WCB.OnInit", "invocation", "f.cs", 30, ReceiverType: "N.WCB") // this.OnInit()
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Master.Build").Single());

        ids.ShouldContain("M:N.Controller.OnInit"); // the concrete this-type's override
        ids.ShouldNotContain("M:N.Lab.OnInit"); // sibling override — CHA over-approximation, narrowed away
        ids.ShouldNotContain("M:N.Pacs.OnInit");
    }

    [Fact]
    public void Bare_implicit_this_self_call_also_carries_the_concrete_receiver()
    {
        // Same chain, but the inner virtual is a BARE call (implicit this — no receiver mined on the edge).
        var graph = Graph(
            new CallEdge("M:N.Master.Build", "M:N.Controller.Init", "invocation", "f.cs", 10, ReceiverType: "N.Controller"),
            new CallEdge("M:N.Controller.Init", "M:N.WCB.Init", "invocation", "f.cs", 20, ReceiverType: "N.WCB"),
            new CallEdge("M:N.WCB.Init", "M:N.WCB.OnInit", "invocation", "f.cs", 30) // bare OnInit() — ReceiverType null
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Master.Build").Single());

        ids.ShouldContain("M:N.Controller.OnInit");
        ids.ShouldNotContain("M:N.Lab.OnInit");
        ids.ShouldNotContain("M:N.Pacs.OnInit");
    }

    [Fact]
    public void No_concrete_this_falls_back_to_full_cha()
    {
        // Entered directly at the base method with a base-typed receiver — no concrete this is known, so
        // the inner virtual must keep the FULL CHA fan-out (recall-safe: only narrow when we actually know).
        var graph = Graph(
            new CallEdge("M:N.Master.Build", "M:N.WCB.Init", "invocation", "f.cs", 10, ReceiverType: "N.WCB"),
            new CallEdge("M:N.WCB.Init", "M:N.WCB.OnInit", "invocation", "f.cs", 30, ReceiverType: "N.WCB")
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Master.Build").Single());

        ids.ShouldContain("M:N.Controller.OnInit");
        ids.ShouldContain("M:N.Lab.OnInit");
        ids.ShouldContain("M:N.Pacs.OnInit");
    }

    [Fact]
    public void External_call_on_a_sibling_object_is_not_treated_as_self()
    {
        // Inside WCB.Init reached as concrete Controller, an EXTERNAL call on a Lab-typed field must NOT
        // inherit Controller as its receiver — it dispatches on its OWN (Lab) receiver. Reaching Lab.OnInit
        // (and not Controller.OnInit) proves the carried this-type didn't leak onto the external call.
        var graph = Graph(
            new CallEdge("M:N.Master.Build", "M:N.Controller.Init", "invocation", "f.cs", 10, ReceiverType: "N.Controller"),
            new CallEdge("M:N.Controller.Init", "M:N.WCB.Init", "invocation", "f.cs", 20, ReceiverType: "N.WCB"),
            new CallEdge("M:N.WCB.Init", "M:N.WCB.OnInit", "invocation", "f.cs", 30, ReceiverType: "N.Lab") // _lab.OnInit()
        );

        var ids = Ids(FactPathFinder.BuildTree(graph, "M:N.Master.Build").Single());

        ids.ShouldContain("M:N.Lab.OnInit"); // external receiver dispatch -> Lab
        ids.ShouldNotContain("M:N.Controller.OnInit"); // carried this did NOT leak onto the external call
        ids.ShouldNotContain("M:N.Pacs.OnInit");
    }

    [Fact]
    public void Reaches_closure_also_narrows_the_self_called_inner_virtual()
    {
        // The same recovery in the reaches closure (not just the tree), so `rig reaches` counts the one
        // real override, not the whole sibling fan-out.
        var graph = Graph(
            new CallEdge("M:N.Master.Build", "M:N.Controller.Init", "invocation", "f.cs", 10, ReceiverType: "N.Controller"),
            new CallEdge("M:N.Controller.Init", "M:N.WCB.Init", "invocation", "f.cs", 20, ReceiverType: "N.WCB"),
            new CallEdge("M:N.WCB.Init", "M:N.WCB.OnInit", "invocation", "f.cs", 30, ReceiverType: "N.WCB")
        );

        var reach = FactPathFinder.Reaches(graph, "M:N.Master.Build");

        reach.Keys.ShouldContain("M:N.Controller.OnInit");
        reach.Keys.ShouldNotContain("M:N.Lab.OnInit");
        reach.Keys.ShouldNotContain("M:N.Pacs.OnInit");
    }
}

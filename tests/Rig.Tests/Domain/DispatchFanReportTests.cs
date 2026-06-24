using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// `FactPathFinder.DispatchFanReport` — the read-only diagnostic behind `rig dispatch-fans`. For every
// DISPATCH SITE (a real call edge `caller -(R)-> B` whose receiver-blind fan has >= 2 targets) it decides
// whether R narrowed the fan, and if not classifies WHY. The playground mirrors OneHopDispatchTests'
// synthetic-graph construction: a base virtual `Animal.Speak` with overrides Dog/Cat/Bird (blindFan 3),
// plus one call edge per cause. The report must NOT change any traversal — it only re-measures the fan.
public sealed class DispatchFanReportTests
{
    // Animal.Speak (base virtual) <- Dog/Cat/Bird overrides; blindFan = 3. The receiver-narrowing inputs
    // (base edges + mined override facts) are present so DispatchTargets narrows exactly as the real walk.
    // Each caller A..E exercises one cause via the receiver type on its call edge into Animal.Speak.
    private static FactGraphData FanShape()
    {
        var edges = new[]
        {
            // A.go: concrete receiver Dog -> narrows to Dog.Speak (fan 1). NOT in the report.
            new CallEdge("M:N.A.go", "M:N.Animal.Speak", "invocation", "f.cs", 1, ReceiverType: "N.Dog"),
            // B.go: NULL receiver -> un-narrowed, absent-receiver.
            new CallEdge("M:N.B.go", "M:N.Animal.Speak", "invocation", "f.cs", 2, ReceiverType: null),
            // C.go: receiver TPet (type param: no '.', not a known type) -> un-narrowed, type-parameter.
            new CallEdge("M:N.C.go", "M:N.Animal.Speak", "invocation", "f.cs", 3, ReceiverType: "TPet"),
            // D.go: receiver Animal (the declaring base) -> un-narrowed, base-typed-receiver.
            new CallEdge("M:N.D.go", "M:N.Animal.Speak", "invocation", "f.cs", 4, ReceiverType: "N.Animal"),
            // E.go: receiver Some.External.Thing (has '.', unresolved) -> un-narrowed, external-or-unbound.
            new CallEdge("M:N.E.go", "M:N.Animal.Speak", "invocation", "f.cs", 5, ReceiverType: "Some.External.Thing"),
        };
        var bases = new[]
        {
            new BaseEdge("T:N.Dog", "T:N.Animal"),
            new BaseEdge("T:N.Cat", "T:N.Animal"),
            new BaseEdge("T:N.Bird", "T:N.Animal"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.A.go", "go", "T:N.A"),
            new MethodRef("M:N.B.go", "go", "T:N.B"),
            new MethodRef("M:N.C.go", "go", "T:N.C"),
            new MethodRef("M:N.D.go", "go", "T:N.D"),
            new MethodRef("M:N.E.go", "go", "T:N.E"),
            new MethodRef("M:N.Animal.Speak", "Speak", "T:N.Animal"),
            new MethodRef("M:N.Dog.Speak", "Speak", "T:N.Dog", IsOverride: true),
            new MethodRef("M:N.Cat.Speak", "Speak", "T:N.Cat", IsOverride: true),
            new MethodRef("M:N.Bird.Speak", "Speak", "T:N.Bird", IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.Animal.Speak", "M:N.Dog.Speak", "override"),
            new DispatchFact("M:N.Animal.Speak", "M:N.Cat.Speak", "override"),
            new DispatchFact("M:N.Animal.Speak", "M:N.Bird.Speak", "override"),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);
    }

    [Test]
    public void Aggregates_the_hub_with_all_four_un_narrowed_causes()
    {
        var rows = FactPathFinder.DispatchFanReport(FanShape());

        var hub = rows.SingleOrDefault(r => r.Hub == "M:N.Animal.Speak");
        hub.ShouldNotBeNull();
        hub!.ResidualFan.ShouldBe(3);
        // Four un-narrowed edges (B/C/D/E); A.go was narrowed by its Dog receiver and excluded.
        hub.IncomingEdges.ShouldBe(4);
        hub.AbsentReceiver.ShouldBe(1);
        hub.TypeParameter.ShouldBe(1);
        hub.BaseTypedReceiver.ShouldBe(1);
        hub.ExternalOrUnbound.ShouldBe(1);
        // Has absent + type-parameter edges => actionable.
        hub.Actionable.ShouldBeTrue();
    }

    [Test]
    public void A_narrowed_edge_does_not_contribute_to_the_report()
    {
        // A.go (Dog receiver) narrows the fan to 1, so it must not be counted as an un-narrowed edge.
        var rows = FactPathFinder.DispatchFanReport(FanShape());
        var hub = rows.Single(r => r.Hub == "M:N.Animal.Speak");

        // 5 call edges into the hub, but only 4 un-narrowed (the narrowed Dog edge is gone).
        hub.IncomingEdges.ShouldBe(4);
    }

    [Test]
    public void A_single_target_hub_never_appears()
    {
        // IFoo.M resolves to a single impl (blindFan 1) — never a dispatch site, so never in the report.
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.IFoo.M", "invocation", "f.cs", 1) };
        var impls = new[] { new ImplementsEdge("T:N.Impl", "T:N.IFoo") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.IFoo.M", "M", "T:N.IFoo"),
            new MethodRef("M:N.Impl.M", "M", "T:N.Impl"),
        };
        var mined = new[] { new DispatchFact("M:N.IFoo.M", "M:N.Impl.M", "impl") };
        var graph = new FactGraphData(edges, impls, methods, null, mined);

        var rows = FactPathFinder.DispatchFanReport(graph);

        rows.ShouldBeEmpty();
    }

    [Test]
    public void An_only_irreducible_hub_is_classified_irreducible()
    {
        // Only base-typed + external-or-unbound edges (D + E) => irreducible (no absent / type-parameter).
        var graph = FanShape();
        var onlyIrreducible = new FactGraphData(
            graph.CallEdges.Where(e => e.Caller is "M:N.D.go" or "M:N.E.go").ToArray(),
            graph.ImplementsEdges,
            graph.Methods,
            graph.BaseEdges,
            graph.MinedDispatch
        );

        var hub = FactPathFinder.DispatchFanReport(onlyIrreducible).Single(r => r.Hub == "M:N.Animal.Speak");

        hub.IncomingEdges.ShouldBe(2);
        hub.BaseTypedReceiver.ShouldBe(1);
        hub.ExternalOrUnbound.ShouldBe(1);
        hub.Actionable.ShouldBeFalse();
    }

    [Test]
    public void Ranks_hubs_by_residual_fan_times_incoming_edges()
    {
        // A second, smaller hub: IBig with many incoming un-narrowed edges should be ranked relative to
        // Animal.Speak by residualFan × incomingEdges. Here Animal.Speak (3 × 4 = 12) outranks a 2-fan
        // hub with 2 edges (2 × 2 = 4).
        var baseGraph = FanShape();
        var extraEdges = new List<CallEdge>(baseGraph.CallEdges)
        {
            new("M:N.P.go", "M:N.Shape.Area", "invocation", "g.cs", 1, ReceiverType: null),
            new("M:N.Q.go", "M:N.Shape.Area", "invocation", "g.cs", 2, ReceiverType: null),
        };
        var methods = new List<MethodRef>(baseGraph.Methods)
        {
            new("M:N.P.go", "go", "T:N.P"),
            new("M:N.Q.go", "go", "T:N.Q"),
            new("M:N.Shape.Area", "Area", "T:N.Shape"),
            new("M:N.Square.Area", "Area", "T:N.Square", IsOverride: true),
            new("M:N.Circle.Area", "Area", "T:N.Circle", IsOverride: true),
        };
        var bases = new List<BaseEdge>(baseGraph.BaseEdges!) { new("T:N.Square", "T:N.Shape"), new("T:N.Circle", "T:N.Shape") };
        var mined = new List<DispatchFact>(baseGraph.MinedDispatch!)
        {
            new("M:N.Shape.Area", "M:N.Square.Area", "override"),
            new("M:N.Shape.Area", "M:N.Circle.Area", "override"),
        };
        var graph = new FactGraphData(extraEdges.ToArray(), baseGraph.ImplementsEdges, methods.ToArray(), bases.ToArray(), mined.ToArray());

        var rows = FactPathFinder.DispatchFanReport(graph);

        rows.Count.ShouldBe(2);
        rows[0].Hub.ShouldBe("M:N.Animal.Speak"); // 3 × 4 = 12
        rows[1].Hub.ShouldBe("M:N.Shape.Area"); // 2 × 2 = 4
        rows[0].Rank.ShouldBeGreaterThan(rows[1].Rank);
    }

    [Test]
    public void Base_M_call_edges_are_not_dispatch_sites()
    {
        // A NonVirtual base.M() edge binds to exactly the base body and is one-hop — never a dispatch site.
        var graph = FanShape();
        var withBaseCall = new FactGraphData(
            graph
                .CallEdges.Concat(
                    new[]
                    {
                        new CallEdge("M:N.F.go", "M:N.Animal.Speak", "invocation", "f.cs", 6, ReceiverType: "N.Animal", NonVirtual: true),
                    }
                )
                .ToArray(),
            graph.ImplementsEdges,
            graph.Methods,
            graph.BaseEdges,
            graph.MinedDispatch
        );

        var hub = FactPathFinder.DispatchFanReport(withBaseCall).Single(r => r.Hub == "M:N.Animal.Speak");

        // Still 4 un-narrowed edges (B/C/D/E); the NonVirtual F.go base call is not counted.
        hub.IncomingEdges.ShouldBe(4);
    }

    [Test]
    public void Cause_type_parameter_filter_keeps_only_rows_with_a_type_parameter_edge()
    {
        // Encodes `rig dispatch-fans --cause type-parameter`: of two hubs, only Animal.Speak has a
        // type-parameter edge (C.go's TPet receiver); Shape.Area has only absent-receiver edges. The CLI
        // filter is `r.TypeParameter > 0`, so it must keep Animal.Speak and drop Shape.Area.
        var baseGraph = FanShape();
        var extraEdges = new List<CallEdge>(baseGraph.CallEdges)
        {
            // Shape.Area: two absent-receiver edges only — NO type-parameter edge.
            new("M:N.P.go", "M:N.Shape.Area", "invocation", "g.cs", 1, ReceiverType: null),
            new("M:N.Q.go", "M:N.Shape.Area", "invocation", "g.cs", 2, ReceiverType: null),
        };
        var methods = new List<MethodRef>(baseGraph.Methods)
        {
            new("M:N.P.go", "go", "T:N.P"),
            new("M:N.Q.go", "go", "T:N.Q"),
            new("M:N.Shape.Area", "Area", "T:N.Shape"),
            new("M:N.Square.Area", "Area", "T:N.Square", IsOverride: true),
            new("M:N.Circle.Area", "Area", "T:N.Circle", IsOverride: true),
        };
        var bases = new List<BaseEdge>(baseGraph.BaseEdges!) { new("T:N.Square", "T:N.Shape"), new("T:N.Circle", "T:N.Shape") };
        var mined = new List<DispatchFact>(baseGraph.MinedDispatch!)
        {
            new("M:N.Shape.Area", "M:N.Square.Area", "override"),
            new("M:N.Shape.Area", "M:N.Circle.Area", "override"),
        };
        var graph = new FactGraphData(extraEdges.ToArray(), baseGraph.ImplementsEdges, methods.ToArray(), bases.ToArray(), mined.ToArray());

        var rows = FactPathFinder.DispatchFanReport(graph);
        // The same predicate the CLI's `--cause type-parameter` applies.
        var filtered = rows.Where(r => r.TypeParameter > 0).ToList();

        filtered.Count.ShouldBe(1);
        filtered[0].Hub.ShouldBe("M:N.Animal.Speak");
        filtered.ShouldNotContain(r => r.Hub == "M:N.Shape.Area");
    }

    [Test]
    public void A_traversal_cut_hub_is_excluded_from_the_worklist()
    {
        // A hub that is a configured traversal-cut seam (the ProvideService<T> / service-locator case) never
        // expands its dispatch fan in reaches/tree/path — Successors `yield break`s on a cut node before
        // emitting dispatch — so its raw fan does NOT pollute real reachability and must not appear in the
        // worklist. Cutting Animal.Speak drops it entirely (it was the only hub, with 4 un-narrowed edges).
        var g = FanShape();
        var cut = new FactGraphData(
            g.CallEdges,
            g.ImplementsEdges,
            g.Methods,
            g.BaseEdges,
            g.MinedDispatch,
            CutRules: new[] { new FactTraversalCutRule(Pattern: "M:N.Animal.Speak", Label: "test service-locator cut") }
        );

        var rows = FactPathFinder.DispatchFanReport(cut);

        rows.ShouldNotContain(r => r.Hub == "M:N.Animal.Speak");
        rows.ShouldBeEmpty();
    }
}

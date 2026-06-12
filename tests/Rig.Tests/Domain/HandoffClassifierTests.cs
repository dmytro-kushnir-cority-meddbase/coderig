using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// The async-handoff classifier's two matching paths, in isolation from extraction:
//   * primary  — a method-group edge's DelegateConsumer DocID matches a dispatcher ConsumerPattern
//     (line-free: works no matter where the consuming `new`/call sits).
//   * fallback — DelegateConsumer null (store predates the fact): same-line co-location with a
//     dispatcher-matching ctor/invocation edge.
// Both are recall-safe: only a method-group whose consumer matches a curated dispatcher is rewritten.
public sealed class HandoffClassifierTests
{
    private static readonly IReadOnlyList<FactHandoffRule> Rules =
    [
        new FactHandoffRule("oneshot.schedule", "background", [".BackgroundProcessSchedule.#ctor"]),
    ];

    private static CallEdge? Classified(IReadOnlyList<CallEdge> edges, string callee) =>
        HandoffClassifier.Classify(edges, Rules).FirstOrDefault(e => e.Callee == callee);

    [Fact]
    public void DelegateConsumer_matching_a_dispatcher_reclassifies_the_methodgroup()
    {
        // The consuming ctor is on a DIFFERENT line (106) from the method-group (108) — the multi-line
        // case the same-line key missed. DelegateConsumer carries the link, so line placement is moot.
        var edge = new CallEdge(
            "M:N.AgedState.RegisterTermEndProcess",
            "M:N.AgedState.EndOfTerm",
            "methodGroup",
            "AgedState.cs",
            108,
            DelegateConsumer: "M:N.BackgroundProcessSchedule.#ctor(System.DateTime,N.Delegate,System.String)"
        );

        var result = Classified([edge], "M:N.AgedState.EndOfTerm");

        result!.Kind.ShouldBe("handoff");
        result.HandoffDispatcher.ShouldBe("oneshot.schedule");
    }

    [Fact]
    public void DelegateConsumer_for_a_non_dispatcher_leaves_the_methodgroup_synchronous()
    {
        var edge = new CallEdge(
            "M:N.Caller",
            "M:N.SyncTransform",
            "methodGroup",
            "f.cs",
            10,
            DelegateConsumer: "M:N.Caller.RunNow(N.Delegate)"
        );

        Classified([edge], "M:N.SyncTransform")!.Kind.ShouldBe("methodGroup");
    }

    [Fact]
    public void Without_DelegateConsumer_a_same_line_dispatcher_consumer_still_classifies()
    {
        // Legacy store (no DelegateConsumer): the single-line `new BackgroundProcessSchedule(.., Cb, ..)`
        // puts the ctor edge and the method-group edge at the SAME (Caller, FilePath, Line).
        var ctor = new CallEdge(
            "M:N.Caller",
            "M:N.BackgroundProcessSchedule.#ctor(System.DateTime,N.Delegate,System.String)",
            "ctor",
            "f.cs",
            33
        );
        var methodGroup = new CallEdge("M:N.Caller", "M:N.Cb", "methodGroup", "f.cs", 33);

        Classified([ctor, methodGroup], "M:N.Cb")!.Kind.ShouldBe("handoff");
    }

    [Fact]
    public void Without_DelegateConsumer_a_methodgroup_with_no_colocated_consumer_stays_synchronous()
    {
        var ctor = new CallEdge(
            "M:N.Caller",
            "M:N.BackgroundProcessSchedule.#ctor(System.DateTime,N.Delegate,System.String)",
            "ctor",
            "f.cs",
            33
        );

        // The method-group is on a DIFFERENT line and carries no DelegateConsumer — the fallback cannot
        // link it, so it stays synchronous (the pre-fix behaviour, preserved for old stores).
        var methodGroup = new CallEdge("M:N.Caller", "M:N.Cb", "methodGroup", "f.cs", 35);

        Classified([ctor, methodGroup], "M:N.Cb")!.Kind.ShouldBe("methodGroup");
    }
}

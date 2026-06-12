using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class HandoffClassifierTests
{
    private static readonly IReadOnlyList<FactHandoffRule> Rules =
    [
        new FactHandoffRule("oneshot.schedule", "background", [".BackgroundProcessSchedule.#ctor"]),
    ];

    private static CallEdge? Classified(IReadOnlyList<CallEdge> edges, string callee) =>
        HandoffClassifier.Classify(edges, Rules).FirstOrDefault(e => e.Callee == callee);

    [Test]
    public void DelegateConsumer_matching_a_dispatcher_reclassifies_the_methodgroup()
    {
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

    [Test]
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

    [Test]
    public void Without_DelegateConsumer_a_same_line_dispatcher_consumer_still_classifies()
    {
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

    [Test]
    public void Without_DelegateConsumer_a_methodgroup_with_no_colocated_consumer_stays_synchronous()
    {
        var ctor = new CallEdge(
            "M:N.Caller",
            "M:N.BackgroundProcessSchedule.#ctor(System.DateTime,N.Delegate,System.String)",
            "ctor",
            "f.cs",
            33
        );
        var methodGroup = new CallEdge("M:N.Caller", "M:N.Cb", "methodGroup", "f.cs", 35);

        Classified([ctor, methodGroup], "M:N.Cb")!.Kind.ShouldBe("methodGroup");
    }
}

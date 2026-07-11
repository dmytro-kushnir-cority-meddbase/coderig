using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Characterization tests for the `DoWhenCommitted(() => Effect())` post-commit-callback shape (backlog
// item "Reach post-commit callbacks"). These pin what the engine ACTUALLY does, free of the store-level
// confounders (substring-matching `~λ` nodes as traversal roots, generic/overload id collisions) that make
// the behaviour easy to misread when querying the real MedDBase store.
//
// Real store shape (verified on the MedDBase store, AbsenceRecordEntity.Save):
//   Caller  --methodGroup-->  Caller~λ0   (DelegateConsumer = the CommonEntityBase.DoWhenCommitted call)
//   Caller~λ0  --invocation-->  Effect     (the lambda body)
// We use NON-colliding ids below so the FROM pattern unambiguously seeds the caller (not the lambda),
// which is exactly what isolates the question we care about: does the traversal CROSS the caller->lambda edge?
public sealed class DoWhenCommittedHandoffTests
{
    private const string Caller = "M:N.AbsenceEntity.DoSave(P,B)";
    private const string Lambda = "M:N.AbsenceEntity.CommitCallback0";
    private const string Effect = "M:N.AbsenceEntity.LogAuditAdded";
    private const string Consumer = "M:N.CommonEntityBase.DoWhenCommitted(System.Action,N.ITransaction)";

    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(IReadOnlyList<CallEdge> edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    private static CallEdge[] Edges() =>
        [
            // The delegate handed to the dispatcher: methodGroup Caller -> lambda, tagged with the consumer.
            new CallEdge(Caller, Lambda, "methodGroup", "f.cs", 114, DelegateConsumer: Consumer),
            // The dispatcher call itself.
            new CallEdge(Caller, Consumer, "invocation", "f.cs", 114),
            // The lambda body's effectful call.
            new CallEdge(Lambda, Effect, "invocation", "f.cs", 114),
        ];

    // CURRENT behaviour: with NO DoWhenCommitted dispatcher rule, the methodGroup->lambda edge is an
    // ordinary (unclassified) edge and is WALKED synchronously — so the deferred callback's effect IS
    // already reachable from the caller today. This REFUTES the backlog premise that the callback is
    // "sync-cut and --async doesn't reach it either".
    [Test]
    public void Unclassified_DoWhenCommitted_lambda_is_walked_synchronously()
    {
        var graph = Graph(HandoffClassifier.Classify(Edges(), rules: []));

        FactPathFinder.Find(graph, Caller, Effect).ShouldNotBeNull();
    }

    // What a `handoffDispatchers` rule for DoWhenCommitted WOULD do: reclassify the methodGroup->lambda edge
    // to a handoff, which SYNC-CUTS it (no synchronous path to the callback) and only walks it under --async.
    // i.e. the rule is a PRECISION change (model the callback as deferred), the OPPOSITE of a recall fix.
    [Test]
    public void A_DoWhenCommitted_dispatcher_rule_sync_cuts_the_callback_and_async_walks_it()
    {
        var rules = new[] { new FactHandoffRule("meddbase.transaction.doWhenCommitted", "deferred", [".DoWhenCommitted"]) };
        var graph = Graph(HandoffClassifier.Classify(Edges(), rules));

        FactPathFinder.Find(graph, Caller, Effect).ShouldBeNull();
        FactPathFinder.Find(graph, Caller, Effect, mode: FactPathFinder.TraversalMode.AsyncInclude).ShouldNotBeNull();
    }
}

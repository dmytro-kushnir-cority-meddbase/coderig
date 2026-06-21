using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged observation rule sections to Rig.Domain's fact-layer observation deriver (P2b),
// mirroring FactEffectRuleProvider. Projects the resilience-retry and concurrency-handled rule families
// (read_before_commit is deferred — cross-invocation, EF-only).
//
// The parallel-fanout list is still hardcoded HERE (as it is in the Roslyn EffectObservationExtractor
// today) rather than in rule data — moving it into rig.rules.json is P2c. Keeping it in this Analysis
// provider, not the Domain deriver, preserves the "detectors are data, Domain is generic" boundary.
internal static class FactObservationRuleProvider
{
    // Task.WhenAll and Parallel.ForEach/ForEachAsync — the fanout wrappers. Matched by the FQN of the
    // resolved receiver type (robust to qualification), with the short name kept for the observation context.
    private static readonly IReadOnlyList<FactParallelFanoutRule> ParallelFanout =
    [
        new FactParallelFanoutRule(Receiver: "Task", ReceiverType: "System.Threading.Tasks.Task", Methods: ["WhenAll"]),
        new FactParallelFanoutRule(
            Receiver: "Parallel",
            ReceiverType: "System.Threading.Tasks.Parallel",
            Methods: ["ForEach", "ForEachAsync"]
        ),
    ];

    internal static FactObservationRules Project(AnalysisRulesDocument doc)
    {
        var resilience = (doc.Observations?.ResilienceRetry ?? [])
            .Select(r => new FactResilienceRetryRule(WrapperMethods: r.WrapperMethods, ReceiverTypePatterns: r.ReceiverTypePatterns))
            .ToArray();
        var concurrency = (doc.Observations?.ConcurrencyHandled ?? [])
            .Select(r => new FactConcurrencyHandledRule(CommitMethods: r.CommitMethods, CatchTypePatterns: r.CatchTypePatterns))
            .ToArray();
        var resourceSpan = (doc.Observations?.ResourceSpan ?? [])
            .Select(r => new FactResourceSpanRule(
                ScopeKind: r.ScopeKind,
                ScopeTypePatterns: r.ScopeTypePatterns,
                ExcludeProviders: r.ExcludeProviders,
                ObservationType: r.ObservationType,
                Context: r.Context
            ))
            .ToArray();

        return new FactObservationRules(resilience, concurrency, ParallelFanout, resourceSpan);
    }
}

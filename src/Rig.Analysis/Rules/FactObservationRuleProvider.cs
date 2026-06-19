using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Bridges the internal AnalysisRuleSet observation rules to Rig.Domain's fact-layer observation
// deriver (P2b), mirroring FactEffectRuleProvider. Projects the resilience-retry and
// concurrency-handled rule families (read_before_commit is deferred — cross-invocation, EF-only).
//
// The parallel-fanout list is still hardcoded HERE (as it is in the Roslyn EffectObservationExtractor
// today) rather than in rule data — moving it into rig.rules.json is P2c. Keeping it in this Analysis
// provider, not the Domain deriver, preserves the "detectors are data, Domain is generic" boundary.
public static class FactObservationRuleProvider
{
    // Task.WhenAll and Parallel.ForEach/ForEachAsync — the fanout wrappers the Roslyn
    // FindParallelFanoutContext recognizes by receiver text + method name.
    private static readonly IReadOnlyList<FactParallelFanoutRule> ParallelFanout =
    [
        new FactParallelFanoutRule("Task", ["WhenAll"]),
        new FactParallelFanoutRule("Parallel", ["ForEach", "ForEachAsync"]),
    ];

    public static FactObservationRules LoadForWorkingDirectory(string workingDirectory, IReadOnlyList<string>? extraRulesPaths = null)
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        return Project(AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths));
    }

    // Project off an already-merged rule set, so RuleSet.Load can project this slice without a second load.
    internal static FactObservationRules Project(AnalysisRuleSet rules)
    {
        var resilience = rules
            .ResilienceRetryObservations.Select(r => new FactResilienceRetryRule(
                WrapperMethods: r.WrapperMethods,
                ReceiverTypePatterns: r.ReceiverTypePatterns
            ))
            .ToArray();
        var concurrency = rules
            .ConcurrencyHandledObservations.Select(r => new FactConcurrencyHandledRule(
                CommitMethods: r.CommitMethods,
                CatchTypePatterns: r.CatchTypePatterns
            ))
            .ToArray();
        var resourceSpan = rules
            .ResourceSpanObservations.Select(r => new FactResourceSpanRule(
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

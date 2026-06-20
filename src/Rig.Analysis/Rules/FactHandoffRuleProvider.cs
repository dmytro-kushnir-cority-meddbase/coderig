using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `handoffDispatchers` rule section to the fact-matchable FactHandoffRule the Domain
// HandoffClassifier consumes. The generic matcher lives in Domain; rule data flows in through RuleSetLoader.
internal static class FactHandoffRuleProvider
{
    internal static IReadOnlyList<FactHandoffRule> Project(AnalysisRulesDocument doc) => (doc.HandoffDispatchers ?? []).Select(Project).ToArray();

    private static FactHandoffRule Project(HandoffDispatcherRule rule) =>
        new(
            Id: rule.Id,
            Kind: rule.Kind,
            ConsumerPatterns: rule.ConsumerPatterns ?? [],
            Repeating: rule.Repeating,
            Requires: rule.Requires
        );
}

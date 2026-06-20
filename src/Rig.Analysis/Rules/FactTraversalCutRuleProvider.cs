using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `traversalCuts` rule section to Rig.Domain's FactTraversalCutRule. Rule data flows
// in through RuleSetLoader; the matcher lives in Domain (FactTraversalCutRule.IsMatch).
internal static class FactTraversalCutRuleProvider
{
    internal static IReadOnlyList<FactTraversalCutRule> Project(AnalysisRulesDocument doc) =>
        (doc.TraversalCuts ?? []).Select(Project).ToArray();

    private static FactTraversalCutRule Project(TraversalCutRule rule) =>
        new(Pattern: rule.Pattern, Label: rule.Label ?? rule.Reason ?? rule.Pattern);
}

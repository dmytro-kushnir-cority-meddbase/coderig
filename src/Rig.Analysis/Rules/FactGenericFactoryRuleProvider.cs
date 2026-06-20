using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `genericFactories` rule section to Rig.Domain's FactGenericFactoryRule the graph
// transform consumes. Rule data flows in through RuleSetLoader; the transform lives in Domain.
internal static class FactGenericFactoryRuleProvider
{
    internal static IReadOnlyList<FactGenericFactoryRule> Project(AnalysisRulesDocument doc) =>
        (doc.GenericFactories ?? []).Select(Project).ToArray();

    private static FactGenericFactoryRule Project(GenericFactoryRule rule) =>
        new(Method: rule.Method, ConstructArgIndex: rule.ConstructArgIndex, TargetMethod: rule.TargetMethod);
}

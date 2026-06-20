using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `contextDispatch` rule section to Rig.Domain's FactContextDispatchRule. Rule data
// flows in through RuleSetLoader; the narrowing mechanism lives in Domain (FactPathFinder).
internal static class FactContextDispatchRuleProvider
{
    internal static IReadOnlyList<FactContextDispatchRule> Project(AnalysisRulesDocument doc) =>
        (doc.ContextDispatch ?? []).Select(Project).ToArray();

    private static FactContextDispatchRule Project(ContextDispatchRule rule) =>
        new(Interface: rule.Interface, BindingBase: rule.BindingBase);
}

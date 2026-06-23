using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `redirectRules` rule section to the fact-matchable FactRedirectRule the Domain
// RedirectClassifier consumes (the external-virtual-override-orphan fix). The generic matcher lives in
// Domain; rule data flows in through RuleSetLoader. Mirrors FactHandoffRuleProvider.
internal static class FactRedirectRuleProvider
{
    internal static IReadOnlyList<FactRedirectRule> Project(AnalysisRulesDocument doc) =>
        (doc.RedirectRules ?? []).Select(r => new FactRedirectRule(Method: r.Method, RedirectTo: r.RedirectTo)).ToArray();
}

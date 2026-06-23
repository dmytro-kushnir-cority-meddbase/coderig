using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `render` rule section to Rig.Domain's FactRenderRules. Rule data flows in through
// RuleSetLoader; the matcher lives in Domain.
internal static class FactRenderRuleProvider
{
    internal static FactRenderRules Project(AnalysisRulesDocument doc) =>
        new(
            CollapseSeams: (doc.Render?.CollapseSeams ?? []).Select(Project).ToArray(),
            OpaqueTypes: (doc.Render?.OpaqueTypes ?? []).Select(Project).ToArray()
        );

    private static FactRenderRule Project(RenderRule rule) => new(Pattern: rule.Pattern, Label: rule.Label ?? rule.Reason ?? rule.Pattern);
}

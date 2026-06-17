using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Bridges the internal AnalysisRuleSet to Rig.Domain's FactRenderRules: loads the `render` rule
// section (built-in + global ~/.rig + local rig.rules.json + --rules) and projects it to the
// fact-matchable FactRenderRule. Same cascade + layering as FactHandoffRuleProvider — render data
// flows in from this (the only layer that can read AnalysisRuleSet); the matcher lives in Domain.
public static class FactRenderRuleProvider
{
    public static FactRenderRules LoadForWorkingDirectory(string workingDirectory, IReadOnlyList<string>? extraRulesPaths = null)
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        var ruleSet = AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths);
        return new FactRenderRules(
            CollapseSeams: ruleSet.RenderCollapseSeams.Select(Project).ToArray(),
            OpaqueTypes: ruleSet.RenderOpaqueTypes.Select(Project).ToArray()
        );
    }

    private static FactRenderRule Project(RenderRule rule) => new(Pattern: rule.Pattern, Label: rule.Label ?? rule.Reason ?? rule.Pattern);
}

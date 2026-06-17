using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Bridges the internal AnalysisRuleSet to Rig.Domain's FactTraversalCutRule: loads the
// `traversalCuts` rule section (built-in + global ~/.rig + local rig.rules.json + --rules) and
// projects it to the fact-matchable FactTraversalCutRule. Same cascade + layering as
// FactRenderRuleProvider — cut data flows in from this (the only layer that can read
// AnalysisRuleSet); the matcher lives in Domain (FactTraversalCutRule.IsMatch).
public static class FactTraversalCutRuleProvider
{
    public static IReadOnlyList<FactTraversalCutRule> LoadForWorkingDirectory(
        string workingDirectory,
        IReadOnlyList<string>? extraRulesPaths = null
    )
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        return Project(AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths));
    }

    // Project off an already-merged rule set, so RuleSet.Load can project this slice without a second load.
    internal static IReadOnlyList<FactTraversalCutRule> Project(AnalysisRuleSet ruleSet) => ruleSet.TraversalCuts.Select(Project).ToArray();

    private static FactTraversalCutRule Project(TraversalCutRule rule) =>
        new(Pattern: rule.Pattern, Label: rule.Label ?? rule.Reason ?? rule.Pattern);
}

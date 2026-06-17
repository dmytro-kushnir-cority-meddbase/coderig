using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Bridges the internal AnalysisRuleSet to Rig.Domain's FactContextDispatchRule: loads the
// `contextDispatch` rule section (built-in + global ~/.rig + local rig.rules.json + --rules) and
// projects it to the fact-matchable FactContextDispatchRule. Same cascade + layering as
// FactTraversalCutRuleProvider — context-dispatch data flows in from this (the only layer that can
// read AnalysisRuleSet); the narrowing mechanism lives in Domain (FactPathFinder).
public static class FactContextDispatchRuleProvider
{
    public static IReadOnlyList<FactContextDispatchRule> LoadForWorkingDirectory(
        string workingDirectory,
        IReadOnlyList<string>? extraRulesPaths = null
    )
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        return Project(AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths));
    }

    // Project off an already-merged rule set, so RuleSet.Load can project this slice without a second load.
    internal static IReadOnlyList<FactContextDispatchRule> Project(AnalysisRuleSet ruleSet) =>
        ruleSet.ContextDispatch.Select(Project).ToArray();

    private static FactContextDispatchRule Project(ContextDispatchRule rule) =>
        new(Interface: rule.Interface, BindingBase: rule.BindingBase);
}

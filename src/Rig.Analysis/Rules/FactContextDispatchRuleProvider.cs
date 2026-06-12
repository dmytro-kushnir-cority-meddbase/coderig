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
        var ruleSet = AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths);
        return ruleSet.ContextDispatch.Select(Project).ToArray();
    }

    private static FactContextDispatchRule Project(ContextDispatchRule rule) => new(rule.Interface, rule.BindingBase);
}

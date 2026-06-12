using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Bridges the internal AnalysisRuleSet to Rig.Domain's FactGenericFactoryRule: loads the
// `genericFactories` rule section (built-in + global ~/.rig + local rig.rules.json + --rules) and
// projects it to the fact-layer rule the graph transform consumes. Same cascade + layering as the
// render/handoff providers — rule data flows in from this layer; the transform lives in Domain.
public static class FactGenericFactoryRuleProvider
{
    public static IReadOnlyList<FactGenericFactoryRule> LoadForWorkingDirectory(
        string workingDirectory,
        IReadOnlyList<string>? extraRulesPaths = null
    )
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        var ruleSet = AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths);
        return ruleSet.GenericFactories.Select(Project).ToArray();
    }

    private static FactGenericFactoryRule Project(GenericFactoryRule rule) => new(rule.Method, rule.ConstructArgIndex, rule.TargetMethod);
}

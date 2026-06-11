using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Bridges the internal AnalysisRuleSet to Rig.Domain's HandoffClassifier: loads the
// `handoffDispatchers` rule section (built-in + global ~/.rig + local rig.rules.json + --rules) and
// projects it to the fact-matchable FactHandoffRule. Same cascade + layering as
// FactEffectRuleProvider / FactEntryPointRuleProvider — rule data flows in from this (the only layer
// that can read AnalysisRuleSet); the generic matcher lives in Domain.
public static class FactHandoffRuleProvider
{
    public static IReadOnlyList<FactHandoffRule> LoadForWorkingDirectory(
        string workingDirectory,
        IReadOnlyList<string>? extraRulesPaths = null
    )
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        var ruleSet = AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths);
        return ruleSet.HandoffDispatchers.Select(Project).ToArray();
    }

    private static FactHandoffRule Project(HandoffDispatcherRule rule) =>
        new(rule.Id, rule.Kind, rule.ConsumerPatterns ?? [], rule.Repeating);
}

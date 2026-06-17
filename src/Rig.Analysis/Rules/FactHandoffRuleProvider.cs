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
        return Project(AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths));
    }

    // Project off an already-merged rule set, so a caller that loaded the cascade once (RuleSet.Load) can
    // project this slice without a second LoadForSolution.
    internal static IReadOnlyList<FactHandoffRule> Project(AnalysisRuleSet ruleSet) => ruleSet.HandoffDispatchers.Select(Project).ToArray();

    private static FactHandoffRule Project(HandoffDispatcherRule rule) =>
        new(
            Id: rule.Id,
            Kind: rule.Kind,
            ConsumerPatterns: rule.ConsumerPatterns ?? [],
            Repeating: rule.Repeating,
            Requires: rule.Requires
        );
}

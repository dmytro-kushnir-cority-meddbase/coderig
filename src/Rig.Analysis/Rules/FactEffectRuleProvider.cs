using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Bridges the internal AnalysisRuleSet to Rig.Domain's fact-layer effect deriver: loads the same
// effect-rule JSON the Roslyn pass uses and projects it to the fact-matchable subset
// (FactEffectRule). This keeps effect detection data-driven (the "detectors are data" agreement)
// while respecting the Domain<-Analysis layering — the matcher (generic infra) lives in Domain,
// the rule data flows in from here, which is the only layer that can read AnalysisRuleSet.
public static class FactEffectRuleProvider
{
    // Resolves effect rules for a fact query rooted at <paramref name="workingDirectory"/> (the
    // directory holding the .rig store). AnalysisRuleSet.LoadForSolution keys rule discovery off
    // the anchor's directory, so a non-solution anchor inside the working dir picks up a colocated
    // rig.rules.json on top of the global (~/.rig) + built-in cascade; --rules paths are appended.
    public static IReadOnlyList<FactEffectRule> LoadForWorkingDirectory(
        string workingDirectory,
        IReadOnlyList<string>? extraRulesPaths = null
    )
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        return Project(AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths));
    }

    // Project off an already-merged rule set, so RuleSet.Load can project this slice without a second load.
    internal static IReadOnlyList<FactEffectRule> Project(AnalysisRuleSet ruleSet) => ruleSet.Effects.Select(Project).ToArray();

    private static FactEffectRule Project(EffectRule rule)
    {
        return new FactEffectRule(
            Provider: rule.Provider,
            Operation: rule.Operation,
            Methods: rule.Methods,
            DeclaringTypes: rule.DeclaringTypes ?? [],
            ReceiverTypes: rule.ReceiverTypes ?? [],
            DeclaringTypeNameEndsWith: rule.DeclaringTypeNameEndsWith,
            DeclaringTypeBaseTypes: rule.DeclaringTypeBaseTypes,
            MatchConstructor: rule.MatchConstructor,
            MinArguments: rule.MinArguments,
            MatchThrow: rule.MatchThrow,
            ContainingNamespaces: rule.ContainingNamespaces,
            ContainingTypes: rule.ContainingTypes,
            ContainingMethods: rule.ContainingMethods,
            Resource: rule.Resource,
            TreatAsDispatch: rule.TreatAsDispatch,
            TargetCallsMethods: rule.TargetCallsMethods,
            TypeArgumentIndex: rule.TypeArgumentIndex,
            ArgumentIndex: rule.ArgumentIndex
        );
    }
}

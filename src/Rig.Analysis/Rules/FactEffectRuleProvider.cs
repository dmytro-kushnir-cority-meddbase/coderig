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
        var rules = AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths);
        return rules.Effects.Select(Project).ToArray();
    }

    private static FactEffectRule Project(EffectRule rule)
    {
        return new FactEffectRule(
            rule.Provider,
            rule.Operation,
            rule.Methods,
            rule.DeclaringTypes ?? [],
            rule.ReceiverTypes ?? [],
            rule.DeclaringTypeNameEndsWith,
            rule.DeclaringTypeBaseTypes,
            rule.MatchConstructor,
            rule.MinArguments,
            rule.MatchThrow,
            rule.ContainingNamespaces,
            rule.ContainingTypes,
            rule.ContainingMethods,
            rule.Resource,
            rule.TreatAsDispatch
        );
    }
}

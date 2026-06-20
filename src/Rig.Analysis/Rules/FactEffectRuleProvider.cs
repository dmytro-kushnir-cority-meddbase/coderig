using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `effects` rule section to Rig.Domain's fact-matchable FactEffectRule. Keeps effect
// detection data-driven (the "detectors are data" agreement) while respecting the Domain<-Analysis
// layering — the matcher (generic infra) lives in Domain; rule data flows in through RuleSetLoader, the
// only layer that can read the JSON authoring model.
internal static class FactEffectRuleProvider
{
    internal static IReadOnlyList<FactEffectRule> Project(AnalysisRulesDocument doc) => (doc.Effects ?? []).Select(Project).ToArray();

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

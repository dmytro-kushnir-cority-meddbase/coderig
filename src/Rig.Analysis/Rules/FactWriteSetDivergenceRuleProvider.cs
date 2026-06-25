using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `writeSetDivergence` rule section to the domain FactWriteSetDivergenceRule the
// write-set-divergence detector consumes (wired in DeriveCommand). A SINGLE object, not a list: null
// when the section is absent, has no pairs, or has no write effects. Mirrors FactCacheCoherenceRuleProvider.
internal static class FactWriteSetDivergenceRuleProvider
{
    internal static FactWriteSetDivergenceRule? Project(AnalysisRulesDocument doc)
    {
        var rule = doc.WriteSetDivergence;
        if (rule is null)
        {
            return null;
        }

        var pairs = rule.Pairs ?? [];
        var writeEffects = rule.WriteEffects ?? [];

        if (pairs.Count == 0 && writeEffects.Count == 0)
        {
            return null;
        }

        var domainPairs = pairs
            .Select(p => new FactWriteSetDivergencePair(
                Entity: p.Entity,
                PrimaryEntryPoint: p.PrimaryEntryPoint,
                SecondaryEntryPoint: p.SecondaryEntryPoint
            ))
            .ToArray();

        var domainEffects = writeEffects.Select(e => new FactEffectRef(Provider: e.Provider, Operation: e.Operation)).ToArray();

        return new FactWriteSetDivergenceRule(Pairs: domainPairs, WriteEffects: domainEffects);
    }
}

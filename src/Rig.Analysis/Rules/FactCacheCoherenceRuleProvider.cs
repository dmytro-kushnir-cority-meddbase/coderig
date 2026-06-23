using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `cacheCoherence` rule section to the domain FactCacheCoherenceRule the
// FactCacheCoherenceDeriver consumes (FR-7). A SINGLE object, not a list: null when the section is absent or
// empty (no cached entities AND no bulk-write methods AND no invalidation methods). Mirrors
// FactRedirectRuleProvider.
internal static class FactCacheCoherenceRuleProvider
{
    internal static FactCacheCoherenceRule? Project(AnalysisRulesDocument doc)
    {
        var rule = doc.CacheCoherence;
        if (rule is null)
        {
            return null;
        }

        var cachedEntities = rule.CachedEntities ?? [];
        var bulkWriteMethods = rule.BulkWriteMethods ?? [];
        var invalidationMethods = rule.InvalidationMethods ?? [];
        if (cachedEntities.Count == 0 && bulkWriteMethods.Count == 0 && invalidationMethods.Count == 0)
        {
            return null;
        }

        return new FactCacheCoherenceRule(
            CachedEntities: cachedEntities,
            BulkWriteMethods: bulkWriteMethods,
            InvalidationMethods: invalidationMethods
        );
    }
}

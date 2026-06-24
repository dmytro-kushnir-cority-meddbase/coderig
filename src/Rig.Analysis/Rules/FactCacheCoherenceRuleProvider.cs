using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `cacheCoherence` rule section to the domain FactCacheCoherenceRule the cache-coherence
// correlation INSTANCE consumes (FR-7). A SINGLE object, not a list: null when the section is absent or empty
// (no cachedEntities AND no excludeEnclosingNamespaceSuffix). Mirrors FactRedirectRuleProvider.
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
        var excludeEnclosingNamespaceSuffix = rule.ExcludeEnclosingNamespaceSuffix;
        if (cachedEntities.Count == 0 && (excludeEnclosingNamespaceSuffix is null || excludeEnclosingNamespaceSuffix.Count == 0))
        {
            return null;
        }

        return new FactCacheCoherenceRule(CachedEntities: cachedEntities, ExcludeEnclosingNamespaceSuffix: excludeEnclosingNamespaceSuffix);
    }
}

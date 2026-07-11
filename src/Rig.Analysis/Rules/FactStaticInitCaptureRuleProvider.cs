using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Projects the merged `staticInitCapture` rule section to the domain FactStaticInitCaptureRule the
// static_init_capture detector consumes. A SINGLE object, not a list: null when the section is absent or
// carries no mutableSources (opt-in — the detector never fires without project-specific patterns). Mirrors
// FactCacheCoherenceRuleProvider.
internal static class FactStaticInitCaptureRuleProvider
{
    internal static FactStaticInitCaptureRule? Project(AnalysisRulesDocument doc)
    {
        var rule = doc.StaticInitCapture;
        if (rule is null)
        {
            return null;
        }

        var mutableSources = rule.MutableSources ?? [];
        if (mutableSources.Count == 0)
        {
            return null;
        }

        return new FactStaticInitCaptureRule(MutableSources: mutableSources);
    }
}

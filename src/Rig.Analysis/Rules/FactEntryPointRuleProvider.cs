using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Bridges the internal AnalysisRuleSet to Rig.Domain's fact-layer entry-point deriver.
// Projects the JSON pageModel rules (PageModelEntryPointRule) to the fact-matchable subset
// (FactEntryPointRule).  Rule data stays in JSON; only generic matching infra is in C#.
// See the "detectors are data" agreement and docs/fact-layer-refactor.md.
public static class FactEntryPointRuleProvider
{
    // Loads entry-point rules projected for the fact deriver, rooted at <workingDirectory>.
    // Uses the same rule-cascade as FactEffectRuleProvider (built-in + global ~/.rig + local
    // rig.rules.json + extraRulesPaths).
    public static IReadOnlyList<FactEntryPointRule> LoadForWorkingDirectory(
        string workingDirectory,
        IReadOnlyList<string>? extraRulesPaths = null)
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        var ruleSet = AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths);
        return ruleSet.PageModelEntryPoints.Select(Project).ToArray();
    }

    private static FactEntryPointRule Project(PageModelEntryPointRule rule)
    {
        // HandlerMethodAttributes in the JSON are short names or fully-qualified type names.
        // The reference_facts TargetSymbolId for an attribute ctor is the DocID, e.g.:
        //   "M:MMS.Web.UI.Attributes.ClientActionAttribute.#ctor"
        // We store the attribute type names as prefix matchers:
        //   "MMS.Web.UI.Attributes.ClientActionAttribute" -> match DocID starting with
        //   "M:MMS.Web.UI.Attributes.ClientActionAttribute."
        // Short names (e.g. "ClientAction") can't be reliably matched without a receiver type,
        // so we only emit prefix matchers for FQN entries that contain a '.'.
        var attributePrefixes = (rule.HandlerMethodAttributes ?? [])
            .Where(a => a.Contains('.'))
            .Select(a => $"M:{a}.")
            .ToArray();

        return new FactEntryPointRule(
            rule.Id,
            rule.Kind,
            rule.DefaultMethod ?? rule.Kind.ToUpperInvariant(),
            rule.BaseTypes,
            rule.NamespacePrefix,
            attributePrefixes
        );
    }
}

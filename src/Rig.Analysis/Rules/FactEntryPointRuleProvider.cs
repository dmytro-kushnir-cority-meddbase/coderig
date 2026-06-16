using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// Bridges the internal AnalysisRuleSet to Rig.Domain's fact-layer entry-point deriver.
// Projects the JSON typeEntryPoints rules (TypeEntryPointRule; `pageModel` is the deprecated alias) to
// the fact-matchable subset (FactEntryPointRule).  Rule data stays in JSON; only generic matching infra
// is in C#.  See the "detectors are data" agreement and docs/fact-layer-refactor.md.
public static class FactEntryPointRuleProvider
{
    // Loads entry-point rules projected for the fact deriver, rooted at <workingDirectory>.
    // Uses the same rule-cascade as FactEffectRuleProvider (built-in + global ~/.rig + local
    // rig.rules.json + extraRulesPaths).
    public static IReadOnlyList<FactEntryPointRule> LoadForWorkingDirectory(
        string workingDirectory,
        IReadOnlyList<string>? extraRulesPaths = null
    )
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        var ruleSet = AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths);
        return ruleSet.TypeEntryPoints.Select(Project).ToArray();
    }

    // Loads the classInheritance entry-point rules projected for the fact deriver (Pattern C:
    // background/service/WCF/HTTP/actor handlers). Same rule-cascade as LoadForWorkingDirectory.
    public static IReadOnlyList<FactClassInheritanceRule> LoadClassInheritanceForWorkingDirectory(
        string workingDirectory,
        IReadOnlyList<string>? extraRulesPaths = null
    )
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        var ruleSet = AnalysisRuleSet.LoadForSolution(anchor, extraRulesPaths);
        return ruleSet.ClassInheritanceEntryPoints.Select(Project).ToArray();
    }

    private static FactClassInheritanceRule Project(ClassInheritanceEntryPointRule rule)
    {
        // Same attribute-prefix projection as the action rules: FQN attribute type names become
        // "M:<FQN>." DocID prefixes. Short names can't be matched without a receiver type, so we
        // only emit prefixes for entries containing a '.'.
        var attributePrefixes = (rule.HandlerMethodAttributes ?? []).Where(a => a.Contains('.')).Select(a => $"M:{a}.").ToArray();

        // Reduce each expected parameter type to its simple name (last '.'-segment, generics stripped)
        // — fact signatures show minimally-qualified type names, so simple-name matching is what the
        // deriver can faithfully check (e.g. "Grpc.Core.ServerCallContext" -> "ServerCallContext").
        var paramTypeSimpleNames = (rule.HandlerParameterTypes ?? []).Select(SimpleTypeName).Where(n => n.Length > 0).ToArray();

        return new FactClassInheritanceRule(
            rule.Id,
            rule.Kind,
            rule.DefaultMethod ?? rule.Kind.ToUpperInvariant(),
            rule.BaseTypes,
            rule.HandlerMethods ?? [],
            rule.RequireOverride,
            attributePrefixes,
            paramTypeSimpleNames,
            rule.Requires
        );
    }

    private static FactEntryPointRule Project(TypeEntryPointRule rule)
    {
        // HandlerMethodAttributes in the JSON are short names or fully-qualified type names.
        // The reference_facts TargetSymbolId for an attribute ctor is the DocID, e.g.:
        //   "M:MMS.Web.UI.Attributes.ClientActionAttribute.#ctor"
        // We store the attribute type names as prefix matchers:
        //   "MMS.Web.UI.Attributes.ClientActionAttribute" -> match DocID starting with
        //   "M:MMS.Web.UI.Attributes.ClientActionAttribute."
        // Short names (e.g. "ClientAction") can't be reliably matched without a receiver type,
        // so we only emit prefix matchers for FQN entries that contain a '.'.
        var attributePrefixes = (rule.HandlerMethodAttributes ?? []).Where(a => a.Contains('.')).Select(a => $"M:{a}.").ToArray();

        return new FactEntryPointRule(
            rule.Id,
            rule.Kind,
            rule.DefaultMethod ?? rule.Kind.ToUpperInvariant(),
            rule.BaseTypes,
            rule.NamespacePrefix,
            attributePrefixes,
            rule.Requires
        );
    }

    // "Grpc.Core.ServerCallContext" -> "ServerCallContext"; "System.Collections.Generic.List<T>" -> "List".
    private static string SimpleTypeName(string type)
    {
        var generic = type.IndexOf('<');
        if (generic >= 0)
        {
            type = type.Substring(0, generic);
        }

        var lastDot = type.LastIndexOf('.');
        return (lastDot >= 0 ? type.Substring(lastDot + 1) : type).Trim();
    }
}

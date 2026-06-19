using Rig.Domain.Data;

namespace Rig.Analysis.Rules;

// The effective rule set for a working directory: the whole cascade (built-in + global ~/.rig + local
// rig.rules.json + --rules) merged ONCE and projected to every fact-matchable rule collection the query
// side consumes. Immutable — load it once at the top of a command and read the slices off it, instead of
// each Fact*RuleProvider re-running LoadForSolution. The merge itself (a union of rule sections, with the
// emoji map last-write-wins; see AnalysisRuleSet) is unchanged; this only collapses N identical loads to one.
//
// Within a single CLI invocation the filesystem is a fixed snapshot, so one load is correct for the whole
// run — there is nothing to invalidate. (Indexing uses its own load against the real solution path +
// per-project rules; that is a genuinely different rule set and is not replaced by this.)
public sealed record RuleSet(
    IReadOnlyList<FactHandoffRule> Handoff,
    IReadOnlyList<FactGenericFactoryRule> Factory,
    IReadOnlyList<FactTraversalCutRule> Cut,
    IReadOnlyList<FactContextDispatchRule> Context,
    IReadOnlyList<FactEffectRule> Effects,
    FactObservationRules Observations,
    IReadOnlyList<FactEntryPointRule> EntryPoints,
    IReadOnlyList<FactClassInheritanceRule> ClassInheritance,
    FactRenderRules Render,
    IReadOnlyDictionary<string, string> EffectEmoji
)
{
    // One LoadForSolution over the cascade, projected to every slice. The single seam the query commands
    // load rules through.
    public static RuleSet Load(string workingDirectory, IReadOnlyList<string>? extraRules = null)
    {
        var anchor = Path.Combine(workingDirectory, "_factrules_.slnx");
        var merged = AnalysisRuleSet.LoadForSolution(anchor, extraRules);
        return new RuleSet(
            Handoff: FactHandoffRuleProvider.Project(merged),
            Factory: FactGenericFactoryRuleProvider.Project(merged),
            Cut: FactTraversalCutRuleProvider.Project(merged),
            Context: FactContextDispatchRuleProvider.Project(merged),
            Effects: FactEffectRuleProvider.Project(merged),
            Observations: FactObservationRuleProvider.Project(merged),
            EntryPoints: FactEntryPointRuleProvider.ProjectTypeEntryPoints(merged),
            ClassInheritance: FactEntryPointRuleProvider.ProjectClassInheritance(merged),
            Render: FactRenderRuleProvider.Project(merged),
            // The emoji map is already a finished dictionary on the merged set — no projection, no separate
            // loader. FactEffectEmojiProvider keeps only the For(...) lookup helper.
            EffectEmoji: merged.EffectEmoji
        );
    }
}

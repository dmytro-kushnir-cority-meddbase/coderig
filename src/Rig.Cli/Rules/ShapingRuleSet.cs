using Rig.Analysis.Rules;
using Rig.Domain.Data;

namespace Rig.Cli.Rules;

// The graph-shaping rule bundle a traversal command applies: handoff classification (always loaded), plus
// generic-factory monomorphization + traversal-cut + context-dispatch narrowing (all bypassed by `--raw`).
// Collapses the four-way "raw ? Empty : FactXxxRuleProvider.Load…" quartet that path/reaches/tree/callers
// each copy-pasted into one load.
internal sealed record ShapingRuleSet(
    IReadOnlyList<FactHandoffRule> Handoff,
    IReadOnlyList<FactGenericFactoryRule> Factory,
    IReadOnlyList<FactTraversalCutRule> Cut,
    IReadOnlyList<FactContextDispatchRule> Context
)
{
    // path/tree/callers load exactly this. `reaches` differs only in loading Factory UNGATED (it
    // monomorphizes generic factories even under --raw — a deliberate, long-standing asymmetry), so it
    // overrides Factory via `with` after calling Load. Handoff is always loaded (it carries the
    // classification baked into the bounded graph); --raw zeroes the other three.
    internal static ShapingRuleSet Load(string workingDirectory, IReadOnlyList<string> extraRules, bool raw) =>
        new(
            FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules),
            raw ? [] : FactGenericFactoryRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules),
            raw ? [] : FactTraversalCutRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules),
            raw ? [] : FactContextDispatchRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules)
        );
}

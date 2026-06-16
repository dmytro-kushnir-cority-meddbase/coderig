using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;

namespace Rig.Cli.Effects;

// The stage-2 effect derivation + the --only/--exclude filter, shared by reaches/tree/derive. Each loads
// the effect + observation rules for the working directory and runs FactEffectDeriver.Derive over its
// (bounded or whole-store) inputs; this collapses that identical rule-load-and-derive block into one call.
internal static class EffectDerivation
{
    // Derive effects from the supplied invocation/ctor/throw inputs under the working directory's effect
    // + observation rules. The inputs differ per caller — reaches/tree pass the bounded closure's rows
    // (with base edges from the loaded graph), derive passes the whole-store rows (base edges from the
    // EP fact data) — but the rule load and the Derive call are identical, so they live here.
    internal static IReadOnlyList<DerivedEffect> DeriveEffects(
        string workingDirectory,
        IReadOnlyList<string> extraRules,
        IReadOnlyList<FactInvocation> invocations,
        IReadOnlyList<(string, string)> baseEdges,
        IReadOnlyList<SymbolRef> ctorRefs,
        IReadOnlyList<SymbolRef> throwRefs
    )
    {
        var effectRules = FactEffectRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var observationRules = FactObservationRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        return FactEffectDeriver.Derive(
            invocations,
            effectRules,
            providerFilter: null,
            baseEdges: baseEdges,
            ctorRefs: ctorRefs,
            observationRules: observationRules,
            throwRefs: throwRefs
        );
    }

    // Effect selection for reaches/tree/derive: --only keeps just the listed effects, --exclude drops
    // them (exclude wins on overlap). Tokens match an effect's `provider` (e.g. "throw") or the precise
    // `provider:operation` (e.g. "llblgen:read"). Returns the input unchanged when neither set is given.
    internal static IReadOnlyList<DerivedEffect> ApplyEffectFilters(
        IReadOnlyList<DerivedEffect> effects,
        HashSet<string> only,
        HashSet<string> exclude
    )
    {
        if (only.Count == 0 && exclude.Count == 0)
            return effects;
        return effects.Where(e => (only.Count == 0 || InSet(e, only)) && !InSet(e, exclude)).ToList();

        static bool InSet(DerivedEffect e, HashSet<string> set) => set.Contains(e.Provider) || set.Contains($"{e.Provider}:{e.Operation}");
    }
}

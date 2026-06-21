using Rig.Domain.Data;
using Rig.Domain.Functions;

namespace Rig.Cli.Effects;

// The stage-2 effect derivation + the --only/--exclude filter, shared by reaches/tree/derive/impact. Runs
// FactEffectDeriver.Derive over the caller's already-loaded effect + observation rules (from its RuleSet)
// and its (bounded or whole-store) invocation/ctor/throw inputs.
internal static class EffectDerivation
{
    internal static IReadOnlyList<DerivedEffect> DeriveEffects(
        IReadOnlyList<FactEffectRule> effectRules,
        FactObservationRules observationRules,
        IReadOnlyList<FactInvocation> invocations,
        IReadOnlyList<(string, string)> baseEdges,
        IReadOnlyList<SymbolRef> ctorRefs,
        IReadOnlyList<SymbolRef> throwRefs,
        // FR-1(b): static-field/auto-property write refs (whole-store; supplied by `derive`). The bounded
        // reaches/tree/impact closures do not yet bound these, so they default to none there (a follow-up).
        IReadOnlyList<SymbolRef>? staticFieldWriteRefs = null
    ) =>
        FactEffectDeriver.Derive(
            invocations,
            effectRules,
            providerFilter: null,
            baseEdges: baseEdges,
            ctorRefs: ctorRefs,
            observationRules: observationRules,
            throwRefs: throwRefs,
            staticFieldWriteRefs: staticFieldWriteRefs
        );

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
        {
            return effects;
        }

        return effects.Where(e => (only.Count == 0 || InSet(e, only)) && !InSet(e, exclude)).ToList();

        static bool InSet(DerivedEffect e, HashSet<string> set) => set.Contains(e.Provider) || set.Contains($"{e.Provider}:{e.Operation}");
    }
}

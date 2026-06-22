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
        IReadOnlyList<FactFieldAccess>? staticFieldWriteRefs = null,
        // FR-1 read arm: static-field/auto-property read refs (whole-store; supplied by `derive`), threaded
        // symmetrically to the write refs. Defaults to none for the bounded closures.
        IReadOnlyList<FactFieldAccess>? staticFieldReadRefs = null,
        // Hazard post-pass (race_window read-before-write matcher). Default OFF — like the other field-fed
        // signals it runs only on the whole-store `derive` path, not the bounded tree/reaches/impact closures
        // (which don't bound the static-field refs, so a read+write pair would be incomplete there anyway).
        bool deriveHazards = false,
        // [ThreadStatic] cell DocIDs (Reads.LoadThreadStaticFieldIdsAsync). A read→write on one is rerouted
        // from race_window to thread_local_context (thread-confined ⇒ not a race, but the FR-2 surface).
        // Null/empty leaves the legacy race_window classification unchanged.
        IReadOnlySet<string>? threadStaticCells = null
    )
    {
        var effects = FactEffectDeriver.Derive(
            invocations,
            effectRules,
            providerFilter: null,
            baseEdges: baseEdges,
            ctorRefs: ctorRefs,
            observationRules: observationRules,
            throwRefs: throwRefs,
            staticFieldWriteRefs: staticFieldWriteRefs,
            staticFieldReadRefs: staticFieldReadRefs
        );

        // Annotate qualifying effects with hazard observations — pure post-passes over the derived effects
        // that add observations and drop nothing:
        //   - race_window: a read-before-write of the same shared cell in one method (RMW / TOCTOU);
        //   - dual_write: durable writes to ≥2 distinct system classes in one method (FR-8, distributed
        //     consistency — DB + queue / search / cache / external HTTP with no atomicity).
        if (!deriveHazards)
        {
            return effects;
        }

        effects = FactHazardDeriver.DeriveRaceWindows(effects, threadStaticCells);
        effects = FactHazardDeriver.DeriveDualWrites(effects);
        return effects;
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
        {
            return effects;
        }

        return effects.Where(e => (only.Count == 0 || InSet(e, only)) && !InSet(e, exclude)).ToList();

        static bool InSet(DerivedEffect e, HashSet<string> set) => set.Contains(e.Provider) || set.Contains($"{e.Provider}:{e.Operation}");
    }
}

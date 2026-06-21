using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// Executable RCA corpus (docs/rca-corpus-meddbase.md). Each test reproduces a REAL production fix as a
// bug/fix pair and asserts what the SHIPPED detectors actually do on each — turning "rig would catch X"
// prose into a pass/fail. Tests named `_Gap_` pin a KNOWN blind spot (a bug the current detectors miss or
// mis-attribute); they are the development backlog made executable, and they guard against a future change
// silently claiming coverage it doesn't have.
public sealed class ProductionFixCorpusTests
{
    // §!10706 — fast-path import: non-atomic read-modify-write on a shared HashSet, fixed with Atom.Swap.
    // Pins the INVERSION the corpus calls out (proof (a)): the detector is BLIND to the buggy bare-collection
    // mutation and FIRES on the FIXED atomic code. A guard/triage tool must therefore never read "fires" as
    // "buggy" — exactly why the atomic tag exists and why FR-1d annotates rather than suppresses.
    [Test]
    public void _10706_atom_swap_bug_is_invisible_fix_fires_and_is_atomic()
    {
        var result = ProductionFixCorpus.Analyze(
            ProductionFixCorpus.LanguageExtStub
                + """
                namespace Importer
                {
                    public static class Cache
                    {
                        public static readonly System.Collections.Generic.HashSet<int> ForcedOff = new();
                        public static readonly LanguageExt.Atom<System.Collections.Generic.HashSet<int>> ForcedOffAtom =
                            LanguageExt.Atom.Create(new System.Collections.Generic.HashSet<int>());
                    }

                    public sealed class FieldEntityModel
                    {
                        // BUG: non-atomic RMW via a mutating method call on a shared bare HashSet.
                        public void MarkConflicts_Bug(int id) => Cache.ForcedOff.Add(id);

                        // FIX: atomic read-modify-write through Atom.Swap.
                        public void MarkConflicts_Fix(int id) => Cache.ForcedOffAtom.Swap(s => { s.Add(id); return s; });
                    }
                }
                """
        );

        // The buggy bare-HashSet mutation is NOT rule-expressible (a HashSet receiver could be local) -> the
        // detector is silent on the real defect. This is a documented blind spot, asserted so it can't change
        // unnoticed.
        result.SharedStateMutationsIn("MarkConflicts_Bug").ShouldBeEmpty();

        // The FIX fires shared_state:mutate (Atom receiver gate) and is tagged atomic (FR-1g), so annotate-only
        // triage shows it as an atomic-RMW hint rather than a bare candidate.
        var fix = result.SharedStateMutationsIn("MarkConflicts_Fix").ShouldHaveSingleItem();
        fix.ResourceType.ShouldContain("LanguageExt.Atom");
        fix.Atomic.ShouldBeTrue();
    }

    // §latent PatientsWithPriorFields — a two-field publish to STATIC fields on a shared cache (the sibling of
    // the !10706 cell, not yet shipped as a bug). FR-1(b): both static-field assignments derive
    // shared_state:mutate, keyed to the publishing method. Only works because the field-emission fix now emits
    // class field symbols (so the static-modifier gate has something to join to).
    [Test]
    public void latent_static_field_publish_fires_one_mutate_per_static_assignment()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace Importer
            {
                public static class Cache
                {
                    public static System.Collections.Generic.HashSet<int> PatientsWithPriorFields;
                    public static bool PatientsWithPriorFieldsLoaded;
                }

                public sealed class Loader
                {
                    // BUG (latent): non-atomic two-field publish to shared static state.
                    public void LoadPatientsWithPriorFields_Bug(System.Collections.Generic.HashSet<int> found)
                    {
                        Cache.PatientsWithPriorFields = found;
                        Cache.PatientsWithPriorFieldsLoaded = true;
                    }
                }
            }
            """
        );

        var mutations = result.SharedStateMutationsIn("LoadPatientsWithPriorFields_Bug");
        mutations.Count.ShouldBe(2); // both static-field assignments
        mutations.ShouldAllBe(e => e.ResourceType.Contains("Cache", System.StringComparison.Ordinal));
        // A plain `=` assignment is NOT atomic — annotate-only keeps these as bare candidates.
        mutations.ShouldAllBe(e => !e.Atomic);
    }

    // §#4246 — the #4246 fix guards a per-cell mutation with a lock, but the `lock` lives INSIDE the
    // RunTransaction wrapper while the mutation runs in the caller's callback. _Gap_: rig sees the lock effect
    // and the shared_state mutation in DIFFERENT enclosing methods, so a per-method guard check cannot connect
    // them — it would label the correctly-locked fix as "no guard on path". This is the concrete reason FR-1d
    // must annotate (over-flag), not subtract, until a guard-wrapper rule teaches rig this idiom.
    [Test]
    public void _4246_Gap_wrapper_guard_is_not_attributed_to_the_guarded_mutation()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace Importer
            {
                public static class SharedState
                {
                    public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Counters = new();
                }

                public static class Locks
                {
                    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Map = new();
                    public static object Get(string key) => Map.GetOrAdd(key, _ => new object());
                }

                public static class Tx
                {
                    // The #4246 guard: the lock statement is HERE, in the wrapper; the caller's work runs in the callback.
                    public static T RunTransaction<T>(object lockTarget, System.Func<T> computation)
                    {
                        lock (lockTarget) { return computation(); }
                    }
                }

                public sealed class Saver
                {
                    // FIX (#4246): the shared mutation runs inside RunTransaction(perCellLock, callback).
                    public int Save_Fixed(int id) =>
                        Tx.RunTransaction(Locks.Get("acc|" + id), () => { SharedState.Counters.TryAdd(id, id); return id; });
                }
            }
            """
        );

        // The guarded mutation IS detected (ConcurrentDictionary.TryAdd in the callback, enclosed by Save_Fixed).
        result.SharedStateMutationsIn("Save_Fixed").ShouldNotBeEmpty();

        // But the lock effect is attributed to RunTransaction, NOT to Save_Fixed — so a per-method "is there a
        // guard here?" check sees the mutation as unguarded. THIS is the gap that forbids suppression.
        result.HasGuardEffectIn("Save_Fixed").ShouldBeFalse();
        result.HasGuardEffectIn("RunTransaction").ShouldBeTrue();
    }
}

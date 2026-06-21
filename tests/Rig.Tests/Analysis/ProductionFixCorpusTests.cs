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

    // §#2892 — Pathways 4000 queries/min: a per-iteration read with no cache (an N+1). FR-3 precursor: the
    // read effect inside a loop carries a `looped_effect` observation (rig sees "read in a loop"); the FIX
    // (read once, outside the loop) does not. Gap: no quantified per-EP query-count estimate yet (FR-3).
    [Test]
    public void _2892_n_plus_1_read_in_a_loop_carries_looped_effect_the_cached_fix_does_not()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace Pathways
            {
                public sealed class Interpreter
                {
                    // BUG (#2892): variable definitions read from the source PER ITERATION (missing cache) -> N+1.
                    public async System.Threading.Tasks.Task ReadVars_Bug(
                        System.Net.Http.HttpClient client,
                        System.Collections.Generic.IEnumerable<string> ids)
                    {
                        foreach (var id in ids)
                        {
                            await client.GetStringAsync("/var/" + id);
                        }
                    }

                    // FIX (#2892): read once, outside the loop (cache the definitions).
                    public async System.Threading.Tasks.Task ReadVars_Fix(System.Net.Http.HttpClient client)
                    {
                        await client.GetStringAsync("/vars/all");
                    }
                }
            }
            """
        );

        var bug = result.EffectsIn("ReadVars_Bug").Single(e => e.Provider == "http");
        bug.Observations.ShouldNotBeNull();
        bug.Observations!.ShouldContain(o => o.Type == "looped_effect");

        var fix = result.EffectsIn("ReadVars_Fix").Single(e => e.Provider == "http");
        (fix.Observations ?? []).ShouldNotContain(o => o.Type == "looped_effect");
    }

    // §#2930 — appointment status check-then-set across two users (a lost-update TOCTOU). FR-1(b) surfaces the
    // WRITE as a shared_state:mutate candidate (narrows where a reviewer looks). _Gap_: the check-then-set
    // COUPLING (the preceding read of the same cell) is not modeled — rig has no inter-effect dataflow, so the
    // candidate is a flag, never a TOCTOU verdict.
    [Test]
    public void _2930_check_then_set_surfaces_the_write_candidate_but_not_the_toctou_coupling()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace Appointments
            {
                public static class Shared
                {
                    public static int Status; // shared appointment status (stand-in for the DB row)
                }

                public sealed class StatusService
                {
                    // BUG (#2930): check-then-set on shared state with a stale read; two sessions race.
                    public void Arrive_Bug()
                    {
                        if (Shared.Status == 0) // read — the stale check (NOT modeled as coupled to the write)
                        {
                            Shared.Status = 1; // write — a static-field mutation (FR-1(b) surfaces this)
                        }
                    }
                }
            }
            """
        );

        // The write is surfaced as a shared-state mutation candidate...
        result.SharedStateMutationsIn("Arrive_Bug").ShouldNotBeEmpty();
        // ...but nothing couples it to the preceding read of the SAME cell — the TOCTOU itself is invisible.
    }

    // §#3024 / #1192 — duplicate inserts under parallelism. FR-1 ingredient 1 (mutation under a concurrency
    // region) IS detected: a shared_state mutation reached inside Parallel.ForEach carries a `parallel_fanout`
    // observation. Gap: no existence-check / uniqueness-guard analysis (ingredients 2-5), so it's a candidate.
    [Test]
    public void _3024_shared_mutation_under_parallel_fanout_carries_the_region_observation()
    {
        // Uses the FULLY-QUALIFIED System.Threading.Tasks.Parallel.ForEach to guard the FQN-matching fix:
        // fanout detection resolves the receiver TYPE, so the qualified form matches as readily as the
        // using-imported `Parallel.ForEach`.
        var result = ProductionFixCorpus.Analyze(
            """
            namespace Imports
            {
                public static class Seen
                {
                    public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Keys = new();
                }

                public sealed class Importer
                {
                    // BUG (#3024/#1192): inserts reachable under a parallel region — duplicates when flows interleave.
                    public void Insert_Bug(System.Collections.Generic.IEnumerable<int> ids) =>
                        System.Threading.Tasks.Parallel.ForEach(ids, id => Seen.Keys.TryAdd(id, id));
                }
            }
            """
        );

        var mutate = result.SharedStateMutationsIn("Insert_Bug");
        mutate.ShouldNotBeEmpty();
        mutate.ShouldContain(e => e.Observations != null && e.Observations.Any(o => o.Type == "parallel_fanout"));
    }

    // _Gap_ — the field-write arm carries NO structural context (its input is a plain SymbolRef with no
    // enclosing-invocations), so a STATIC-FIELD publish reached under Parallel.ForEach fires
    // shared_state:mutate but gets NO `parallel_fanout` observation. That means FR-1's region-join currently
    // MISSES the !10706 / latent-publish family (a field publish under fan-out) — the highest-value shape.
    // Pinned so that wiring structural context into the field-write arm flips this test and proves the fix.
    [Test]
    public void field_publish_under_fanout_Gap_fires_mutate_but_misses_the_region_observation()
    {
        // Uses the idiomatic `Parallel.ForEach` so the missing parallel_fanout is attributable to the
        // field-write arm dropping structural context — NOT to the receiver-text artifact.
        var result = ProductionFixCorpus.Analyze(
            """
            using System.Threading.Tasks;
            namespace Imports
            {
                public static class Cache
                {
                    public static int LastId; // shared static cell published under fan-out
                }

                public sealed class Publisher
                {
                    public void Publish_Bug(System.Collections.Generic.IEnumerable<int> ids) =>
                        Parallel.ForEach(ids, id => { Cache.LastId = id; });
                }
            }
            """
        );

        var mutate = result.SharedStateMutationsIn("Publish_Bug");
        mutate.ShouldNotBeEmpty(); // FR-1(b) sees the static-field publish
        // ...but the field-write arm drops structural context, so the under-fanout region is NOT observed
        // even with the idiomatic receiver — proving the gap is the arm, not the syntactic match.
        mutate.ShouldAllBe(e => e.Observations == null || e.Observations.All(o => o.Type != "parallel_fanout"));
    }
}

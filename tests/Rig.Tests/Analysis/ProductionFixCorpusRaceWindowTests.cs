using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// Corpus tests for the FIRST hazard: `race_window` — a read-modify-write / TOCTOU candidate (RCA #2930:
// two users read appointment status, both write, one clobbers the other). The matcher pairs a
// shared_state:read with a same-cell shared_state:mutate at a LATER line in the SAME method and attaches a
// race_window observation to the mutate. These fixtures use a static field as the stand-in for the shared
// row (the FR-1 static-field arms are what emit the read/mutate legs).
//
// Cell precision: the field rules use resource:"field_slot" (slot-precise — the F:Ns.Type.Field DocID),
// so read TypeX.A and write TypeX.B do NOT falsely pair. Pairing is on the exact field slot.
public sealed class ProductionFixCorpusRaceWindowTests
{
    // BUG: `if (Cache.Status == 0) Cache.Status = 1;` — a read THEN a write of the SAME static field with no
    // isolation. The mutate gets a race_window: high confidence, rmw_no_isolation_on_path, naming the read site.
    [Test]
    public void Read_before_write_of_the_same_static_cell_with_no_transaction_emits_a_high_confidence_race_window()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public static class Cache
                {
                    public static int Status;
                }

                public static class Booking
                {
                    public static void ClaimSlot()
                    {
                        if (Cache.Status == 0)
                        {
                            Cache.Status = 1;
                        }
                    }
                }
            }
            """
        );

        var races = result.RaceWindowsIn("Booking.ClaimSlot");
        races.Count.ShouldBe(1);

        var race = races[0];
        race.Type.ShouldBe("race_window");
        // The cell is the slot-precise field DocID, shared by the paired read and write.
        race.Context.ShouldBe("F:App.Cache.Status");
        race.Confidence.ShouldBe("high");
        race.Reason.ShouldBe("rmw_no_isolation_on_path");
        // Detail names the paired READ's site (file:line) — the check that opened the window.
        race.Detail.ShouldStartWith("Corpus.cs:");
        // Mutate-existing shape (non-init method + non-init cell) — NOT classified as lazy init.
        result.LazyInitRacesIn("Booking.ClaimSlot").ShouldBeEmpty();
    }

    // FIX (DISCLOSED, NOT suppressed): the same read+write inside a using(var tx = ...) transaction scope.
    // A transaction is NOT a guarantee against lost updates under read-committed, so the race_window is STILL
    // emitted — but downgraded to medium confidence / rmw_in_transaction_verify_isolation. Assert it is still
    // present (we do not hide it) AND tiered.
    [Test]
    public void Read_before_write_inside_a_transaction_still_emits_a_race_window_but_disclosed_at_lower_confidence()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            using System;

            namespace App
            {
                public sealed class FakeTransaction : IDisposable
                {
                    public static FakeTransaction New() => new FakeTransaction();
                    public void Commit() { }
                    public void Dispose() { }
                }

                public static class Cache
                {
                    public static int Status;
                }

                public static class BookingTx
                {
                    public static void ClaimSlotInTransaction()
                    {
                        using (var transaction = FakeTransaction.New())
                        {
                            if (Cache.Status == 0)
                            {
                                Cache.Status = 1;
                            }
                            transaction.Commit();
                        }
                    }
                }
            }
            """
        );

        // Sanity: both legs are bracketed by the transaction (the precondition for the tiering downgrade).
        result
            .SharedStateReadsIn("BookingTx.ClaimSlotInTransaction")
            .ShouldHaveSingleItem()
            .Observations!.ShouldContain(o => o.Type == "transaction_spans_effect");
        result
            .SharedStateMutationsIn("BookingTx.ClaimSlotInTransaction")
            .ShouldHaveSingleItem()
            .Observations!.ShouldContain(o => o.Type == "transaction_spans_effect");

        var races = result.RaceWindowsIn("BookingTx.ClaimSlotInTransaction");
        // STILL emitted — the transaction does not suppress the finding.
        races.Count.ShouldBe(1);

        var race = races[0];
        race.Type.ShouldBe("race_window");
        race.Context.ShouldBe("F:App.Cache.Status");
        // Disclosed, not hidden: downgraded tier.
        race.Confidence.ShouldBe("medium");
        race.Reason.ShouldBe("rmw_in_transaction_verify_isolation");
        race.Detail.ShouldStartWith("Corpus.cs:");
    }

    // NEGATIVE: a method that ONLY WRITES the field (no preceding read) — no read leg, so NO race_window.
    [Test]
    public void Write_only_of_a_static_cell_emits_no_race_window()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public static class Cache
                {
                    public static int Status;
                }

                public static class Resetter
                {
                    public static void Reset()
                    {
                        Cache.Status = 0;
                    }
                }
            }
            """
        );

        // The mutate still fires (we suppress nothing), but it carries no race_window.
        result.SharedStateMutationsIn("Resetter.Reset").Count.ShouldBe(1);
        result.RaceWindowsIn("Resetter.Reset").ShouldBeEmpty();
    }

    // SPLIT — lazy-init shape: a property getter doing `if (_thing == null) _thing = new Thing();` (a single
    // read then a single write of the same static field, getter context). This is the do-once / lazy-singleton
    // archetype, NOT a clobber of already-valued shared state, so it is classified as a low-confidence,
    // heuristic-disclosed lazy_init_race — NOT a race_window. Annotate-only: the mutate still fires.
    [Test]
    public void Lazy_init_shape_in_a_getter_is_classified_as_lazy_init_race_not_race_window()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public sealed class Thing { }

                public static class Lazy
                {
                    private static Thing _instance;

                    public static Thing Instance
                    {
                        get
                        {
                            if (_instance == null)
                            {
                                _instance = new Thing();
                            }

                            return _instance;
                        }
                    }
                }
            }
            """
        );

        // The mutate still fires (nothing suppressed).
        result.SharedStateMutationsIn("Lazy.get_Instance").Count.ShouldBe(1);

        // Classified as lazy_init_race (low, heuristic) — NOT race_window.
        result.RaceWindowsIn("Lazy.get_Instance").ShouldBeEmpty();
        var lazies = result.LazyInitRacesIn("Lazy.get_Instance");
        lazies.Count.ShouldBe(1);

        var lazy = lazies[0];
        lazy.Type.ShouldBe("lazy_init_race");
        lazy.Context.ShouldBe("F:App.Lazy._instance");
        lazy.Confidence.ShouldBe("low");
        lazy.Reason.ShouldBe("lazy_init_heuristic");
        lazy.Detail.ShouldStartWith("Corpus.cs:");
    }

    // SPLIT — do-once init FLAG in an Init-named method: `if (!_initialised) _initialised = true;` (single
    // read, single write, init-shaped method name). Also a lazy_init_race, not a race_window.
    [Test]
    public void Do_once_init_flag_in_an_init_method_is_classified_as_lazy_init_race()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public static class Startup
                {
                    private static bool _initialised;

                    public static void Initialise()
                    {
                        if (!_initialised)
                        {
                            _initialised = true;
                        }
                    }
                }
            }
            """
        );

        result.SharedStateMutationsIn("Startup.Initialise").Count.ShouldBe(1);
        result.RaceWindowsIn("Startup.Initialise").ShouldBeEmpty();

        var lazies = result.LazyInitRacesIn("Startup.Initialise");
        lazies.Count.ShouldBe(1);
        lazies[0].Confidence.ShouldBe("low");
        lazies[0].Reason.ShouldBe("lazy_init_heuristic");
    }

    // SPLIT — mutate-existing COUNTER: `Cache.N = Cache.N + 1;` read-then-write of an already-valued cell in a
    // plain (non-init) method. The cell name (`N`) and method (`Bump`) carry no init signal, so this stays a
    // high-confidence race_window — the residual the split is meant to preserve.
    [Test]
    public void Mutate_existing_counter_stays_a_race_window_not_lazy_init()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public static class Cache
                {
                    public static int N;
                }

                public static class Counter
                {
                    public static void Bump()
                    {
                        if (Cache.N >= 0)
                        {
                            Cache.N = 1;
                        }
                    }
                }
            }
            """
        );

        result.SharedStateMutationsIn("Counter.Bump").Count.ShouldBe(1);
        result.LazyInitRacesIn("Counter.Bump").ShouldBeEmpty();

        var races = result.RaceWindowsIn("Counter.Bump");
        races.Count.ShouldBe(1);
        races[0].Type.ShouldBe("race_window");
        races[0].Confidence.ShouldBe("high");
    }

    // TASK A guard: a READ of a static READONLY cell is immutable ⇒ cannot be a TOCTOU check ⇒ the read arm
    // must not emit it. The write to a separate mutable cell still fires but pairs with no read → no finding.
    [Test]
    public void Read_of_a_static_readonly_field_emits_no_shared_state_read()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public static class Config
                {
                    public static readonly int Max = 10;
                    public static int Used;
                }

                public static class Allocator
                {
                    public static void Take()
                    {
                        if (Used < Config.Max)
                        {
                            Used = Config.Max;
                        }
                    }
                }
            }
            """
        );

        // The readonly cell (Config.Max) produces NO shared_state:read (excluded by the readonly gate); only
        // the mutable Used read remains.
        var reads = result.SharedStateReadsIn("Allocator.Take");
        reads.ShouldAllBe(r => r.ResourceType == "F:App.Config.Used");
        reads.ShouldNotContain(r => r.ResourceType == "F:App.Config.Max");
    }
}

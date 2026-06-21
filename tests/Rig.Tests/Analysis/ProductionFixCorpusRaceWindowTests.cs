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
}

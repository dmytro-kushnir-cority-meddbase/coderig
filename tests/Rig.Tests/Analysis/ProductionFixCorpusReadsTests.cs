using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// Foundational fact-presence tests for the FR-1 read arm (shared_state:read) — the symmetric twin of the
// field-write arm (shared_state:mutate). This arm is infrastructure for a later read-before-write
// TOCTOU/lost-update detector, so these assert the RAW MATERIAL (which effects fire on a read / write / both)
// rather than a bug-vs-fix verdict. Mirrors the ProductionFixCorpus harness used by ProductionFixCorpusTests
// but owns its own file (does not touch the existing corpus suite).
public sealed class ProductionFixCorpusReadsTests
{
    // A read of a STATIC field (`= Cache.Status`) derives exactly one shared_state:read effect keyed to the
    // reading method, resource naming the cell's declaring type (resource:"declaring_type").
    [Test]
    public void Read_of_a_static_field_derives_a_shared_state_read_effect()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public static class Cache
                {
                    public static int Status;
                }

                public static class Reader
                {
                    public static int ReadStatus()
                    {
                        return Cache.Status;
                    }
                }
            }
            """
        );

        var reads = result.SharedStateReadsIn("Reader.ReadStatus");
        reads.Count.ShouldBe(1);

        var read = reads[0];
        read.Provider.ShouldBe("shared_state");
        read.Operation.ShouldBe("read");
        read.ResourceType.ShouldBe("App.Cache");
        read.EnclosingSymbolId.ShouldNotBeNull().ShouldContain("Reader.ReadStatus");
        // A read is never an atomic read-modify-write (the rule leaves Atomic false).
        read.Atomic.ShouldBeFalse();
    }

    // A method that READS then WRITES the SAME static field derives BOTH a shared_state:read AND a
    // shared_state:mutate on that cell, both enclosed by the method. This is the raw material for a later
    // read-before-write TOCTOU candidate — assert both halves are present on the same cell.
    [Test]
    public void Read_then_write_of_the_same_static_field_derives_both_effects()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public static class Cache
                {
                    public static int Status;
                }

                public static class Checker
                {
                    public static void CheckThenAct()
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

        var reads = result.SharedStateReadsIn("Checker.CheckThenAct");
        var mutations = result.SharedStateMutationsIn("Checker.CheckThenAct");

        reads.Count.ShouldBe(1);
        mutations.Count.ShouldBe(1);

        // Both halves name the same shared cell's declaring type and are enclosed by the same method.
        reads[0].ResourceType.ShouldBe("App.Cache");
        mutations[0].ResourceType.ShouldBe("App.Cache");
        reads[0].EnclosingSymbolId.ShouldNotBeNull().ShouldContain("Checker.CheckThenAct");
        mutations[0].EnclosingSymbolId.ShouldNotBeNull().ShouldContain("Checker.CheckThenAct");
    }

    // A method that ONLY WRITES the field derives a shared_state:mutate but NO shared_state:read — the read
    // arm does not over-fire on a write.
    [Test]
    public void Write_only_of_a_static_field_derives_no_read_effect()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public static class Cache
                {
                    public static int Status;
                }

                public static class Writer
                {
                    public static void Reset()
                    {
                        Cache.Status = 0;
                    }
                }
            }
            """
        );

        result.SharedStateMutationsIn("Writer.Reset").Count.ShouldBe(1);
        result.SharedStateReadsIn("Writer.Reset").ShouldBeEmpty();
    }
}

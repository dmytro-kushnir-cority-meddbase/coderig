using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// Corpus tests for the FR-8 hazard: `dual_write` — a distributed-consistency hazard where ONE enclosing
// method performs durable writes to ≥2 DIFFERENT system classes (DB + queue, DB + search, DB + cache, DB +
// external HTTP, …) in a single unit of work. If the second write fails after the first commits, the systems
// diverge with no atomicity; the classic mitigation is an outbox/inbox/CDC. The matcher (FactHazardDeriver
// .DeriveDualWrites) maps each method's durable WRITE effects to a system class via the DefaultSystemClassMap
// and, when ≥2 distinct classes co-occur, attaches ONE dual_write observation (medium, naming the systems) to
// a representative write effect — annotate-only, suppressing nothing.
//
// Reliable surfaces here: System.Net.Http.HttpClient.PostAsync (http:POST, real framework ref) and a stubbed
// type whose name ends with "ObjectStore" with a Save method (object_store:write, per the builtin rule). The
// two are DISTINCT system classes ⇒ a dual_write; two http writes are NOT.
public sealed class ProductionFixCorpusDualWriteTests
{
    // BUG: one method writes to TWO distinct durable systems — an object-store write (object_store) AND an
    // HTTP POST (http) — with no atomicity between them. ONE dual_write finding, medium, naming both systems
    // (sorted set "http+object_store"), disclosing it did not check for an outbox. The IObjectStore stub's
    // generic Store<T> matches the builtin object_store:write rule (methods include Store,
    // declaringTypeNameEndsWith "ObjectStore", resource "type_argument"), mirroring the serialization fixture.
    [Test]
    public void Write_to_two_distinct_systems_in_one_method_emits_one_dual_write_naming_both()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            using System.Net.Http;
            using System.Threading.Tasks;

            namespace App
            {
                public interface IObjectStore
                {
                    void Store<T>(T value);
                }

                public sealed class OrderService
                {
                    public async Task PlaceOrder(HttpClient http, IObjectStore store)
                    {
                        store.Store(new object());
                        await http.PostAsync("https://payments/charge", null);
                    }
                }
            }
            """
        );

        var dualWrites = result.DualWritesIn("OrderService.PlaceOrder");
        dualWrites.Count.ShouldBe(1);

        var dw = dualWrites[0];
        dw.Type.ShouldBe("dual_write");
        // Context is the sorted '+'-joined distinct system set.
        dw.Context.ShouldBe("http+object_store");
        dw.Confidence.ShouldBe("medium");
        dw.Reason.ShouldBe("dual_write_no_outbox_checked");
        // Detail names the write SITES (file:line of each leg).
        dw.Detail.ShouldContain("Corpus.cs:");
    }

    // NEGATIVE: two writes to the SAME system class (two HTTP mutations — POST + DELETE, both http) is NOT a
    // dual write. The writes still fire (nothing suppressed); no dual_write observation.
    [Test]
    public void Two_writes_to_the_same_system_emit_no_dual_write()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            using System.Net.Http;
            using System.Threading.Tasks;

            namespace App
            {
                public sealed class HttpOnlyService
                {
                    public async Task SyncTwice(HttpClient http)
                    {
                        await http.PostAsync("https://a/upsert", null);
                        await http.DeleteAsync("https://a/stale");
                    }
                }
            }
            """
        );

        // Both http writes are present (suppress nothing) but they share one system class ⇒ no dual_write.
        result.EffectsIn("HttpOnlyService.SyncTwice").Count(e => e.Provider == "http").ShouldBe(2);
        result.DualWritesIn("HttpOnlyService.SyncTwice").ShouldBeEmpty();
    }

    // NEGATIVE: a single durable write (one HTTP POST) is, by definition, not a dual write.
    [Test]
    public void A_single_write_emits_no_dual_write()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            using System.Net.Http;
            using System.Threading.Tasks;

            namespace App
            {
                public sealed class SingleWriteService
                {
                    public async Task ChargeOnce(HttpClient http)
                    {
                        await http.PostAsync("https://payments/charge", null);
                    }
                }
            }
            """
        );

        result.EffectsIn("SingleWriteService.ChargeOnce").Count(e => e.Provider == "http").ShouldBe(1);
        result.DualWritesIn("SingleWriteService.ChargeOnce").ShouldBeEmpty();
    }
}

using Rig.Cli.Effects;
using Rig.Domain.Data;
using Shouldly;

namespace Rig.Tests;

// FR-1 read-arm WRITE-PAIRING GATE (EffectDerivation.DeriveEffects, gate param — on by default). A
// shared_state:read effect on a static field is pure inventory unless its cell ALSO appears as a static-field
// WRITE somewhere (only then can it pair with the write for the race_window TOCTOU hazard). The gate (default
// ON) drops reads of never-written cells (const/enum/other immutable statics); --no-gate (gate: false) emits
// every read, preserving the legacy behaviour.
//
// Mirrors the FactFieldAccess + MatchFieldWrite construction in
// FactDerivationTests.Write_to_a_static_field_derives_a_shared_state_mutate_effect, adding the symmetric
// MatchFieldRead arm. Pure — no store needed; the gate lives in the pure derivation layer.
public sealed class SharedStateReadGateTests
{
    // The static-field READ rule: emits shared_state:read for any static-field read ref it is handed.
    private static FactEffectRule ReadRule() =>
        new(
            Provider: "shared_state",
            Operation: "read",
            Methods: [],
            DeclaringTypes: [],
            ReceiverTypes: [],
            MatchFieldRead: true,
            Resource: "field"
        );

    // The static-field WRITE rule: emits shared_state:mutate for any static-field write ref it is handed.
    private static FactEffectRule WriteRule() =>
        new(
            Provider: "shared_state",
            Operation: "mutate",
            Methods: [],
            DeclaringTypes: [],
            ReceiverTypes: [],
            MatchFieldWrite: true,
            Resource: "field"
        );

    // Empty observation rules — the gate is independent of structural-observation derivation.
    private static FactObservationRules EmptyObservations() => new([], [], [], [], [], []);

    // The PAIRED cell: read AND written somewhere → survives the gate.
    private const string PairedCell = "F:App.GlobalCache.SharedCounter";

    // The UNPAIRED cell: read but NEVER written (the const/enum inventory-noise shape) → dropped by the gate.
    private const string UnpairedCell = "F:App.Constants.MaxRetries";

    private static FactFieldAccess Read(string target) =>
        new(Target: target, Enclosing: "M:App.Reader.Check", FilePath: "Reader.cs", Line: 7);

    private static FactFieldAccess Write(string target) =>
        new(Target: target, Enclosing: "M:App.Writer.Run", FilePath: "Writer.cs", Line: 11);

    private static IReadOnlyList<DerivedEffect> Derive(bool gate)
    {
        // Two reads (one paired, one unpaired) and a single write — of the PAIRED cell only.
        var reads = new[] { Read(PairedCell), Read(UnpairedCell) };
        var writes = new[] { Write(PairedCell) };

        return EffectDerivation.DeriveEffects(
            effectRules: [ReadRule(), WriteRule()],
            observationRules: EmptyObservations(),
            invocations: [],
            baseEdges: [],
            ctorRefs: [],
            throwRefs: [],
            staticFieldWriteRefs: writes,
            staticFieldReadRefs: reads,
            gate: gate
        );
    }

    // Gate ON (default): the read of the WRITTEN cell survives; the read of the never-written cell is dropped.
    [Test]
    public void Gate_on_keeps_only_the_write_paired_read()
    {
        var reads = Derive(gate: true).Where(e => e is { Provider: "shared_state", Operation: "read" }).ToList();

        reads.Count.ShouldBe(1);
        reads[0].ResourceType.ShouldBe(PairedCell);
        reads.ShouldNotContain(e => e.ResourceType == UnpairedCell);
    }

    // Gate OFF (--no-gate): BOTH reads are emitted — the legacy ungated behaviour is preserved.
    [Test]
    public void Gate_off_emits_every_static_field_read()
    {
        var reads = Derive(gate: false).Where(e => e is { Provider: "shared_state", Operation: "read" }).ToList();

        reads.Count.ShouldBe(2);
        reads.Select(e => e.ResourceType).ShouldBe([PairedCell, UnpairedCell], ignoreOrder: true);
    }

    // The write effect is unaffected by the gate (the gate only pre-filters the read refs): the same
    // shared_state:mutate is emitted whether the gate is on or off.
    [Test]
    public void Gate_does_not_touch_the_write_effect()
    {
        foreach (var gate in new[] { true, false })
        {
            var writes = Derive(gate).Where(e => e is { Provider: "shared_state", Operation: "mutate" }).ToList();
            writes.Count.ShouldBe(1);
            writes[0].ResourceType.ShouldBe(PairedCell);
        }
    }
}

using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// FactHazardDeriver.DeriveSyncOverAsync — the sync-over-async hazard: a call to Task(<T>).Wait() or the
// .GetAwaiter().GetResult() idiom (mined as the `async_block` provider, builtin-rules.json) whose ENCLOSING
// method is itself declared `async`. Tier 1 structural co-occurrence: both facts (the blocking call, the
// enclosing method's `async` modifier) are already mined, no dataflow needed. Mirrors DeriveDualWrites'
// annotate-only / null-safe conventions: this is a hand-built-fixture unit test directly against the
// deriver (no corpus/Roslyn round-trip needed for a pure post-pass over DerivedEffect records).
public sealed class SyncOverAsyncHazardTests
{
    private const string AsyncMethod = "M:App.Service.FetchAsync";
    private const string SyncMethod = "M:App.Service.FetchSync";

    private static DerivedEffect BlockingWaitEffect(string enclosing) =>
        new(
            Provider: "async_block",
            Operation: "wait",
            ResourceType: "System.Threading.Tasks.Task",
            EnclosingSymbolId: enclosing,
            FilePath: "Service.cs",
            Line: 10
        );

    private static DerivedEffect GetResultEffect(string enclosing) =>
        new(
            Provider: "async_block",
            Operation: "get_result",
            ResourceType: "System.Runtime.CompilerServices.TaskAwaiter",
            EnclosingSymbolId: enclosing,
            FilePath: "Service.cs",
            Line: 14
        );

    private static DerivedEffect OtherProviderEffect(string enclosing) =>
        new(
            Provider: "db_command",
            Operation: "execute",
            ResourceType: "System.Data.Common.DbCommand",
            EnclosingSymbolId: enclosing,
            FilePath: "Service.cs",
            Line: 20
        );

    // (a) A blocking-wait effect whose enclosing method IS in asyncMethodIds -> the hazard fires.
    [Test]
    public void Blocking_wait_inside_an_async_enclosing_method_emits_sync_over_async()
    {
        var effect = BlockingWaitEffect(AsyncMethod);
        var asyncMethodIds = new HashSet<string>(StringComparer.Ordinal) { AsyncMethod };

        var result = FactHazardDeriver.DeriveSyncOverAsync([effect], asyncMethodIds);

        var written = result.Single();
        written.Observations.ShouldNotBeNull();
        var hazard = written.Observations!.ShouldHaveSingleItem();
        hazard.Type.ShouldBe(FactHazardDeriver.SyncOverAsyncType);
        hazard.Type.ShouldBe("sync_over_async");
        hazard.Confidence.ShouldBe("high");
        hazard.Basis.ShouldBe("fact_derived");
        hazard.Reason.ShouldBe("blocking_wait_inside_async_method");
    }

    // Same shape via the .GetAwaiter().GetResult() idiom (async_block:get_result) instead of Task.Wait().
    [Test]
    public void GetAwaiter_GetResult_inside_an_async_enclosing_method_emits_sync_over_async()
    {
        var effect = GetResultEffect(AsyncMethod);
        var asyncMethodIds = new HashSet<string>(StringComparer.Ordinal) { AsyncMethod };

        var result = FactHazardDeriver.DeriveSyncOverAsync([effect], asyncMethodIds);

        var written = result.Single();
        written.Observations.ShouldNotBeNull();
        written.Observations!.ShouldHaveSingleItem().Type.ShouldBe(FactHazardDeriver.SyncOverAsyncType);
    }

    // (b) The enclosing method is NOT in asyncMethodIds (a plain sync/async-boundary block, e.g. a
    // top-level Main()) -> no hazard. This is the common, sometimes-intentional pattern the detector must
    // NOT flag.
    [Test]
    public void Blocking_wait_inside_a_non_async_enclosing_method_emits_no_observation()
    {
        var effect = BlockingWaitEffect(SyncMethod);
        var asyncMethodIds = new HashSet<string>(StringComparer.Ordinal) { AsyncMethod }; // SyncMethod absent

        var result = FactHazardDeriver.DeriveSyncOverAsync([effect], asyncMethodIds);

        var written = result.Single();
        (written.Observations ?? []).ShouldBeEmpty();
    }

    // (c) A null asyncMethodIds set is a back-compat no-op (mirrors threadStaticCells/volatileCells).
    [Test]
    public void Null_asyncMethodIds_is_a_no_op()
    {
        var effect = BlockingWaitEffect(AsyncMethod);

        var result = FactHazardDeriver.DeriveSyncOverAsync([effect], asyncMethodIds: null);

        var written = result.Single();
        (written.Observations ?? []).ShouldBeEmpty();
    }

    // An empty (non-null) asyncMethodIds set is likewise a no-op.
    [Test]
    public void Empty_asyncMethodIds_is_a_no_op()
    {
        var effect = BlockingWaitEffect(AsyncMethod);

        var result = FactHazardDeriver.DeriveSyncOverAsync([effect], asyncMethodIds: new HashSet<string>(StringComparer.Ordinal));

        var written = result.Single();
        (written.Observations ?? []).ShouldBeEmpty();
    }

    // (d) A non-async_block-provider effect is never touched, even when its enclosing method IS async —
    // this detector must not annotate unrelated effects (e.g. a plain db_command:execute) just because they
    // happen to live in an async method.
    [Test]
    public void Non_async_block_provider_effect_is_never_touched_even_when_enclosing_is_async()
    {
        var effect = OtherProviderEffect(AsyncMethod);
        var asyncMethodIds = new HashSet<string>(StringComparer.Ordinal) { AsyncMethod };

        var result = FactHazardDeriver.DeriveSyncOverAsync([effect], asyncMethodIds);

        var written = result.Single();
        written.ShouldBe(effect);
        (written.Observations ?? []).ShouldBeEmpty();
    }

    // Annotate-only: an effect that already carries an unrelated observation keeps it AND gains the new one
    // (append, not replace) — mirrors DeriveDualWrites/DeriveRaceWindows' additive Observations convention.
    [Test]
    public void Existing_observations_are_preserved_when_sync_over_async_is_appended()
    {
        var existing = new EffectObservationInfo(
            Type: "looped_effect",
            Context: "loop",
            Detail: "for-loop",
            Confidence: "high",
            Basis: "compilation",
            Reason: "inside_loop"
        );
        var effect = BlockingWaitEffect(AsyncMethod) with { Observations = [existing] };
        var asyncMethodIds = new HashSet<string>(StringComparer.Ordinal) { AsyncMethod };

        var result = FactHazardDeriver.DeriveSyncOverAsync([effect], asyncMethodIds);

        var written = result.Single();
        written.Observations!.Count.ShouldBe(2);
        written.Observations!.ShouldContain(existing);
        written.Observations!.ShouldContain(o => o.Type == FactHazardDeriver.SyncOverAsyncType);
    }

    // HazardKinds.All must recognise sync_over_async as a hazard (promoted out of the generic Observations
    // block into the Hazards view), and HazardKinds.SyncOverAsync must be the SAME string the deriver emits
    // — the catalog-never-drifts-from-the-emitter invariant documented in HazardKinds.cs.
    [Test]
    public void HazardKinds_recognises_sync_over_async_and_matches_the_deriver_constant()
    {
        HazardKinds.SyncOverAsync.ShouldBe(FactHazardDeriver.SyncOverAsyncType);
        HazardKinds.IsHazard(FactHazardDeriver.SyncOverAsyncType).ShouldBeTrue();
        HazardKinds.All.ShouldContain(FactHazardDeriver.SyncOverAsyncType);
    }
}

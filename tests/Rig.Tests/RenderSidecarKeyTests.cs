using Shouldly;
using static Rig.Cli.Caching.QueryCacheKeys;

namespace Rig.Tests;

// Pure-function tests for RenderSidecarKey — the render-sidecar cache key derivation that pins the
// locations + seam slots off a forest TreeCacheKey. The seam key MUST namespace hazards (+gate) so a
// `tree --view hazards` run caches (instead of a permanent render-miss) without tainting a plain tree's
// seam, while the non-hazards key stays byte-identical to the legacy `TreeKey + ":seam:" + FilterSignature`.
public sealed class RenderSidecarKeyTests
{
    private static readonly ForestCacheKey K = new("FOREST_KEY_ABC123");
    private const string S = "only=db;exclude=";

    [Test]
    public void Hazards_namespaces_the_seam()
    {
        var withHazards = new RenderSidecarKey(K, S, Hazards: true, Gate: true).Seam();
        var withoutHazards = new RenderSidecarKey(K, S, Hazards: false, Gate: true).Seam();

        withHazards.ShouldNotBe(withoutHazards);
    }

    [Test]
    public void Gate_fragments_the_seam_only_under_hazards()
    {
        // Under --view hazards the seam derives from the gate-dependent hazard-augmented effect set, so the
        // gate MUST fragment the key.
        var hazardsGated = new RenderSidecarKey(K, S, Hazards: true, Gate: true).Seam();
        var hazardsUngated = new RenderSidecarKey(K, S, Hazards: true, Gate: false).Seam();
        hazardsGated.ShouldNotBe(hazardsUngated);

        // A plain tree has no gate-dependent effects, so the gate MUST NOT fragment the non-hazards key.
        var plainGated = new RenderSidecarKey(K, S, Hazards: false, Gate: true).Seam();
        var plainUngated = new RenderSidecarKey(K, S, Hazards: false, Gate: false).Seam();
        plainGated.ShouldBe(plainUngated);
    }

    [Test]
    public void Non_hazards_seam_is_byte_identical_to_the_legacy_key()
    {
        // Back-compat: existing plain-tree warm caches keyed `TreeKey + ":seam:" + FilterSignature` must
        // still hit after this change. Pin the legacy layout byte-for-byte.
        var seam = new RenderSidecarKey(K, S, Hazards: false, Gate: false).Seam();

        seam.ShouldBe(K.Value + ":seam:" + S);
    }

    [Test]
    public void FilterSignature_and_TreeKey_both_participate_in_the_seam()
    {
        var baseline = new RenderSidecarKey(K, S, Hazards: true, Gate: true).Seam();

        var differentFilter = new RenderSidecarKey(K, "only=http;exclude=", Hazards: true, Gate: true).Seam();
        differentFilter.ShouldNotBe(baseline);

        var differentTree = new RenderSidecarKey(new ForestCacheKey("OTHER_FOREST_KEY"), S, Hazards: true, Gate: true).Seam();
        differentTree.ShouldNotBe(baseline);
    }

    [Test]
    public void Locations_is_hazard_gate_and_filter_independent()
    {
        var expected = K.Value + ":loc";

        new RenderSidecarKey(K, S, Hazards: false, Gate: false).Locations().ShouldBe(expected);
        new RenderSidecarKey(K, S, Hazards: false, Gate: true).Locations().ShouldBe(expected);
        new RenderSidecarKey(K, S, Hazards: true, Gate: false).Locations().ShouldBe(expected);
        new RenderSidecarKey(K, S, Hazards: true, Gate: true).Locations().ShouldBe(expected);
        new RenderSidecarKey(K, "only=cache;exclude=db", Hazards: true, Gate: true).Locations().ShouldBe(expected);
    }
}

using Rig.Cli.Caching;
using Rig.Cli.Commands;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Cli;

// The GRAPH-TIER hazard wiring for `tree --view hazards`: cache_coherence / event_cycle / static_init_capture
// are WHOLE-STORE, NOT effect-attached, so they are derived once over the shaped graph + static-field universe
// and CACHED store+rules-keyed (GraphHazardFindingsCodec), then filtered to a tree's reachable methods —
// exactly like the effect-attached hazard-augmented effect set. These tests pin the part testable WITHOUT a
// store: the cache codec round-trips every HazardFinding field for the graph-tier types lossless, so a warm
// `tree --hazards` (a cache hit, NO graph load) renders byte-identically to a cold derive.
//
// The end-to-end inline ⚠ marks + tsv `hazard` rows on the real MedDBase store are an orchestrator check
// (needs a built rig + the .rig store): an EP reaching ContactEntity.RemovePersonContactLinks must show
// `⚠ cache_coherence(high)` inline (the mark format confirmed against live `rig tree --view hazards` output:
// existing graph-effect hazards render `⚠ lazy_init_race(low)` / `⚠ thread_local_context(low)`).
public sealed class TreeGraphHazardsTests
{
    private static DeriveCommand.HazardFinding Finding(
        string type,
        string confidence,
        string reason,
        string context,
        string detail,
        string enclosing,
        string filePath,
        int line
    ) =>
        new(
            Type: type,
            Confidence: confidence,
            Reason: reason,
            Context: context,
            Detail: detail,
            Enclosing: enclosing,
            FilePath: filePath,
            Line: line
        );

    [Test]
    public void Codec_round_trips_the_three_graph_tier_finding_types_lossless()
    {
        // One finding per graph-tier source, each carrying a DISTINCT value in every field — so a dropped or
        // swapped field is caught. cache_coherence: high, entity-key context, no detail. event_cycle: the
        // representative caller + the multi-edge detail string. static_init_capture: the frozen config key.
        var findings = new List<DeriveCommand.HazardFinding>
        {
            Finding(
                type: HazardKinds.CacheCoherence,
                confidence: "high",
                reason: "bulk_write_without_cache_invalidation",
                context: "Person",
                detail: "",
                enclosing: "M:App.Data.ContactEntity.RemovePersonContactLinks",
                filePath: "C:/repo/App/ContactEntity.cs",
                line: 80
            ),
            Finding(
                type: FactCycleDeriver.EventCycleType,
                confidence: "high",
                reason: "feedback_cycle_over_delivery_edges",
                context: "11 methods",
                detail: "M:App.A.ShowDialog->M:App.B.OnSelected@G.cs:72[event_raise]",
                enclosing: "M:App.A.ShowDialog",
                filePath: "G.cs",
                line: 72
            ),
            Finding(
                type: HazardKinds.StaticInitCapture,
                confidence: "medium",
                reason: "config_read_frozen_in_static_field_init",
                context: "P:App.Configuration.Settings.EnableFeature",
                detail: "",
                enclosing: "F:App.Pages.Edit.FeatureUI",
                filePath: "C:/repo/App/Edit.cs",
                line: 327
            ),
        };

        var decoded = GraphHazardFindingsCodec.Decode(GraphHazardFindingsCodec.Encode(findings));

        decoded.ShouldNotBeNull();
        decoded.Count.ShouldBe(3);
        // Order preserved (the union order derive + tree rely on), and every field equal.
        decoded.ShouldBe(findings);
    }

    [Test]
    public void Codec_round_trips_an_empty_finding_set()
    {
        // The opt-out case (no cacheCoherence/staticInitCapture rule section + no cycles): an empty set must
        // survive so a warm hit yields an empty list (not null → not a spurious miss → no re-derive).
        var decoded = GraphHazardFindingsCodec.Decode(GraphHazardFindingsCodec.Encode([]));

        decoded.ShouldNotBeNull();
        decoded.ShouldBeEmpty();
    }

    [Test]
    public void Decode_returns_null_on_corrupt_blob_so_it_is_treated_as_a_cache_miss()
    {
        // A non-gzip / truncated blob must decode to null (cache MISS → recompute), never throw — same
        // corruption contract as the other query-cache codecs.
        GraphHazardFindingsCodec.Decode([0x01, 0x02, 0x03]).ShouldBeNull();
    }
}

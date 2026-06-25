namespace Rig.Domain.Functions;

// The catalog of HAZARD finding types — the higher-order findings that match PATTERNS over effects (a
// read-modify-write window, an N+1 read in a loop, …), as distinct from STRUCTURAL observations
// (looped_effect / parallel_fanout / lock_held_across_effect / transaction_spans_effect) which are context
// facts, not hazards. Most are modeled as EffectObservationInfo notes on effects; the single GRAPH-tier
// hazard, event_cycle (FactCycleDeriver), is NOT effect-attached — it is a property of the call-graph
// topology (a feedback cycle closing through a publish→consumer delivery edge), derived over the graph and
// folded into the same Hazards view as a second source. This is the single place that answers "is this a
// hazard?" so the derive Hazards view, the generic-observations exclusion, and the tsv split don't each
// hard-code the list.
//
// The type strings are owned by their derivers (race_window / lazy_init_race by FactHazardDeriver;
// n_plus_1 / unserializable_payload by FactObservationDeriver) and re-stated here as the closed set — this
// catalog enumerates, it does not detect.
public static class HazardKinds
{
    // race_window / lazy_init_race come from FactHazardDeriver; reuse its constants so the catalog can never
    // drift from the emitter.
    public const string NPlusOne = "n_plus_1";
    public const string UnserializablePayload = "unserializable_payload";

    // cache_coherence is the cache-specific INSTANCE of the generic effect-correlation deriver
    // (FactCorrelationDeriver): a bulk_write anchor whose forward closure lacks a same-key cache:invalidate
    // companion. It has no single owning deriver class anymore (it is wired in DeriveCommand), so the type
    // string lives HERE — referenced by both the All set below and the DeriveCommand finding mapping.
    public const string CacheCoherence = "cache_coherence";

    // The closed set of hazard finding types. Membership test for "promote this observation into the Hazards
    // view (and drop it from the generic Observations block)".
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        FactHazardDeriver.RaceWindowType,
        FactHazardDeriver.LazyInitRaceType,
        FactHazardDeriver.ThreadLocalContextType,
        FactHazardDeriver.DualWriteType,
        FactCycleDeriver.EventCycleType,
        CacheCoherence,
        NPlusOne,
        UnserializablePayload,
    };

    // True when an observation TYPE is a hazard finding (vs. a structural context observation).
    public static bool IsHazard(string type) => All.Contains(type);
}

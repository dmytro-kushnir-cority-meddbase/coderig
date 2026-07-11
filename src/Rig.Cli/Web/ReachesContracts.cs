namespace Rig.Cli.Web;

// JSON contract for /api/reaches — the web equivalent of `rig reaches <from>`. Flat: the CLI's three
// buckets (direct/scheduled/dispatch-fanout) are folded into one effect inventory (see the type-level
// note on ReachesQueryService). Reuses EffectDto (WebContracts.cs) for the per-(provider,operation) rows
// so /api/tree and /api/reaches report effects in the identical shape.
internal sealed record ReachesResponseDto(
    string From,
    bool Matched,
    // Distinct methods reachable from `From` (unbounded depth — this endpoint doesn't expose --depth yet).
    int ReachableCount,
    IReadOnlyList<EffectDto> Effects
);
